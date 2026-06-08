using Avalonia;
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
using LTAI.Core.Debugging;

namespace LTAI.Desktop;

/// <summary>
/// 文件浏览器 + 编辑器（AvaloniaEdit + TreeSitter 语法检查）
/// </summary>
public sealed partial class TextPadView : UserControl
{
    /// <summary>触发时切换到聊天视图，传递上下文信息。</summary>
    public event Action<string>? AskAiRequested;
    private readonly TreeView _tree;
    private readonly TextEditor _editor;
    private readonly TextBlock _statusBar = null!;
    private readonly StackPanel _editorPanel;
    private readonly Button _toggleBtn;
    private readonly ListBox _symbolList;
    private readonly TextBox _buildOutput;
    private readonly Border _gitOutputPanel;
    private readonly TextBlock _gitOutputText;
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

    // ── Debugging integration ──
    internal LTAI.Desktop.Debugging.DapSession DebugSession { get; }
    private LTAI.Desktop.Debugging.BreakpointManager _bpManager = null!;
    private LTAI.Desktop.Debugging.BreakpointMargin _bpMargin = null!;
    private LTAI.Desktop.Debugging.DebugToolbar _debugToolbar = null!;
    private LTAI.Desktop.Debugging.CallStackView _callStackView = null!;
    private LTAI.Desktop.Debugging.VariablesView _variablesView = null!;


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
    public string? GitBranch => _gitBranch;
    private Dictionary<string, string> _gitFileStatus = new();
    private string? _selectedText;
    private Button _gitCommitBtn = null!;
    private Button _gitPullBtn = null!;
    private Button _gitPushBtn = null!;
    private Button _gitBlameBtn = null!;
    private TextBlock _gitBranchLabel = null!;
    private readonly ListBox _problemList;
    private readonly Terminal.TerminalView _terminalView;
    private readonly Button _terminalBtn;
    private FileSystemWatcher? _fileWatcher;
    private Dictionary<string, string>? _blameData;
    private TextEditor? _splitEditor;
    private bool _showSplit;
    private readonly Grid _editorGrid;

    // ── MVVM ──
    private readonly ViewModels.TextPadViewModel? _vm;

    // ── Error detection (P1: 命令失败自动拉起 AI) ──
    private string? _lastError;
    private string? _lastErrorCommand;
    private Button _errorFixBtn = null!;
    public bool HasPendingError => _errorFixBtn?.IsVisible == true;
    private sealed record SymbolItem(string Icon, string Name, int Line);

    public TextPadView(ViewModels.TextPadViewModel? vm = null,
        LTAI.Desktop.Debugging.DebugBridge? debugBridge = null)
    {
        _vm = vm;
        if (vm != null) DataContext = vm;
        _rootDir = vm?.RootDir ?? Directory.GetCurrentDirectory();

        _tree = new TreeView
        {
            MinWidth = 250, MaxWidth = 400,
            Background = LtaiTheme.Sbb(LtaiTheme.Bg),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            SelectionMode = SelectionMode.Multiple,
        };
        _tree.SelectionChanged += OnTreeSelectionChanged;
        _tree.ContextMenu = MakeTreeContextMenu();
        _tree.DoubleTapped += (_, _) =>
        {
            if (_tree.SelectedItem is TreeViewItem item && item.Tag is string path)
            {
                if (Directory.Exists(path)) { item.IsExpanded = !item.IsExpanded; }
                else if (File.Exists(path)) OpenFile(path);
            }
        };
        BuildTree(_tree.Items, _rootDir);

        _editor = new TextEditor
        {
            IsReadOnly = true, ShowLineNumbers = true,
            FontFamily = LtaiTheme.CodeFont, FontSize = 13,
            Background = LtaiTheme.Sbb(LtaiTheme.Bg),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            LineNumbersForeground = LtaiTheme.Sbb(LtaiTheme.TextDim),
            WordWrap = false,
        };
        try { _editor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("C#"); } catch { }

        // 当前行高亮
        _editor.TextArea.TextView.CurrentLineBackground = LtaiTheme.Sbb(LtaiTheme.SurfaceOverlay);
        _editor.TextArea.TextView.CurrentLineBorder = new Pen(LtaiTheme.Sbb(LtaiTheme.CurrentLineBorder), 1);
        _editor.TextArea.SelectionBrush = LtaiTheme.Sbb(LtaiTheme.SelectionBg);
        _editor.TextArea.SelectionCornerRadius = 2;
        _editor.TextArea.SelectionBrush = LtaiTheme.Sbb(LtaiTheme.SelectionBg);
        _editor.TextArea.SelectionCornerRadius = 2;

        // 选中内容追踪（Smart Context Injection）
        _editor.TextArea.SelectionChanged += (_, _) =>
        {
            _selectedText = _editor.SelectedText;
            if (!string.IsNullOrEmpty(_selectedText))
                _statusBar.Text = $"选中 {_selectedText.Length} 字符 · {_currentFile ?? ""}";
            else if (_currentFile != null)
                _statusBar.Text = $"{_currentFile}  |  {FormatSize(new FileInfo(_currentFile).Length)}";
        };

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

        // ── Debugging: DapSession + BreakpointManager ──
        DebugSession = new LTAI.Desktop.Debugging.DapSession();
        _bpManager = new LTAI.Desktop.Debugging.BreakpointManager(_rootDir);
        debugBridge?.SetSession(DebugSession, _bpManager);

        // ── BreakpointMargin — 左侧断点边栏 ──
        _bpMargin = new LTAI.Desktop.Debugging.BreakpointMargin(_bpManager, () => _currentFile);
        _editor.TextArea.LeftMargins.Insert(0, _bpMargin);

        // Paused-line highlighter (yellow background for current paused line)
        var hl = new CurrentLineHighlighter();
        _editor.TextArea.TextView.LineTransformers.Add(hl);

        DebugSession.Stopped += (line, file) =>
        {
            if (file != null && file != _currentFile)
                OpenFile(file);
            _bpMargin.SetPausedLine(line);
            hl.SetPausedLine(line);
        };
        DebugSession.StateChanged += state =>
        {
            if (state is DebugState.Running or DebugState.Terminated)
            {
                _bpMargin.ClearPausedLine();
                hl.ClearPausedLine();
            }
        };

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
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextOnAccent), Margin = new(0, 0, 4, 0),
        };
        _toggleBtn.Click += (_, _) => ToggleEdit();

        var checkBtn = new Button
        {
            Content = "🔍 语法检查", FontSize = 11,
            Background = LtaiTheme.Sbb(LtaiTheme.AccentInfo),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextOnAccent),
        };
        checkBtn.Click += (_, _) => RunSyntaxCheck();

        var saveBtn = new Button
        {
            Content = "💾 保存", FontSize = 11,
            Background = LtaiTheme.Sbb(LtaiTheme.AccentSystem),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextOnAccent),
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
            FontFamily = LtaiTheme.CodeFont,
            FontSize = 11,
            Background = LtaiTheme.Sbb(LtaiTheme.Bg),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            MinHeight = 0, MaxHeight = 200,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };

        // ── Git Terminal Panel ──
        _gitOutputPanel = new Border
        {
            Background = LtaiTheme.Sbb(Color.Parse("#0d1117")),
            BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border),
            BorderThickness = new(1),
            CornerRadius = new CornerRadius(6),
            Padding = new(10),
            Margin = new(0, 4, 0, 0),
            IsVisible = false,
            MaxHeight = 300,
        };
        _gitOutputText = new TextBlock
        {
            FontFamily = LtaiTheme.CodeFont,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = LtaiTheme.Sbb(Color.Parse("#c9d1d9")),
        };
        _gitOutputPanel.Child = _gitOutputText;

        _buildBtn = new Button { Content = "🛠 Build", FontSize = 10, Height = 20, Background = LtaiTheme.Sbb(LtaiTheme.AccentDNA), Foreground = LtaiTheme.Sbb(LtaiTheme.TextOnAccent), IsVisible = false };
        _buildBtn.Click += (_, _) => RunProjectCmd("build");
        _testBtn = new Button { Content = "🧪 Test", FontSize = 10, Height = 20, Background = LtaiTheme.Sbb(LtaiTheme.AccentInfo), Foreground = LtaiTheme.Sbb(LtaiTheme.TextOnAccent), IsVisible = false };
        _testBtn.Click += (_, _) => RunProjectCmd("test");
        _publishBtn = new Button { Content = "🚀 Deploy", FontSize = 10, Height = 20, Background = LtaiTheme.Sbb(LtaiTheme.AccentSystem), Foreground = LtaiTheme.Sbb(LtaiTheme.TextOnAccent), IsVisible = false };
        _publishBtn.Click += (_, _) => RunProjectCmd("publish");
        _runBtn = new Button { Content = "▶ Run", FontSize = 10, Height = 20, Background = LtaiTheme.Sbb(LtaiTheme.AccentInfo), Foreground = LtaiTheme.Sbb(LtaiTheme.TextOnAccent), IsVisible = false };
        _runBtn.Click += (_, _) => RunProjectCmd("run");

        var debugBtn = new Button { Content = "🐛 Debug", FontSize = 10, Height = 20, Background = LtaiTheme.Sbb(LtaiTheme.AccentWarning), Foreground = LtaiTheme.Sbb(LtaiTheme.TextOnAccent), IsVisible = false };
        debugBtn.Click += (_, _) => _ = this.StartDebugAsync();

        var formatBtn = new Button { Content = "✨ Format", FontSize = 10, Height = 20, Background = LtaiTheme.Sbb(LtaiTheme.AccentWarning), Foreground = LtaiTheme.Sbb(LtaiTheme.TextOnAccent) };
        formatBtn.Click += (_, _) => RunFormat();
        var askAiBtn = new Button { Content = "🤖 问 AI", FontSize = 10, Height = 20, Background = LtaiTheme.Sbb(LtaiTheme.AccentDNA), Foreground = LtaiTheme.Sbb(LtaiTheme.TextOnAccent) };
        askAiBtn.Click += (_, _) => AskAiWithContext(_currentFile ?? _rootDir);
        _errorFixBtn = new Button { Content = "⚡ AI Fix", FontSize = 10, Height = 20, Background = LtaiTheme.Sbb(LtaiTheme.AccentWarning), Foreground = LtaiTheme.Sbb(LtaiTheme.TextOnAccent), IsVisible = false };
        _errorFixBtn.Click += (_, _) =>
        {
            var msg = $"命令执行出错，请分析以下错误并修复：\n```\n{_lastErrorCommand}\n```\n错误信息：\n```\n{_lastError}\n```";
            _lastError = null;
            _lastErrorCommand = null;
            _errorFixBtn.IsVisible = false;
            AskAiRequested?.Invoke(msg);
        };

        var wrapBtn = new Button { Content = "⤫ 换行", FontSize = 10, Height = 20, Background = LtaiTheme.Sbb(LtaiTheme.BgPanel), Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim) };
        wrapBtn.Click += (_, _) => { _editor.WordWrap = !_editor.WordWrap; wrapBtn.Content = _editor.WordWrap ? "⤫ 已换行" : "⤫ 换行"; };
        var gotoBtn = new Button { Content = "↕ 跳转", FontSize = 10, Height = 20, Background = LtaiTheme.Sbb(LtaiTheme.BgPanel), Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim) };
        gotoBtn.Click += (_, _) => ShowGoToLineDialog();

        // ── 分屏按钮 ──
        var splitBtn = new Button { Content = "📐 分屏", FontSize = 10, Height = 20, Background = LtaiTheme.Sbb(LtaiTheme.BgPanel), Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim) };
        splitBtn.Click += (_, _) => ToggleSplitView();

        // ── Git 按钮 ──
        _gitBranchLabel = new TextBlock { FontSize = 11, Foreground = LtaiTheme.Sbb(LtaiTheme.AccentInfo), IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
        _gitCommitBtn = new Button { Content = "💾 Commit", FontSize = 10, Height = 20, Background = LtaiTheme.Sbb(LtaiTheme.AccentSystem), Foreground = LtaiTheme.Sbb(LtaiTheme.TextOnAccent), IsVisible = false };
        _gitCommitBtn.Click += (_, _) => ShowGitCommitDialog();
        _gitPullBtn = new Button { Content = "⬇ Pull", FontSize = 10, Height = 20, Background = LtaiTheme.Sbb(LtaiTheme.AccentInfo), Foreground = LtaiTheme.Sbb(LtaiTheme.TextOnAccent), IsVisible = false };
        _gitPullBtn.Click += async (_, _) => await RunGitCmdAsync("pull");
        _gitPushBtn = new Button { Content = "⬆ Push", FontSize = 10, Height = 20, Background = LtaiTheme.Sbb(LtaiTheme.AccentDNA), Foreground = LtaiTheme.Sbb(LtaiTheme.TextOnAccent), IsVisible = false };
        _gitPushBtn.Click += async (_, _) => await RunGitCmdAsync("push");
        _gitBlameBtn = new Button { Content = "👤 Blame", FontSize = 10, Height = 20, Background = LtaiTheme.Sbb(LtaiTheme.AccentWarning), Foreground = LtaiTheme.Sbb(LtaiTheme.TextOnAccent), IsVisible = false };
        _gitBlameBtn.Click += (_, _) => ToggleBlame();

        // ── 构建/Shell 输出容器 ──
        _buildPanel = new StackPanel { Spacing = 4, Margin = new(0, 4, 0, 0) };

        // ── Debug toolbar (only visible during debug session) ──
        _debugToolbar = new LTAI.Desktop.Debugging.DebugToolbar(DebugSession);

        // ── 终端面板 (必须在 toolbar 之前创建，因为 toolbar 引用 _terminalBtn) ──
        _terminalView = new Terminal.TerminalView { WorkingDirectory = _rootDir, IsVisible = false };
        _terminalBtn = new Button { Content = "📟 终端", FontSize = 10, Height = 20, Background = LtaiTheme.Sbb(LtaiTheme.BgPanel), Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim) };
        _terminalBtn.Click += (_, _) =>
        {
            _terminalView.IsVisible = !_terminalView.IsVisible;
            if (_terminalView.IsVisible) _terminalView.Start();
            else _terminalView.Stop();
        };

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new(4), Spacing = 4,
            Children = { _toggleBtn, checkBtn!, saveBtn!, formatBtn!, askAiBtn!, _errorFixBtn, wrapBtn!, gotoBtn!, splitBtn!, debugBtn!, _runBtn, _terminalBtn, _gitBranchLabel, _gitCommitBtn, _gitPullBtn, _gitPushBtn, _gitBlameBtn },
        };

        _editorGrid = new Grid();
        _editorGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        _editorGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        Grid.SetColumn(_editor, 0);
        Grid.SetRow(_editor, 0);
        _editorGrid.Children.Add(_editor);

        // ── Debug bottom panel (CallStack + Variables) ──
        _callStackView = new LTAI.Desktop.Debugging.CallStackView(DebugSession);
        _variablesView = new LTAI.Desktop.Debugging.VariablesView(DebugSession);
        var debugBottomTab = new TabControl
        {
            IsVisible = false,
            MinHeight = 120, MaxHeight = 250,
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
        };
        var callStackTab = new TabItem { Header = "📋 调用栈", Content = _callStackView };
        var variablesTab = new TabItem { Header = "🔍 变量", Content = _variablesView.CreateWithWatch() };
        debugBottomTab.Items.Add(callStackTab);
        debugBottomTab.Items.Add(variablesTab);

        // Show debug panels when session is active
        DebugSession.StateChanged += state =>
        {
            debugBottomTab.IsVisible = state is DebugState.Paused
                or DebugState.Running
                or DebugState.Launching;
            _bpMargin.IsVisible = state >= DebugState.Launching;
        };

        var editorAndDebug = new DockPanel();
        DockPanel.SetDock(debugBottomTab, Dock.Bottom);
        editorAndDebug.Children.Add(_editorGrid);
        editorAndDebug.Children.Add(debugBottomTab);

        _editorPanel = new StackPanel { Children = { _debugToolbar, toolbar, editorAndDebug, _statusBar, _buildPanel, _terminalView } };

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
        _buildPanel.Children.Add(_buildOutput);
        _buildPanel.Children.Add(_gitOutputPanel);

        // ── 项目搜索框（树上方） ──
        var searchBox = new TextBox
        {
            PlaceholderText = "🔍 搜索文件名 (Ctrl+P)...",
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

            // Debugging keyboard shortcuts
            if (e.Key == Key.F5 && e.KeyModifiers == KeyModifiers.None)
            {
                if (DebugSession.State == DebugState.Idle)
                    _ = this.StartDebugAsync();
                else
                    _ = DebugSession.ContinueAsync();
                e.Handled = true;
            }
            if (e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.None && DebugSession.State == DebugState.Paused)
            { _ = DebugSession.StepOverAsync(); e.Handled = true; }
            if (e.Key == Key.F11 && e.KeyModifiers == KeyModifiers.None && DebugSession.State == DebugState.Paused)
            { _ = DebugSession.StepIntoAsync(); e.Handled = true; }
            if (e.Key == Key.F5 && e.KeyModifiers == KeyModifiers.Shift && DebugSession.State >= DebugState.Running)
            { _ = DebugSession.TerminateAsync(); e.Handled = true; }
            // P1: Ctrl+Alt+. → 拉起 AI 修复
            if (e.Key == Key.OemPeriod && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Alt) && _errorFixBtn.IsVisible)
            {
                AskAiRequested?.Invoke($"命令执行出错，请分析以下错误并修复：\n```\n{_lastErrorCommand}\n```\n错误信息：\n```\n{_lastError}\n```");
                _lastError = null; _lastErrorCommand = null; _errorFixBtn.IsVisible = false;
                e.Handled = true;
            }
        };
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
        var files = new HashSet<string>(Directory.GetFiles(_rootDir).Select(f => Path.GetFileName(f) ?? ""), StringComparer.OrdinalIgnoreCase);
        var allFiles = new HashSet<string>(Directory.GetFiles(_rootDir, "*.*", SearchOption.TopDirectoryOnly).Select(f => Path.GetFileName(f) ?? ""), StringComparer.OrdinalIgnoreCase);

        string type, buildCmd = "", testCmd = "", runCmd = "", deployCmd = "";

        if (allFiles.Any(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)) || allFiles.Any(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        { type = "C# .NET"; buildCmd = "dotnet build"; testCmd = "dotnet test"; runCmd = "dotnet run"; deployCmd = "dotnet publish -c Release"; }
        else if (files.Contains("Cargo.toml"))
        { type = "Rust"; buildCmd = "cargo build"; testCmd = "cargo test"; runCmd = "cargo run"; }
        else if (files.Contains("go.mod"))
        { type = "Go"; buildCmd = "go build ./..."; testCmd = "go test ./..."; runCmd = "go run ."; }
        else if (files.Contains("package.json"))
        { type = "Node.js"; buildCmd = "npm run build"; testCmd = "npm test"; runCmd = "npm start"; deployCmd = "npm publish"; }
        else if (files.Contains("pyproject.toml") || files.Contains("setup.py") || files.Contains("requirements.txt"))
        { type = "Python"; testCmd = "python -m pytest"; runCmd = "python main.py"; buildCmd = files.Contains("pyproject.toml") ? "python -m build" : ""; }
        else if (files.Contains("pom.xml"))
        { type = "Java (Maven)"; buildCmd = "mvn compile"; testCmd = "mvn test"; runCmd = "mvn exec:java"; deployCmd = "mvn deploy"; }
        else if (files.Contains("build.gradle") || files.Contains("build.gradle.kts"))
        { type = "Java (Gradle)"; buildCmd = "gradle build"; testCmd = "gradle test"; runCmd = "gradle run"; }
        else
        { type = "unknown"; }

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

            // P1: 捕获错误，显示 AI Fix 按钮
            if (process.ExitCode != 0)
            {
                _lastErrorCommand = command;
                _lastError = $"[stderr]\n{error}\n[stdout]\n{output}";
                _errorFixBtn.IsVisible = true;

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
        var input = new TextBox { PlaceholderText = $"行号 (1-{_editor.Document.LineCount})" };
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

    private async Task StartDebugAsync()
    {
        if (this.DebugSession.State != DebugState.Idle) return;

        var csprojFiles = Directory.GetFiles(this._rootDir, "*.csproj", SearchOption.AllDirectories);
        var selectedProject = csprojFiles.FirstOrDefault();
        if (selectedProject == null)
        {
            this._statusBar.Text = "未找到 .csproj 文件，无法启动调试器";
            return;
        }

        var projectDir = Path.GetDirectoryName(selectedProject)!;
        var projectName = Path.GetFileNameWithoutExtension(selectedProject);
        var outputDir = Path.Combine(projectDir, "bin", "Debug", "net10.0");
        var programPath = Path.Combine(outputDir, $"{projectName}.dll");

        this._statusBar.Text = $"Building {projectName}...";
        try
        {
            var psi = new ProcessStartInfo("dotnet", $"build -c Debug \"{selectedProject}\"")
            {
                WorkingDirectory = projectDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            var build = Process.Start(psi)!;
            var buildOut = await build.StandardOutput.ReadToEndAsync();
            await build.WaitForExitAsync();

            if (build.ExitCode != 0)
            {
                this._buildOutput.Text = $"Build failed:\n{buildOut}\n{build.StandardError.ReadToEnd()}";
                this._buildOutput.IsVisible = true;
                this._statusBar.Text = "Build failed";
                return;
            }
        }
        catch (Exception ex)
        {
            this._statusBar.Text = $"Build error: {ex.Message}";
            return;
        }

        var allBps = this._bpManager.All;
        foreach (var bp in allBps)
        {
            var absPath = Path.IsPathRooted(bp.File) ? bp.File : Path.Combine(this._rootDir, bp.File);
            if (File.Exists(absPath))
            {
                await this.DebugSession.SetBreakpointsAsync(absPath, [bp.Line]);
            }
        }

        await this.DebugSession.LaunchAsync("dotnet",
            ["debug", "--debug-adapter", programPath],
            projectDir,
            new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "coreclr",
                ["name"] = $".NET Launch ({projectName})",
                ["program"] = programPath,
                ["cwd"] = projectDir,
                ["stopAtEntry"] = false,
            });

        _statusBar.Text = $"Debugging {projectName}";
    }
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

/// <summary>
/// DocumentColorizingTransformer for highlighting the current paused line.
/// </summary>
file sealed class CurrentLineHighlighter : AvaloniaEdit.Rendering.DocumentColorizingTransformer
{
    private int _pausedLine = -1;

    public void SetPausedLine(int line) { _pausedLine = line; }
    public void ClearPausedLine() { _pausedLine = -1; }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (line.LineNumber != _pausedLine) return;
        ChangeLinePart(line.Offset, line.EndOffset, el =>
        {
            el.TextRunProperties.SetBackgroundBrush(
                new Avalonia.Media.SolidColorBrush(
                    Avalonia.Media.Color.Parse("#3d3d1a")));
        });
    }
}
