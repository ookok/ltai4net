using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;

namespace LTAI.Desktop;

/// <summary>
/// 文件浏览器 + 编辑器（AvaloniaEdit + TreeSitter 语法检查）
/// </summary>
public sealed class TextPadView : UserControl
{
    private readonly TreeView _tree;
    private readonly TextEditor _editor;
    private readonly TextBlock _statusBar;
    private readonly StackPanel _editorPanel;
    private readonly Button _toggleBtn;
    private string _rootDir;
    private string? _currentFile;
    private bool _isReadOnly = true;

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

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new(4), Spacing = 4,
            Children = { _toggleBtn, checkBtn, saveBtn },
        };

        _editorPanel = new StackPanel { Children = { toolbar, _editor, _statusBar } };

        var split = new Grid();
        split.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(280)));
        split.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

        var treeScroll = new ScrollViewer { Content = _tree };
        Grid.SetColumn(treeScroll, 0); split.Children.Add(treeScroll);
        var editorScroll = new ScrollViewer { Content = _editorPanel };
        Grid.SetColumn(editorScroll, 1); split.Children.Add(editorScroll);

        Content = split;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.S && e.KeyModifiers == KeyModifiers.Control) { SaveFile(); e.Handled = true; }
            if (e.Key == Key.E && e.KeyModifiers == KeyModifiers.Control) { ToggleEdit(); e.Handled = true; }
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
                };
                node.Expanded += (_, _) => { if (node.Items.Count == 0) BuildTree(node.Items, d); };
                items.Add(node);
            }
            foreach (var f in Directory.GetFiles(dir).Where(f => TextExts.Contains(Path.GetExtension(f))).OrderBy(Path.GetFileName))
            {
                var icon = f.EndsWith(".md") ? "📝" : f.EndsWith(".cs") ? "📄" : f.EndsWith(".py") ? "🐍" : "📄";
                var node = new TreeViewItem { Header = $"{icon} {Path.GetFileName(f)}", Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary), Tag = f };
                items.Add(node);
            }
        }
        catch { }
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
            _editor.Load(path);
            var ext = Path.GetExtension(path);
            var hlName = ext switch
            {
                ".cs" => "C#", ".py" => "Python", ".js" => "JavaScript", ".ts" => "TypeScript",
                ".go" => "Go", ".rs" => "Rust", ".java" => "Java", ".xml" => "XML",
                ".html" or ".htm" => "HTML", ".css" => "CSS", ".json" => "JSON",
                ".md" => "Markdown", ".yaml" or ".yml" => "YAML",
                ".sh" or ".bash" => "PowerShell", _ => null,
            };
            try { _editor.SyntaxHighlighting = hlName != null ? HighlightingManager.Instance.GetDefinition(hlName) : null; } catch { }
            _editor.IsReadOnly = !CodeExts.Contains(ext);
            _isReadOnly = _editor.IsReadOnly;
            _toggleBtn.Content = _isReadOnly ? "🔓 编辑" : "🔒 只读";
            var fi = new FileInfo(path);
            var lines = File.ReadLines(path).Count();
            _statusBar.Text = $"{path}  |  {FormatSize(fi.Length)}  |  {lines} 行";
        }
        catch { _statusBar.Text = $"无法打开: {path}"; }
    }

    private void RunSyntaxCheck()
    {
        if (_currentFile == null) { _statusBar.Text = "请先打开一个文件"; return; }
        try
        {
            var ext = Path.GetExtension(_currentFile);
            if (ext != ".cs" && ext != ".py" && ext != ".js" && ext != ".ts" && ext != ".go" && ext != ".rs" && ext != ".java")
            { _statusBar.Text = "语法检查仅支持 .cs/.py/.js/.ts/.go/.rs/.java"; return; }

            var parser = new LTAI.Agent.Tools.TreeSitterParser();
            var code = _editor.Text;
            var symbols = parser.ExtractSymbols(code, ext);
            parser.Dispose();

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

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B", < 1024 * 1024 => $"{bytes / 1024} KB", _ => $"{bytes / 1024 / 1024} MB"
    };
}
