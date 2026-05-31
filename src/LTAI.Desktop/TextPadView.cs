using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Search;

namespace LTAI.Desktop;

/// <summary>
/// 文件浏览器 + 编辑器（AvaloniaEdit + TreeSitter 语法检查）
/// </summary>
public sealed class TextPadView : UserControl
{
    /// <summary>触发时切换到聊天视图，传递上下文信息。</summary>
    public event Action<string>? AskAiRequested;
    private readonly TreeView _tree;
    private readonly TextEditor _editor;
    private readonly TextBlock _statusBar;
    private readonly StackPanel _editorPanel;
    private readonly Button _toggleBtn;
    private readonly ListBox _symbolList;
    private readonly TextBox _buildOutput;
    private readonly StackPanel _buildPanel;
    private readonly Button _buildBtn;
    private readonly Button _publishBtn;
    private readonly Button _testBtn;
    private readonly Button _runBtn;
    private string _rootDir;
    private string? _currentFile;
    private bool _isReadOnly = true;
    private string _projectType = "unknown";

    private const long MaxEditorSize = 50 * 1024 * 1024;

    private static readonly HashSet<string> CodeExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".py", ".js", ".ts", ".go", ".rs", ".java",
        ".jsx", ".tsx", ".css", ".html", ".sh", ".bash",
    };

    private static readonly HashSet<string> TextExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".py", ".js", ".ts", ".go", ".rs", ".java", ".sh", ".bash",
        ".md", ".txt", ".json", ".xml", ".yaml", ".yml", ".toml", ".ini",
        ".cfg", ".conf", ".css", ".html", ".htm", ".jsx", ".tsx", ".sln",
        ".csproj", ".props", ".targets", ".gitignore", ".env", ".editorconfig",
    };

    private FileSystemWatcher? _watcher;
    private string? _gitBranch;
    private Dictionary<string, string> _gitFileStatus = new();
    private readonly Button _gitCommitBtn;
    private readonly Button _gitPullBtn;
    private readonly Button _gitPushBtn;
    private readonly Button _gitBlameBtn;
    private readonly TextBlock _gitBranchLabel;
    private readonly ListBox _problemList;
    private readonly Terminal.TerminalView _terminalView;
    private readonly Button _terminalBtn;
    private FileSystemWatcher? _fileWatcher;
    private Dictionary<string, string>? _blameData;
    private sealed record SymbolItem(string Icon, string Name, int Line);

    public TextPadView(string? rootDir = null)
    {
        _rootDir = rootDir ?? Directory.GetCurrentDirectory();

        _tree = new TreeView
        {
            MinWidth = 250, MaxWidth = 400,
            Background = LtaiTheme.Sbb(LtaiTheme.Bg),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
        };
        _tree.SelectionChanged += OnTreeSelectionChanged;
        BuildTree(_tree.Items, _rootDir);

        _editor = new TextEditor
        {
            IsReadOnly = true, ShowLineNumbers = true,
            FontFamily = new FontFamily("Consolas"), FontSize = 13,
            Background = LtaiTheme.Sbb(LtaiTheme.Bg),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            LineNumbersForeground = LtaiTheme.Sbb(LtaiTheme.TextDim),
            WordWrap = false,
        };
        try { _editor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("C#"); } catch { }

        // 当前行高亮
        _editor.TextArea.TextView.CurrentLineBackground = LtaiTheme.Sbb(Color.Parse("#1a2332"));
        _editor.TextArea.TextView.CurrentLineBorder = new Pen(LtaiTheme.Sbb(Color.Parse("#2d3a4a")), 1);
        // 选中文本背景
        _editor.TextArea.SelectionBrush = LtaiTheme.Sbb(Color.Parse("#264f78"));
        _editor.TextArea.SelectionCornerRadius = 2;
        // 括号匹配（AvaloniaEdit 默认已启用）
        _editor.TextArea.SelectionBrush = LtaiTheme.Sbb(Color.Parse("#264f78"));
        _editor.TextArea.SelectionCornerRadius = 2;

        // URL 超链接检测（Ctrl+点击跳转）
        _editor.Options = new TextEditorOptions
        {
            EnableHyperlinks = true,
            EnableEmailHyperlinks = true,
            RequireControlModifierForHyperlinkClick = true,
        };
        _editor.TextArea.IndentationStrategy = new AvaloniaEdit.Indentation.DefaultIndentationStrategy();

        // 多语言代码折叠
        try
        {
            var foldingManager = FoldingManager.Install(_editor.TextArea);
            var foldingStrategy = new MultiLangFoldingStrategy();
            foldingStrategy.UpdateFoldings(foldingManager, _editor.Document);
            // 文档变更时更新折叠
            _editor.Document.TextChanged += (_, _) => foldingStrategy.UpdateFoldings(foldingManager, _editor.Document);
        }
        catch { /* folding not critical */ }

        // Ctrl+滚轮缩放
        _editor.PointerWheelChanged += (_, e) =>
        {
            if (e.KeyModifiers == KeyModifiers.Control)
            {
                var delta = e.Delta.Y > 0 ? 1 : -1;
                _editor.FontSize = Math.Clamp(_editor.FontSize + delta, 8, 36);
                e.Handled = true;
            }
        };

        _toggleBtn = new Button
        {
            Content = "🔓 编辑", FontSize = 11,
            Background = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
            Foreground = LtaiTheme.Sbb("#ffffff"), Margin = new(0, 0, 4, 0),
        };
        _toggleBtn.Click += (_, _) => ToggleEdit();

        var checkBtn = new Button
        {
            Content = "🔍 语法检查", FontSize = 11,
            Background = LtaiTheme.Sbb(LtaiTheme.AccentInfo),
            Foreground = LtaiTheme.Sbb("#ffffff"),
        };
        checkBtn.Click += (_, _) => RunSyntaxCheck();

        var saveBtn = new Button
        {
            Content = "💾 保存", FontSize = 11,
            Background = LtaiTheme.Sbb(LtaiTheme.AccentSystem),
            Foreground = LtaiTheme.Sbb("#ffffff"),
        };
        saveBtn.Click += (_, _) => SaveFile();

        _statusBar = new TextBlock
        {
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
            FontSize = 11, Margin = new(4, 2, 0, 0),
        };

        // ── 符号大纲列表 ──
        _symbolList = new ListBox
        {
            MinWidth = 180, MaxWidth = 250,
            Background = LtaiTheme.Sbb(LtaiTheme.Bg),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
            FontSize = 11,
            IsVisible = false,
        };
        _symbolList.SelectionChanged += (_, _) =>
        {
            if (_symbolList.SelectedItem is SymbolItem sym && _currentFile != null)
                _editor.TextArea.Caret.Line = sym.Line;
        };

        // ── 构建/测试/运行 面板（多语言支持） ──
        _buildOutput = new TextBox
        {
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Background = LtaiTheme.Sbb(LtaiTheme.Bg),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            MinHeight = 0, MaxHeight = 200,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };
        _buildBtn = new Button { Content = "🛠 Build", FontSize = 10, Height = 20, Background = LtaiTheme.Sbb(LtaiTheme.AccentDNA), Foreground = LtaiTheme.Sbb("#ffffff"), IsVisible = false };
        _buildBtn.Click += (_, _) => RunProjectCmd("build");
        _testBtn = new Button { Content = "🧪 Test", FontSize = 10, Height = 20, Background = LtaiTheme.Sbb(LtaiTheme.AccentInfo), Foreground = LtaiTheme.Sbb("#ffffff"), IsVisible = false };
        _testBtn.Click += (_, _) => RunProjectCmd("test");
        _publishBtn = new Button { Content = "🚀 Deploy", FontSize = 10, Height = 20, Background = LtaiTheme.Sbb(LtaiTheme.AccentSystem), Foreground = LtaiTheme.Sbb("#ffffff"), IsVisible = false };
        _publishBtn.Click += (_, _) => RunProjectCmd("publish");
        _runBtn = new Button { Content = "▶ Run", FontSize = 10, Height = 20, Background = LtaiTheme.Sbb(LtaiTheme.AccentInfo), Foreground = LtaiTheme.Sbb("#ffffff"), IsVisible = false };
        _runBtn.Click += (_, _) => RunProjectCmd("run");

        var formatBtn = new Button { Content = "✨ Format", FontSize = 10, Height = 20, Background = LtaiTheme.Sbb(LtaiTheme.AccentWarning), Foreground = LtaiTheme.Sbb("#ffffff") };
        formatBtn.Click += (_, _) => RunFormat();
        var askAiBtn = new Button { Content = "🤖 问 AI", FontSize = 10, Height = 20, Background = LtaiTheme.Sbb(LtaiTheme.AccentDNA), Foreground = LtaiTheme.Sbb("#ffffff") };
        askAiBtn.Click += (_, _) => AskAiWithContext(_currentFile ?? _rootDir);

        var wrapBtn = new Button { Content = "⤫ 换行", FontSize = 10, Height = 20, Background = LtaiTheme.Sbb(LtaiTheme.BgPanel), Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim) };
        wrapBtn.Click += (_, _) => { _editor.WordWrap = !_editor.WordWrap; wrapBtn.Content = _editor.WordWrap ? "⤫ 已换行" : "⤫ 换行"; };
        var gotoBtn = new Button { Content = "↕ 跳转", FontSize = 10, Height = 20, Background = LtaiTheme.Sbb(LtaiTheme.BgPanel), Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim) };
        gotoBtn.Click += (_, _) => ShowGoToLineDialog();

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new(4), Spacing = 4,
            Children = { _toggleBtn, checkBtn, saveBtn, formatBtn, askAiBtn, wrapBtn, gotoBtn, _terminalBtn, _gitBranchLabel, _gitCommitBtn, _gitPullBtn, _gitPushBtn, _gitBlameBtn },
        };

        // ── 终端面板 ──
        _terminalView = new Terminal.TerminalView { WorkingDirectory = _rootDir, IsVisible = false };
        _terminalBtn = new Button { Content = "📟 终端", FontSize = 10, Height = 20, Background = LtaiTheme.Sbb(LtaiTheme.BgPanel), Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim) };
        _terminalBtn.Click += (_, _) =>
        {
            _terminalView.IsVisible = !_terminalView.IsVisible;
            if (_terminalView.IsVisible) _terminalView.Start();
            else _terminalView.Stop();
        };

        _editorPanel = new StackPanel { Children = { toolbar, _editor, _statusBar, _buildPanel, _terminalView } };

        var split = new Grid();
        split.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(280)));
        split.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        split.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        // ── Ctrl+F 搜索面板 ──
        SearchPanel.Install(_editor);

        // ── 问题面板（构建错误列表） ──
        _problemList = new ListBox
        {
            MinHeight = 0, MaxHeight = 150,
            Background = LtaiTheme.Sbb(LtaiTheme.Bg),
            FontSize = 11,
            IsVisible = false,
        };
        _problemList.SelectionChanged += (_, e) =>
        {
            if (_problemList.SelectedItem is (string file, int line, string msg))
                OpenFile(Path.Combine(_rootDir, file));
        };
        _buildPanel.Children.Add(_problemList);

        // ── 项目搜索框（树上方） ──
        var searchBox = new TextBox
        {
            Watermark = "🔍 搜索文件名 (Ctrl+P)...",
            FontSize = 11, Height = 22,
        };
        var searchResults = new ListBox
        {
            MinHeight = 0, MaxHeight = 200,
            Background = LtaiTheme.Sbb(LtaiTheme.Bg),
            FontSize = 11,
            IsVisible = false,
        };
        searchResults.SelectionChanged += (_, e) =>
        {
            if (searchResults.SelectedItem is string fp && File.Exists(fp)) OpenFile(fp);
        };
        searchBox.TextChanged += (_, _) =>
        {
            var q = searchBox.Text?.Trim();
            if (string.IsNullOrEmpty(q) || q.Length < 2) { searchResults.IsVisible = false; return; }
            try
            {
                var matches = Directory.EnumerateFiles(_rootDir, "*", SearchOption.AllDirectories)
                    .Where(f => TextExts.Contains(Path.GetExtension(f)) && Path.GetFileName(f).Contains(q, StringComparison.OrdinalIgnoreCase))
                    .Take(30).ToList();
                searchResults.ItemsSource = matches;
                searchResults.IsVisible = matches.Count > 0;
            }
            catch { searchResults.IsVisible = false; }
        };

        var treePanel = new StackPanel
        {
            Children = { searchBox, searchResults, new ScrollViewer { Content = _tree } }
        };

        var treeScroll = new ScrollViewer { Content = treePanel };
        Grid.SetColumn(treeScroll, 0); split.Children.Add(treeScroll);
        var editorScroll = new ScrollViewer { Content = _editorPanel };
        Grid.SetColumn(editorScroll, 1); split.Children.Add(editorScroll);
        Grid.SetColumn(_symbolList, 2); split.Children.Add(_symbolList);

        Content = split;

        // 文件系统监控：自动刷新目录树
        StartWatching(_rootDir);

        DetachedFromVisualTree += (_, _) => StopWatching();

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.S && e.KeyModifiers == KeyModifiers.Control) { SaveFile(); e.Handled = true; }
            if (e.Key == Key.E && e.KeyModifiers == KeyModifiers.Control) { ToggleEdit(); e.Handled = true; }
            if (e.Key == Key.B && e.KeyModifiers == KeyModifiers.Control) { RunProjectCmd("build"); e.Handled = true; }
            if (e.Key == Key.G && e.KeyModifiers == KeyModifiers.Control) { ShowGoToLineDialog(); e.Handled = true; }
            if (e.Key == Key.F12 && e.KeyModifiers == KeyModifiers.None) { GoToDefinition(); e.Handled = true; }
            if (e.Key == Key.D && e.KeyModifiers == KeyModifiers.Control)
            {
                GoToDefinition();
                e.Handled = true;
            }
            if (e.Key == Key.O && e.KeyModifiers == KeyModifiers.Control)
            {
                _symbolList.IsVisible = !_symbolList.IsVisible;
                if (_symbolList.IsVisible) RefreshSymbols();
                e.Handled = true;
            }
        };
    }

    private void ToggleEdit()
    {
        _isReadOnly = !_isReadOnly;
        _editor.IsReadOnly = _isReadOnly;
        _toggleBtn.Content = _isReadOnly ? "🔓 编辑" : "🔒 只读";
        _editor.Background = _isReadOnly
            ? LtaiTheme.Sbb(LtaiTheme.Bg)
            : LtaiTheme.Sbb(Color.Parse("#1a2332"));
    }

    private void BuildTree(ItemCollection items, string dir)
    {
        try
        {
            foreach (var d in Directory.GetDirectories(dir).OrderBy(Path.GetFileName))
            {
                var node = new TreeViewItem
                {
                    Header = $"📁 {Path.GetFileName(d)}",
                    Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
                    Tag = d,
                    ContextMenu = MakeDirContextMenu(d),
                };
                var capturedNode = node;
                var capturedDir = d;
                capturedNode.Expanded += (_, _) => { if (capturedNode.Items.Count == 0) BuildTree(capturedNode.Items, capturedDir); };
                items.Add(node);
            }
            foreach (var f in Directory.GetFiles(dir).Where(f => TextExts.Contains(Path.GetExtension(f))).OrderBy(Path.GetFileName))
            {
                var icon = f.EndsWith(".md") ? "📝" : f.EndsWith(".cs") ? "📄" : f.EndsWith(".py") ? "🐍" : "📄";
                var node = new TreeViewItem { Header = $"{icon} {Path.GetFileName(f)}", Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary), Tag = f, ContextMenu = MakeFileContextMenu(f) };
                items.Add(node);
            }
        }
        catch
        {
            // 非关键：目录读取失败时跳过
        }
    }

    private ContextMenu MakeFileContextMenu(string path)
    {
        var menu = new ContextMenu();
        menu.Items.Add(WithClick(new MenuItem { Header = "🤖 问 AI" }, (_, _) => AskAiWithContext(path)));

        var rename = new MenuItem { Header = "✏️ 重命名" };
        rename.Click += async (_, _) =>
        {
            var top = TopLevel.GetTopLevel(this) as Window;
            if (top == null) return;
            var input = new TextBox { Text = Path.GetFileName(path) };
            var ok = new Button { Content = "确定" };
            var dlg = new Window { Title = "重命名", Width = 350, Height = 120, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new StackPanel { Margin = new(15), Spacing = 8, Children = { input, ok } } };
            string? result = null;
            ok.Click += (_, _) => { result = input.Text?.Trim(); dlg.Close(); };
            await dlg.ShowDialog(top);
            if (string.IsNullOrEmpty(result)) return;
            try { File.Move(path, Path.Combine(Path.GetDirectoryName(path)!, result)); RefreshTree(); }
            catch (Exception ex) { _statusBar.Text = $"❌ 重命名失败: {ex.Message}"; }
        };
        menu.Items.Add(rename);
        var del = new MenuItem { Header = "🗑️ 删除" };
        del.Click += async (_, _) =>
        {
            var top = TopLevel.GetTopLevel(this) as Window;
            if (top == null) return;
            var yes = new Button { Content = "删除" };
            var no = new Button { Content = "取消" };
            var dlg = new Window { Title = "确认删除", Width = 350, Height = 130, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new StackPanel { Margin = new(15), Spacing = 8,
                    Children = { new TextBlock { Text = $"确认删除 {Path.GetFileName(path)}?" },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { yes, no } } } } };
            var confirmed = false;
            yes.Click += (_, _) => { confirmed = true; dlg.Close(); };
            no.Click += (_, _) => dlg.Close();
            await dlg.ShowDialog(top);
            if (!confirmed) return;
            try { File.Delete(path); RefreshTree(); _statusBar.Text = $"✅ 已删除: {path}"; }
            catch (Exception ex) { _statusBar.Text = $"❌ 删除失败: {ex.Message}"; }
        };
        menu.Items.Add(del);
        return menu;
    }

    private ContextMenu MakeDirContextMenu(string path)
    {
        var menu = new ContextMenu();
        menu.Items.Add(WithClick(new MenuItem { Header = "🤖 问 AI (文件夹)" }, (_, _) => AskAiWithContext(path)));

        var rename = new MenuItem { Header = "✏️ 重命名目录" };
        rename.Click += async (_, _) =>
        {
            var top = TopLevel.GetTopLevel(this) as Window;
            if (top == null) return;
            var input = new TextBox { Text = Path.GetFileName(path) };
            var ok = new Button { Content = "确定" };
            var dlg = new Window { Title = "重命名目录", Width = 350, Height = 120, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new StackPanel { Margin = new(15), Spacing = 8, Children = { input, ok } } };
            string? result = null;
            ok.Click += (_, _) => { result = input.Text?.Trim(); dlg.Close(); };
            await dlg.ShowDialog(top);
            if (string.IsNullOrEmpty(result)) return;
            try { Directory.Move(path, Path.Combine(Path.GetDirectoryName(path)!, result)); RefreshTree(); }
            catch (Exception ex) { _statusBar.Text = $"❌ 重命名失败: {ex.Message}"; }
        };
        menu.Items.Add(rename);

        // 新建文件
        var newFile = new MenuItem { Header = "📄 新建文件" };
        newFile.Click += (_, _) =>
        {
            for (int n = 1; n < 1000; n++)
            {
                var name = $"新建文件{n}.txt";
                var fp = Path.Combine(path, name);
                if (!File.Exists(fp)) { try { File.WriteAllText(fp, ""); RefreshTree(); _statusBar.Text = $"✅ 已创建: {name}"; } catch (Exception ex) { _statusBar.Text = $"❌ 创建失败: {ex.Message}"; } break; }
            }
        };
        menu.Items.Add(newFile);

        // 新建文件夹
        var newDir = new MenuItem { Header = "📁 新建文件夹" };
        newDir.Click += (_, _) =>
        {
            for (int n = 1; n < 1000; n++)
            {
                var name = $"新建文件夹{n}";
                var fp = Path.Combine(path, name);
                if (!Directory.Exists(fp)) { try { Directory.CreateDirectory(fp); RefreshTree(); _statusBar.Text = $"✅ 已创建: {name}"; } catch (Exception ex) { _statusBar.Text = $"❌ 创建失败: {ex.Message}"; } break; }
            }
        };
        menu.Items.Add(newDir);

        menu.Items.Add(new MenuItem { Header = "-" });

        // 项目感知操作
        var files = Directory.GetFiles(path);
        var allFiles = new HashSet<string>(files.Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
        if (allFiles.Any(f => f?.EndsWith(".csproj") == true || f?.EndsWith(".sln") == true))
        {
            menu.Items.Add(WithClick(new MenuItem { Header = "🛠 Build" }, (_, _) => RunShell("dotnet build")));
            menu.Items.Add(WithClick(new MenuItem { Header = "🧪 Test" }, (_, _) => RunShell("dotnet test")));
            menu.Items.Add(WithClick(new MenuItem { Header = "▶ Run" }, (_, _) => RunShell("dotnet run")));
        }
        else if (allFiles.Contains("Cargo.toml"))
            menu.Items.Add(WithClick(new MenuItem { Header = "🛠 cargo build" }, (_, _) => RunShell("cargo build")));
        else if (allFiles.Contains("package.json"))
            menu.Items.Add(WithClick(new MenuItem { Header = "🛠 npm run build" }, (_, _) => RunShell("npm run build")));

        // Git 操作
        if (Directory.Exists(Path.Combine(path, ".git")) || FindGitDir(path) != null)
        {
            menu.Items.Add(WithClick(new MenuItem { Header = "🌿 git status" }, (_, _) => RunGitCmd("status")));
            menu.Items.Add(WithClick(new MenuItem { Header = "💾 git commit..." }, (_, _) => ShowGitCommitDialog()));
        }
        return menu;
    }

    private void AskAiWithContext(string context)
    {
        AskAiRequested?.Invoke(context);
    }

    private static MenuItem WithClick(MenuItem item, EventHandler<EventArgs> handler)
    {
        item.Click += handler;
        return item;
    }

    private void RefreshTree()
    {
        var expanded = new HashSet<string>();
        CaptureExpanded(_tree.Items, "", expanded);
        _tree.Items.Clear();
        BuildTree(_tree.Items, _rootDir);
        RestoreExpanded(_tree.Items, expanded);
    }

    private void OnTreeSelectionChanged(object? s, SelectionChangedEventArgs e)
    {
        if (_tree.SelectedItem is TreeViewItem item && item.Tag is string path) OpenFile(path);
    }

    private void OpenFile(string path)
    {
        try
        {
            _currentFile = path;
            var fi = new FileInfo(path);
            if (fi.Length > MaxEditorSize)
            {
                _editor.Text = $"[文件过大: {FormatSize(fi.Length)}]\nAvaloniaEdit 将整文件读入内存 (UTF-16 编码, 实际内存 ≈ 文件大小 × 2)。\n\n"
                    + $"建议使用 ReadFileContent 工具按需读取。\n\n路径: {path}";
                _editor.IsReadOnly = true;
                _isReadOnly = true;
                _toggleBtn.Content = "🔓 编辑";
                _statusBar.Text = $"{path}  |  {FormatSize(fi.Length)}  |  过大，无法编辑";
                return;
            }
            // AvaloniaEdit 会将整个文件加载到 TextDocument 中 (UTF-16 内存 = 文件字节数 × 2)
            // 用 SequentialScan 提示减少缓存管理器开销
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            _editor.Load(fs);
            var ext = Path.GetExtension(path);
            var hlName = ext switch
            {
                ".cs" => "C#", ".py" => "Python", ".js" => "JavaScript", ".ts" => "TypeScript",
                ".go" => "Go", ".rs" => "Rust", ".java" => "Java", ".xml" => "XML",
                ".html" or ".htm" => "HTML", ".css" => "CSS", ".json" => "JSON",
                ".md" => "Markdown", ".yaml" or ".yml" => "YAML",
                ".sh" or ".bash" => "PowerShell", _ => null,
            };
            try { _editor.SyntaxHighlighting = hlName != null ? HighlightingManager.Instance.GetDefinition(hlName) : null; } catch { /* 语法高亮不可用 */ }
            _editor.IsReadOnly = !CodeExts.Contains(ext);
            _isReadOnly = _editor.IsReadOnly;
            _toggleBtn.Content = _isReadOnly ? "🔓 编辑" : "🔒 只读";
            // 估算行数：用文件大小 / 平均行长
            var approxLines = fi.Length > 0 ? (int)(fi.Length / 60) : 0;
            // 编码检测（读 BOM）
            var encoding = DetectEncoding(path);
            _statusBar.Text = $"{path}  |  {FormatSize(fi.Length)}  |  ~{approxLines} 行  |  {encoding}";
            RefreshSymbols();
            DetectProject();
            UpdateGitInfo();

            // 监视当前文件的外部变更
            StartFileWatcher(path);
        }
        catch { _statusBar.Text = $"无法打开: {path}"; }
    }

    private void StartFileWatcher(string path)
    {
        try
        {
            _fileWatcher?.Dispose();
            var dir = Path.GetDirectoryName(path);
            if (dir == null) return;
            _fileWatcher = new FileSystemWatcher(dir, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _fileWatcher.Changed += OnExternalFileChange;
        }
        catch { /* file watcher not critical */ }
    }

    private DateTime _lastFileChange = DateTime.MinValue;

    private void OnExternalFileChange(object s, FileSystemEventArgs e)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastFileChange).TotalMilliseconds < 1000) return;
        _lastFileChange = now;
        System.Threading.Thread.Sleep(200);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_currentFile == e.FullPath && File.Exists(e.FullPath))
                OpenFile(e.FullPath);
        });
    }

    private void RunSyntaxCheck()
    {
        if (_currentFile == null) { _statusBar.Text = "请先打开一个文件"; return; }
        try
        {
            var ext = Path.GetExtension(_currentFile);
            if (ext != ".cs" && ext != ".py" && ext != ".js" && ext != ".ts" && ext != ".go" && ext != ".rs" && ext != ".java")
            { _statusBar.Text = "语法检查仅支持 .cs/.py/.js/.ts/.go/.rs/.java"; return; }

            using var parser = new LTAI.Agent.Tools.TreeSitterParser();
            var code = _editor.Text;
            var symbols = parser.ExtractSymbols(code, ext);

            if (symbols.Count == 0)
            {
                _statusBar.Text = "⚠️ 未解析出符号，可能存在语法错误";
            }
            else
            {
                var kinds = symbols.Select(s => s.kind).Distinct();
                _statusBar.Text = $"✅ 语法检查通过: {symbols.Count} 个符号 ({string.Join(", ", kinds)})";
            }
        }
        catch (Exception ex) { _statusBar.Text = $"❌ 语法错误: {ex.Message}"; }
    }

    private void SaveFile()
    {
        if (_currentFile == null) return;
        try { File.WriteAllText(_currentFile, _editor.Text); _statusBar.Text = $"✅ 已保存: {_currentFile}"; }
        catch (Exception ex) { _statusBar.Text = $"❌ 保存失败: {ex.Message}"; }
    }

    private void StartWatching(string dir)
    {
        try
        {
            _watcher?.Dispose();
            _watcher = new FileSystemWatcher(dir)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                IncludeSubdirectories = true,
                EnableRaisingEvents = false, // 先启动事件再开
            };
            _watcher.Created += OnFileChanged;
            _watcher.Deleted += OnFileChanged;
            _watcher.Renamed += OnFileRenamed;
            _watcher.Changed += OnFileChanged;
            _watcher.Error += (_, e) => System.Diagnostics.Debug.WriteLine($"FileWatcher error: {e.GetException().Message}");
            _watcher.EnableRaisingEvents = true;
            System.Diagnostics.Debug.WriteLine($"FileWatcher started: {dir}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"FileWatcher start failed: {ex.Message}");
        }
    }

    private void StopWatching()
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
        _fileWatcher?.Dispose();
        _fileWatcher = null;
    }

    private DateTime _lastTreeRefresh = DateTime.MinValue;

    private void OnFileChanged(object s, FileSystemEventArgs e)
    {
        // 防抖：500ms 内的多次变更合并为一次刷新
        var now = DateTime.UtcNow;
        if ((now - _lastTreeRefresh).TotalMilliseconds < 500) return;
        _lastTreeRefresh = now;

        // 过滤隐藏/系统目录
        var name = Path.GetFileName(e.Name ?? "");
        if (name.StartsWith('.')) return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try
            {
                // 保存展开状态
                var expandedDirs = new HashSet<string>();
                CaptureExpanded(_tree.Items, "", expandedDirs);

                _tree.Items.Clear();
                BuildTree(_tree.Items, _rootDir);

                // 恢复展开状态
                RestoreExpanded(_tree.Items, expandedDirs);
            }
            catch { /* tree refresh error - non-critical */ }
        });
    }

    private void OnFileRenamed(object s, RenamedEventArgs e)
    {
        OnFileChanged(s, e);
    }

    private void CaptureExpanded(ItemCollection items, string prefix, HashSet<string> result)
    {
        foreach (var item in items.OfType<TreeViewItem>())
        {
            if (item.IsExpanded && item.Tag is string tag && Directory.Exists(tag))
                result.Add(tag);
            if (item.Items.Count > 0)
                CaptureExpanded(item.Items, prefix, result);
        }
    }

    private void RestoreExpanded(ItemCollection items, HashSet<string> expanded)
    {
        foreach (var item in items.OfType<TreeViewItem>())
        {
            if (item.Tag is string tag && expanded.Contains(tag))
            {
                item.IsExpanded = true;
                if (item.Items.Count == 0 && Directory.Exists(tag))
                    BuildTree(item.Items, tag);
            }
            if (item.Items.Count > 0)
                RestoreExpanded(item.Items, expanded);
        }
    }

    private void UpdateGitInfo()
    {
        try
        {
            // 检查是否在 Git 仓库中
            var gitDir = FindGitDir(_rootDir);
            if (gitDir == null) { _gitBranchLabel.IsVisible = false; return; }

            // 获取当前分支
            var branch = RunGit("rev-parse --abbrev-ref HEAD")?.Trim();
            _gitBranch = branch ?? "unknown";
            _gitBranchLabel.Text = $"🌿 {_gitBranch}";
            _gitBranchLabel.IsVisible = true;

            // 获取文件状态
            var status = RunGit("status --porcelain --untracked-files=normal");
            _gitFileStatus = new Dictionary<string, string>();
            if (status != null)
            {
                foreach (var line in status.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.Length < 4) continue;
                    var state = line[..2].Trim();
                    var file = line[3..].Trim();
                    // 去掉引号（git 对特殊文件名加引号）
                    if (file.StartsWith('"') && file.EndsWith('"')) file = file[1..^1];
                    _gitFileStatus[file] = state;
                }
            }

            // 显示 Git 按钮
            _gitCommitBtn.IsVisible = true;
            _gitPullBtn.IsVisible = true;
            _gitPushBtn.IsVisible = true;
            _gitBlameBtn.IsVisible = _currentFile != null;

            // 刷新文件树状态色
            RefreshTreeGitStatus();
        }
        catch { _gitBranchLabel.IsVisible = false; }
    }

    private static string? FindGitDir(string dir)
    {
        var d = new DirectoryInfo(dir);
        while (d != null)
        {
            if (Directory.Exists(Path.Combine(d.FullName, ".git"))) return d.FullName;
            d = d.Parent;
        }
        return null;
    }

    private string? RunGit(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("git", args)
            {
                WorkingDirectory = _rootDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = new Process { StartInfo = psi };
            process.Start();
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5000);
            return process.ExitCode == 0 ? output : null;
        }
        catch { return null; }
    }

    private void RunGitCmd(string args)
    {
        if (_gitBranch == null) { _statusBar.Text = "⚠️ 不在 Git 仓库中"; return; }
        try
        {
            _statusBar.Text = $"🔄 git {args}...";
            var psi = new ProcessStartInfo("git", args)
            {
                WorkingDirectory = _rootDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = new Process { StartInfo = psi };
            process.Start();
            var output = process.StandardOutput.ReadToEnd().Trim();
            var error = process.StandardError.ReadToEnd().Trim();
            process.WaitForExit(60_000);
            _statusBar.Text = process.ExitCode == 0
                ? $"✅ git {args}: 成功"
                : $"❌ git {args}: {error}";
            if (!string.IsNullOrEmpty(output)) _buildOutput.Text = output;
            UpdateGitInfo(); // 刷新状态
        }
        catch (Exception ex) { _statusBar.Text = $"❌ git {args}: {ex.Message}"; }
    }

    private void ShowGitCommitDialog()
    {
        if (_gitBranch == null) { _statusBar.Text = "⚠️ 不在 Git 仓库中"; return; }
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is not Window owner) return;

        var msgBox = new TextBox { AcceptsReturn = true, MinHeight = 60, MinWidth = 300, MaxWidth = 500, Watermark = "Commit message..." };
        var stageAll = new CheckBox { Content = "暂存所有变更 (git add -A)", IsChecked = true };
        var yesBtn = new Button { Content = "提交", Width = 80 };
        var noBtn = new Button { Content = "取消", Width = 80 };
        var dialog = new Window
        {
            Title = $"💾 Commit — 🌿 {_gitBranch}",
            Width = 450, Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new(15), Spacing = 8,
                Children =
                {
                    new TextBlock { Text = $"提交到 {_gitBranch}", FontWeight = FontWeight.Bold },
                    msgBox, stageAll,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { yesBtn, noBtn } },
                }
            }
        };
        yesBtn.Click += (_, _) =>
        {
            var msg = msgBox.Text?.Trim();
            if (string.IsNullOrEmpty(msg)) return;
            var stage = stageAll.IsChecked == true ? "add -A && " : "";
            var result = RunGit($"{stage}commit -m \"{msg.Replace("\"", "\\\"")}\"");
            _statusBar.Text = result != null ? $"✅ 已提交: {msg[..Math.Min(msg.Length, 50)]}" : "❌ 提交失败";
            dialog.Close();
            UpdateGitInfo();
        };
        noBtn.Click += (_, _) => dialog.Close();
        dialog.ShowDialog(owner);
    }

    private void ToggleBlame()
    {
        if (_currentFile == null) return;
        if (_blameData != null) { _blameData = null; _statusBar.Text = "Blame 已关闭"; return; }

        try
        {
            var output = RunGit($"blame --line-porcelain \"{_currentFile}\"");
            if (output == null) { _statusBar.Text = "❌ Blame 不可用"; return; }

            _blameData = new Dictionary<string, string>();
            string? currentCommit = null, currentAuthor = null;
            int currentLine = 0;
            foreach (var line in output.Split('\n'))
            {
                if (line.StartsWith("author ")) currentAuthor = line[7..].Trim();
                else if (line.Length > 0 && !line.StartsWith('\t') && !line.Contains(' '))
                {
                    var parts = line.Split(' ');
                    if (parts.Length >= 4 && int.TryParse(parts[2], out var ln))
                        currentLine = ln;
                }
                else if (line.StartsWith('\t'))
                {
                    if (currentLine > 0 && currentAuthor != null)
                        _blameData[currentLine.ToString()] = currentAuthor;
                    currentLine = 0;
                }
            }
            _statusBar.Text = $"👤 Blame: 已加载 {_blameData.Count} 行作者信息";
        }
        catch (Exception ex) { _statusBar.Text = $"❌ Blame 失败: {ex.Message}"; }
    }

    private void RefreshTreeGitStatus()
    {
        // 给 TreeView 条目着色
        try
        {
            ApplyGitStatusToTree(_tree.Items, "");
        }
        catch { }
    }

    private void ApplyGitStatusToTree(ItemCollection items, string prefix)
    {
        foreach (var item in items.OfType<TreeViewItem>())
        {
            if (item.Tag is string path)
            {
                var relPath = Path.GetRelativePath(_rootDir, path).Replace('\\', '/');
                if (_gitFileStatus.TryGetValue(relPath, out var status))
                {
                    var fg = status switch
                    {
                        "M" or "M " => LtaiTheme.AccentDNA,       // 修改 → 蓝色
                        "A" or "A " => LtaiTheme.AccentSystem,     // 新增 → 绿色
                        "D" or "D " => LtaiTheme.AccentDanger,     // 删除 → 红色
                        "R" or "R " => LtaiTheme.AccentWarning,    // 重命名 → 黄色
                        "?" or "? " => LtaiTheme.AccentWarning,    // 未跟踪 → 黄色
                        _ => LtaiTheme.TextPrimary
                    };
                    item.Foreground = LtaiTheme.Sbb(fg);
                }
                else if (item.Tag is string tag && !tag.StartsWith(_rootDir))
                {
                    item.Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary);
                }
            }
            if (item.Items.Count > 0)
                ApplyGitStatusToTree(item.Items, prefix + "/");
        }
    }

    private void RefreshSymbols()
    {
        try
        {
            if (_currentFile == null || !_symbolList.IsVisible) return;
            var ext = Path.GetExtension(_currentFile);
            var symbols = new LTAI.Agent.Tools.TreeSitterParser().ExtractSymbols(_editor.Text, ext);
            _symbolList.ItemsSource = symbols.Select(s => new SymbolItem(s.kind == "method" ? "🔧" : s.kind == "class" ? "📦" : "📌", s.name, s.line)).ToList();
        }
        catch { /* non-critical */ }
    }

    private void DetectProject()
    {
        var files = new HashSet<string>(Directory.GetFiles(_rootDir).Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
        var allFiles = new HashSet<string>(Directory.GetFiles(_rootDir, "*.*", SearchOption.TopDirectoryOnly).Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);

        string type, buildCmd = "", testCmd = "", runCmd = "", deployCmd = "";
        string label;

        if (allFiles.Any(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)) || allFiles.Any(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        { type = "C# .NET"; label = "🟣"; buildCmd = "dotnet build"; testCmd = "dotnet test"; runCmd = "dotnet run"; deployCmd = "dotnet publish -c Release"; }
        else if (files.Contains("Cargo.toml"))
        { type = "Rust"; label = "🦀"; buildCmd = "cargo build"; testCmd = "cargo test"; runCmd = "cargo run"; }
        else if (files.Contains("go.mod"))
        { type = "Go"; label = "🔵"; buildCmd = "go build ./..."; testCmd = "go test ./..."; runCmd = "go run ."; }
        else if (files.Contains("package.json"))
        { type = "Node.js"; label = "🟢"; buildCmd = "npm run build"; testCmd = "npm test"; runCmd = "npm start"; deployCmd = "npm publish"; }
        else if (files.Contains("pyproject.toml") || files.Contains("setup.py") || files.Contains("requirements.txt"))
        { type = "Python"; label = "🐍"; testCmd = "python -m pytest"; runCmd = "python main.py"; buildCmd = files.Contains("pyproject.toml") ? "python -m build" : ""; }
        else if (files.Contains("pom.xml"))
        { type = "Java (Maven)"; label = "🟤"; buildCmd = "mvn compile"; testCmd = "mvn test"; runCmd = "mvn exec:java"; deployCmd = "mvn deploy"; }
        else if (files.Contains("build.gradle") || files.Contains("build.gradle.kts"))
        { type = "Java (Gradle)"; label = "🟤"; buildCmd = "gradle build"; testCmd = "gradle test"; runCmd = "gradle run"; }
        else
        { type = "unknown"; label = "⚪"; }

        _projectType = type;
        var visible = type != "unknown";
        _buildBtn.IsVisible = visible && !string.IsNullOrEmpty(buildCmd);
        _buildBtn.Content = string.IsNullOrEmpty(buildCmd) ? "🛠 Build" : $"🛠 {buildCmd}";
        _testBtn.IsVisible = visible && !string.IsNullOrEmpty(testCmd);
        _testBtn.Content = string.IsNullOrEmpty(testCmd) ? "🧪 Test" : $"🧪 {testCmd}";
        _runBtn.IsVisible = visible && !string.IsNullOrEmpty(runCmd);
        _runBtn.Content = string.IsNullOrEmpty(runCmd) ? "▶ Run" : $"▶ {runCmd}";
        _publishBtn.IsVisible = visible && !string.IsNullOrEmpty(deployCmd);
        _publishBtn.Content = string.IsNullOrEmpty(deployCmd) ? "🚀 Deploy" : $"🚀 {deployCmd}";
    }

    private void RunProjectCmd(string action)
    {
        var type = _projectType;
        var cmd = action switch
        {
            "build" when type == "C# .NET" => "dotnet build",
            "build" when type == "Rust" => "cargo build",
            "build" when type == "Go" => "go build ./...",
            "build" when type == "Node.js" => "npm run build",
            "build" when type == "Python" => "python -m build",
            "build" when type == "Java (Maven)" => "mvn compile",
            "build" when type == "Java (Gradle)" => "gradle build",
            "test" when type == "C# .NET" => "dotnet test",
            "test" when type == "Rust" => "cargo test",
            "test" when type == "Go" => "go test ./...",
            "test" when type == "Node.js" => "npm test",
            "test" when type == "Python" => "python -m pytest",
            "test" when type == "Java (Maven)" => "mvn test",
            "test" when type == "Java (Gradle)" => "gradle test",
            "run" when type == "C# .NET" => "dotnet run",
            "run" when type == "Rust" => "cargo run",
            "run" when type == "Go" => "go run .",
            "run" when type == "Node.js" => "npm start",
            "run" when type == "Python" => "python main.py",
            "run" when type == "Java (Maven)" => "mvn exec:java",
            "run" when type == "Java (Gradle)" => "gradle run",
            "publish" when type == "C# .NET" => "dotnet publish -c Release",
            "publish" when type == "Node.js" => "npm publish",
            "publish" when type == "Java (Maven)" => "mvn deploy",
            _ => null
        };
        if (cmd == null) { _statusBar.Text = $"当前项目类型 ({type}) 不支持 {action} 操作"; return; }
        RunShell(cmd);
    }

    private void RunShell(string command)
    {
        try
        {
            _buildOutput.IsVisible = true;
            _buildOutput.Text = $"🔄 {command}\n";
            var parts = command.Split(' ', 2);
            var psi = new ProcessStartInfo(parts[0], parts.Length > 1 ? parts[1] : "")
            {
                WorkingDirectory = _rootDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = new Process { StartInfo = psi };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit(120_000);
            var result = process.ExitCode == 0 ? "✅ 成功" : "❌ 失败";
            _buildOutput.Text = $"{result} (exit={process.ExitCode})\n{command}\n\n{output}\n{error}".Trim();
            _statusBar.Text = $"{command}: {result}";

            // 解析构建错误 → 问题列表
            if (process.ExitCode != 0)
            {
                var problems = new List<(string file, int line, string msg)>();
                var errorRx = new Regex(@"^\s*([^(\]]+?)\((\d+)(?:,\d+)?\)\s*:\s*(error|warning)\s+(\w+\d+)\s*:\s*(.+)", RegexOptions.Multiline);
                foreach (Match m in errorRx.Matches(output + "\n" + error))
                {
                    var file = m.Groups[1].Value.Trim();
                    var line = int.Parse(m.Groups[2].Value);
                    var msg = m.Groups[5].Value.Trim();
                    problems.Add((file, line, $"{m.Groups[3].Value} {m.Groups[4].Value}: {msg}"));
                }
                if (problems.Count > 0)
                {
                    _problemList.ItemsSource = problems;
                    _problemList.IsVisible = true;
                }
            }
        }
        catch (Exception ex) { _buildOutput.Text = $"❌ 错误: {ex.Message}"; }
    }

    private void RunFormat()
    {
        if (_currentFile == null) { _statusBar.Text = "请先打开一个文件"; return; }
        try
        {
            var ext = Path.GetExtension(_currentFile);
            if (ext == ".cs") RunShell("dotnet format");
            else if (ext is ".js" or ".ts" or ".json" or ".md" or ".yaml" or ".yml")
            {
                // 尝试用 prettier（如果可用）
                var psi = new ProcessStartInfo("npx", $"prettier --write \"{_currentFile}\"")
                {
                    WorkingDirectory = _rootDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var process = new Process { StartInfo = psi };
                process.Start();
                process.WaitForExit(30_000);
                if (process.ExitCode == 0)
                {
                    using var fs = new FileStream(_currentFile, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
                    _editor.Load(fs);
                    _statusBar.Text = $"✅ 已格式化: {_currentFile}";
                }
                else { _statusBar.Text = $"⚠️ prettier 不可用，请安装: npm install -g prettier"; }
            }
            else { _statusBar.Text = $"⚠️ 不支持格式化 {ext} 文件"; }
        }
        catch (Exception ex) { _statusBar.Text = $"❌ 格式化失败: {ex.Message}"; }
    }

    private void ShowGoToLineDialog()
    {
        if (_currentFile == null) return;
        var top = TopLevel.GetTopLevel(this) as Window;
        if (top == null) return;
        var input = new TextBox { Watermark = $"行号 (1-{_editor.Document.LineCount})" };
        var ok = new Button { Content = "跳转" };
        var dlg = new Window { Title = "跳转到行", Width = 300, Height = 120, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel { Margin = new(15), Spacing = 8, Children = { input, ok } } };
        ok.Click += (_, _) =>
        {
            if (int.TryParse(input.Text, out var line) && line >= 1 && line <= _editor.Document.LineCount)
                _editor.TextArea.Caret.Line = line;
            dlg.Close();
        };
        dlg.ShowDialog(top);
    }

    private void GoToDefinition()
    {
        if (_currentFile == null) return;
        try
        {
            // 1. 获取光标下的单词
            var caret = _editor.TextArea.Caret;
            var lineSegment = _editor.Document.GetLineByNumber(caret.Line);
            var line = _editor.Document.GetText(lineSegment);
            var col = caret.Column - 1;
            if (col < 0 || col >= line.Length) return;

            // 向前后扩展取完整标识符
            int start = col;
            while (start > 0 && (char.IsLetterOrDigit(line[start - 1]) || line[start - 1] == '_')) start--;
            int end = col;
            while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] == '_')) end++;
            if (start >= end) return;
            var name = line[start..end];
            if (string.IsNullOrEmpty(name) || name == "var" || name == "await" || name == "new") return;

            _statusBar.Text = $"🔍 查找定义: {name}...";

            // 2. 在当前文件中搜索定义
            var ext = Path.GetExtension(_currentFile);
            using var parser = new LTAI.Agent.Tools.TreeSitterParser();
            var symbols = parser.ExtractSymbols(_editor.Text, ext);
            var def = symbols.FirstOrDefault(s => s.name == name && s.kind is "class" or "method" or "struct" or "interface" or "enum" or "property" or "field" or "function");

            if (def.name != null)
            {
                // 当前文件找到定义 → 跳转
                _editor.TextArea.Caret.Line = def.line;
                _editor.TextArea.Caret.Column = 1;
                _editor.ScrollTo(def.line, 1);
                _statusBar.Text = $"📍 {name} — 定义在第 {def.line} 行 ({def.kind})";
                return;
            }

            // 3. 跨文件搜索定义（限制范围，避免长时间扫描）
            var searchDir = Path.GetDirectoryName(_currentFile);
            if (searchDir == null) { _statusBar.Text = $"❌ 未找到 {name} 的定义"; return; }

            // 向上找 .sln 或 .csproj 目录作为搜索根
            var projectRoot = FindProjectRoot(searchDir);
            if (projectRoot == null) projectRoot = searchDir;

            var found = false;
            foreach (var f in Directory.EnumerateFiles(projectRoot, $"*{ext}", SearchOption.AllDirectories)
                .Take(200)) // 限制扫描文件数
            {
                if (f == _currentFile) continue;
                var content = File.ReadAllText(f);
                var fileSymbols = parser.ExtractSymbols(content, ext);
                var fileDef = fileSymbols.FirstOrDefault(s => s.name == name && s.kind is "class" or "method" or "struct" or "interface" or "enum" or "function");
                if (fileDef.name != null)
                {
                    OpenFile(f);
                    _editor.TextArea.Caret.Line = fileDef.line;
                    _editor.TextArea.Caret.Column = 1;
                    _editor.ScrollTo(fileDef.line, 1);
                    _statusBar.Text = $"📍 {name} — {Path.GetFileName(f)}:{fileDef.line} ({fileDef.kind})";
                    found = true;
                    break;
                }
            }

            if (!found)
                _statusBar.Text = $"❌ 未找到 {name} 的定义 (已搜索 {projectRoot})";
        }
        catch (Exception ex)
        {
            _statusBar.Text = $"❌ 跳转失败: {ex.Message}";
        }
    }

    private static string? FindProjectRoot(string dir)
    {
        var d = new DirectoryInfo(dir);
        while (d != null)
        {
            if (d.GetFiles("*.sln").Length > 0 || d.GetFiles("*.csproj").Length > 0)
                return d.FullName;
            d = d.Parent;
        }
        return null;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B", < 1024 * 1024 => $"{bytes / 1024} KB", _ => $"{bytes / 1024 / 1024} MB"
    };

    private static string DetectEncoding(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1);
            var bom = new byte[4];
            var read = fs.Read(bom, 0, 4);
            if (read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF) return "UTF-8 BOM";
            if (read >= 4 && bom[0] == 0xFF && bom[1] == 0xFE && bom[2] == 0x00 && bom[3] == 0x00) return "UTF-32 LE";
            if (read >= 4 && bom[0] == 0x00 && bom[1] == 0x00 && bom[2] == 0xFE && bom[3] == 0xFF) return "UTF-32 BE";
            if (read >= 2 && bom[0] == 0xFF && bom[1] == 0xFE) return "UTF-16 LE";
            if (read >= 2 && bom[0] == 0xFE && bom[1] == 0xFF) return "UTF-16 BE";
            // 检测是否为 UTF-8
            if (read >= 3 && IsUtf8NoBom(fs)) return "UTF-8";
            return "ASCII";
        }
        catch { return "?"; }
    }

    private static bool IsUtf8NoBom(FileStream fs)
    {
        try
        {
            var sample = new byte[1024];
            var read = fs.Read(sample, 0, sample.Length);
            int i = 0;
            while (i < read)
            {
                if (sample[i] <= 0x7F) i++;
                else if (sample[i] >= 0xC2 && sample[i] <= 0xDF && i + 1 < read && sample[i + 1] >= 0x80 && sample[i + 1] <= 0xBF) i += 2;
                else if (i + 2 < read && IsUtf8ThreeByte(sample[i], sample[i + 1], sample[i + 2])) i += 3;
                else return false;
            }
            return true;
        }
        catch { return false; }
    }

    private static bool IsUtf8ThreeByte(byte b1, byte b2, byte b3) =>
        (b1 >= 0xE0 && b1 <= 0xEF) && b2 >= 0x80 && b2 <= 0xBF && b3 >= 0x80 && b3 <= 0xBF;
}

/// <summary>多语言代码折叠策略 — 支持大括号/region/HTML 标签/缩进语言。</summary>
public sealed class MultiLangFoldingStrategy
{
    public void UpdateFoldings(FoldingManager manager, TextDocument document)
    {
        var foldings = new List<NewFolding>();
        var ext = Path.GetExtension(document.FileName ?? "");
        if (IsBraceLang(ext))
            AddBraceFoldings(document, foldings);
        else if (ext is ".html" or ".htm" or ".xml" or ".xaml" or ".csproj" or ".props" or ".targets")
            AddXmlTagFoldings(document, foldings);
        else if (ext is ".py")
            AddIndentFoldings(document, foldings);
        // region/ifendif 适用于 C# 等
        AddRegionFoldings(document, foldings);
        manager.UpdateFoldings(foldings.OrderBy(f => f.StartOffset).ToList(), -1);
    }

    private static bool IsBraceLang(string ext) => ext switch
    {
        ".cs" or ".js" or ".ts" or ".jsx" or ".tsx" or ".go" or ".rs" or ".java"
        or ".css" or ".scss" or ".less" or ".swift" or ".kt" or ".kts" or ".dart" => true,
        _ => false
    };

    private static void AddBraceFoldings(TextDocument document, List<NewFolding> foldings)
    {
        var openStack = new Stack<int>();
        for (int i = 1; i <= document.LineCount; i++)
        {
            var line = document.GetLineByNumber(i);
            var text = document.GetText(line);
            var trimmed = text.Trim();
            if (trimmed.EndsWith("{"))
                openStack.Push(line.Offset); // 用 Offset（字符位置）
            else if (trimmed == "}" && openStack.Count > 0)
            {
                var startOff = openStack.Pop();
                if (line.EndOffset - startOff > 1)
                    foldings.Add(new NewFolding(startOff, line.EndOffset));
            }
        }
    }

    private static void AddXmlTagFoldings(TextDocument document, List<NewFolding> foldings)
    {
        var tagStack = new Stack<(string name, int startOff)>();
        for (int i = 1; i <= document.LineCount; i++)
        {
            var line = document.GetLineByNumber(i);
            var text = document.GetText(line).Trim();
            if (text.StartsWith("<") && !text.StartsWith("</") && !text.EndsWith("/>") && !text.Contains(">"))
            {
                var tagName = text.Split(' ', '>', '\n')[0].Trim('<');
                if (!string.IsNullOrEmpty(tagName))
                    tagStack.Push((tagName, line.Offset));
            }
            else if (text.StartsWith("</"))
            {
                var closeTag = text.Split('>')[0].Trim('/').Trim('<');
                while (tagStack.Count > 0)
                {
                    var (name, startOff) = tagStack.Pop();
                    if (name == closeTag)
                    {
                        if (line.EndOffset - startOff > 1)
                            foldings.Add(new NewFolding(startOff, line.EndOffset));
                        break;
                    }
                }
            }
        }
    }

    private static void AddIndentFoldings(TextDocument document, List<NewFolding> foldings)
    {
        var indentStack = new Stack<(int off, int indent)>();
        for (int i = 1; i <= document.LineCount; i++)
        {
            var line = document.GetLineByNumber(i);
            var text = document.GetText(line);
            var indent = text.TakeWhile(char.IsWhiteSpace).Count();
            var trimmed = text.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            if (trimmed.EndsWith(":"))
                indentStack.Push((line.Offset, indent));
            else if (indentStack.Count > 0 && indent <= indentStack.Peek().indent)
            {
                var (startOff, _) = indentStack.Pop();
                if (line.Offset - startOff > 1)
                    foldings.Add(new NewFolding(startOff, line.Offset));
            }
        }
    }

    private static void AddRegionFoldings(TextDocument document, List<NewFolding> foldings)
    {
        var regionStack = new Stack<(int off, bool isIf)>();
        string[] regionStarts = ["#region", "#if", "#else", "#elif"];
        string[] regionEnds = ["#endregion", "#endif"];

        for (int i = 1; i <= document.LineCount; i++)
        {
            var line = document.GetLineByNumber(i);
            var text = document.GetText(line).Trim();
            foreach (var s in regionStarts)
                if (text.StartsWith(s)) { regionStack.Push((line.Offset, s is "#if" or "#else" or "#elif")); break; }
            foreach (var e in regionEnds)
                if (text.StartsWith(e))
                {
                    if (regionStack.Count > 0)
                    {
                        var (startOff, _) = regionStack.Pop();
                        if (line.EndOffset > startOff)
                            foldings.Add(new NewFolding(startOff, line.EndOffset));
                    }
                    break;
                }
        }
    }
}
