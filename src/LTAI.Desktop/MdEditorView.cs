using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using LTAI.Knowledge.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace LTAI.Desktop;

public sealed class MdEditorView : UserControl
{
    private readonly string _workspaceRoot;
    private TreeView _fileTree;
    private TextBox _mdEditor;
    private TextEditor _codeEditor;
    private ScrollViewer _editorScroller;
    private ScrollViewer _previewScroller;
    private StackPanel _previewPanel;
    private readonly TextBlock _statusBar;
    private Button? _saveBtn;
    private readonly DispatcherTimer _previewTimer;
    private string? _currentFile;
    private bool _isCodeFile;
    private string _detectedLanguage = "plaintext";

    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".csx", ".py", ".js", ".ts", ".tsx", ".jsx",
        ".go", ".rs", ".java", ".kt", ".swift", ".c", ".cpp", ".h", ".hpp",
        ".php", ".rb", ".vue", ".svelte", ".html", ".css", ".scss",
        ".json", ".xml", ".yaml", ".yml", ".toml", ".sh", ".ps1",
        ".sql", ".graphql", ".proto", ".dockerfile", ".env", ".gitignore"
    };

    private static readonly Dictionary<string, string> LanguageMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "C#", [".csproj"] = "MSBuild", [".csx"] = "C# Script",
        [".py"] = "Python", [".js"] = "JavaScript", [".ts"] = "TypeScript",
        [".tsx"] = "TSX", [".jsx"] = "JSX", [".go"] = "Go",
        [".rs"] = "Rust", [".java"] = "Java", [".kt"] = "Kotlin",
        [".swift"] = "Swift", [".c"] = "C", [".cpp"] = "C++",
        [".h"] = "C/C++ Header", [".hpp"] = "C++ Header",
        [".php"] = "PHP", [".rb"] = "Ruby", [".vue"] = "Vue",
        [".svelte"] = "Svelte", [".html"] = "HTML", [".css"] = "CSS",
        [".scss"] = "SCSS", [".json"] = "JSON", [".xml"] = "XML",
        [".yaml"] = "YAML", [".yml"] = "YAML", [".toml"] = "TOML",
        [".sh"] = "Shell", [".ps1"] = "PowerShell", [".sql"] = "SQL",
        [".graphql"] = "GraphQL", [".proto"] = "Protobuf"
    };

    private string EditorText
    {
        get => _isCodeFile ? _codeEditor.Text : (_mdEditor.Text ?? "");
        set
        {
            if (_isCodeFile)
                _codeEditor.Text = value;
            else
                _mdEditor.Text = value;
        }
    }

    public MdEditorView()
    {
        _workspaceRoot = OptionService.Get("LTAI_WORKSPACE")
                      ?? Environment.GetEnvironmentVariable("LTAI_WORKSPACE")
                      ?? Directory.GetCurrentDirectory();

        Background = LtaiTheme.Sbb(LtaiTheme.Bg);

        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _previewTimer.Tick += (_, _) => RefreshPreview();

        var mainGrid = new Grid
        {
            ColumnDefinitions = new("240,*,320"),
            RowDefinitions = new("Auto,*")
        };

        var toolbar = BuildToolbar();
        Grid.SetRow(toolbar, 0);
        Grid.SetColumnSpan(toolbar, 3);
        mainGrid.Children.Add(toolbar);

        var fileTreePanel = BuildFileTree();
        Grid.SetRow(fileTreePanel, 1);
        Grid.SetColumn(fileTreePanel, 0);
        mainGrid.Children.Add(fileTreePanel);

        var editorPanel = BuildEditor();
        Grid.SetRow(editorPanel, 1);
        Grid.SetColumn(editorPanel, 1);
        mainGrid.Children.Add(editorPanel);

        var previewPanelWrapper = BuildPreview();
        Grid.SetRow(previewPanelWrapper, 1);
        Grid.SetColumn(previewPanelWrapper, 2);
        mainGrid.Children.Add(previewPanelWrapper);

        Content = mainGrid;

        _statusBar = new TextBlock
        {
            Text = "Ready",
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
            FontFamily = new("Consolas"),
            FontSize = 11,
            Margin = new(8, 0)
        };

        PopulateFileTree();

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel != null)
            topLevel.KeyDown += OnGlobalKeyDown;
    }

    private Border BuildToolbar()
    {
        var panel = new DockPanel
        {
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            Margin = new(0, 0, 0, 0)
        };

        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };

        var newBtn = new Button
        {
            Content = "New",
            Width = 50,
            Background = LtaiTheme.Sbb(LtaiTheme.AccentSystem),
            Foreground = Brushes.White,
            FontSize = 11,
            FontWeight = FontWeight.Bold
        };
        newBtn.Click += OnNewFile;
        left.Children.Add(newBtn);

        _saveBtn = new Button
        {
            Content = "Save",
            Width = 50,
            Background = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
            Foreground = Brushes.White,
            FontSize = 11,
            IsEnabled = false
        };
        _saveBtn.Click += async (_, _) => await SaveFileAsync();
        left.Children.Add(_saveBtn);

        var refreshBtn = new Button
        {
            Content = "Refresh",
            Width = 56,
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
            FontSize = 11
        };
        refreshBtn.Click += (_, _) => PopulateFileTree();
        left.Children.Add(refreshBtn);

        panel.Children.Add(left);

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(_statusBar);
        panel.Children.Add(right);

        return new Border
        {
            BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border),
            BorderThickness = new(0, 0, 0, 1),
            Child = panel
        };
    }

    private Border BuildFileTree()
    {
        _fileTree = new TreeView
        {
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            BorderThickness = new(0)
        };

        _fileTree.DoubleTapped += OnFileTreeDoubleTapped;

        var header = new TextBlock
        {
            Text = "Markdown Files",
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
            FontSize = 10,
            FontWeight = FontWeight.Bold,
            Margin = new(8, 4, 0, 4)
        };

        var panel = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);
        panel.Children.Add(new ScrollViewer { Content = _fileTree });

        return new Border
        {
            BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border),
            BorderThickness = new(0, 0, 1, 0),
            Child = panel
        };
    }

    private Border BuildEditor()
    {
        _mdEditor = new TextBox
        {
            FontFamily = new("Consolas"),
            FontSize = 13,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            Background = LtaiTheme.Sbb(Color.Parse("#0d1117")),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new(0)
        };
        _mdEditor.TextChanged += OnEditorTextChanged;

        _codeEditor = new TextEditor
        {
            FontFamily = new("Cascadia Code, Consolas, monospace"),
            FontSize = 13,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            Background = LtaiTheme.Sbb(Color.Parse("#0a0e14")),
            ShowLineNumbers = true,
            WordWrap = false,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Options = { ShowTabs = false, ShowSpaces = false, ConvertTabsToSpaces = true, IndentationSize = 4 }
        };
        _codeEditor.TextChanged += OnEditorTextChanged;

        _mdEditor.IsVisible = true;
        _codeEditor.IsVisible = false;

        var editorStack = new Grid();
        editorStack.Children.Add(_mdEditor);
        editorStack.Children.Add(_codeEditor);

        _editorScroller = new ScrollViewer { Content = editorStack };

        return new Border
        {
            BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border),
            BorderThickness = new(1, 0),
            Child = _editorScroller
        };
    }

    private Border BuildPreview()
    {
        _previewPanel = new StackPanel { Spacing = 4, Margin = new(12) };
        _previewScroller = new ScrollViewer { Content = _previewPanel };

        var header = new TextBlock
        {
            Text = "Preview",
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
            FontSize = 10,
            FontWeight = FontWeight.Bold,
            Margin = new(8, 4, 0, 4)
        };

        var panel = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);
        panel.Children.Add(_previewScroller);

        return new Border
        {
            BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border),
            BorderThickness = new(1, 0, 0, 0),
            Child = panel
        };
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void PopulateFileTree()
    {
        _fileTree.Items.Clear();

        WalkDir("skills", "Skills");
        WalkDir("prompts", "Prompts");
        WalkDir("tools", "Tools");
        WalkDir("memory", "Memory");
        WalkDir("config", "Config");

        WalkCodeDir("src", "Source Code");
    }

    private void WalkDir(string relPath, string label)
    {
        var absPath = Path.Combine(_workspaceRoot, relPath);
        if (!Directory.Exists(absPath)) return;
        var rootItem = new TreeViewItem { Header = label, IsExpanded = true };
        AddDirectory(rootItem, absPath);
        _fileTree.Items.Add(rootItem);
    }

    private static void AddDirectory(TreeViewItem parent, string dir)
    {
        foreach (var subDir in Directory.GetDirectories(dir).OrderBy(Path.GetFileName))
        {
            var name = Path.GetFileName(subDir);
            if (name.StartsWith('.') || name == "versions" || name == "obj") continue;
            var item = new TreeViewItem { Header = name };
            AddDirectory(item, subDir);
            parent.Items.Add(item);
        }

        foreach (var file in Directory.GetFiles(dir, "*.md").OrderBy(Path.GetFileName))
        {
            var name = Path.GetFileName(file);
            var item = new TreeViewItem
            {
                Header = name,
                Tag = file
            };
            parent.Items.Add(item);
        }
    }

    private void WalkCodeDir(string relPath, string label)
    {
        var absPath = Path.Combine(_workspaceRoot, relPath);
        if (!Directory.Exists(absPath)) return;
        var rootItem = new TreeViewItem { Header = label, IsExpanded = false };
        AddCodeDirectory(rootItem, absPath);
        _fileTree.Items.Add(rootItem);
    }

    private static void AddCodeDirectory(TreeViewItem parent, string dir, int depth = 0)
    {
        if (depth > 4) return;

        foreach (var subDir in Directory.GetDirectories(dir).OrderBy(Path.GetFileName))
        {
            var name = Path.GetFileName(subDir);
            if (name.StartsWith('.') || name is "obj" or "bin" or "node_modules" or "dist") continue;
            var item = new TreeViewItem { Header = name };
            AddCodeDirectory(item, subDir, depth + 1);
            if (item.Items.Count > 0 || Directory.GetFiles(subDir).Any(f => CodeExtensions.Contains(Path.GetExtension(f))))
                parent.Items.Add(item);
        }

        foreach (var file in Directory.GetFiles(dir).OrderBy(Path.GetFileName))
        {
            var ext = Path.GetExtension(file);
            if (!CodeExtensions.Contains(ext)) continue;
            var item = new TreeViewItem
            {
                Header = Path.GetFileName(file),
                Tag = file
            };
            parent.Items.Add(item);
        }
    }

    private async void OnFileTreeDoubleTapped(object? sender, RoutedEventArgs e)
    {
        var item = _fileTree.SelectedItem as TreeViewItem;
        var path = item?.Tag as string;
        if (path == null || !File.Exists(path)) return;

        await OpenFileAsync(path);
    }

    private async Task OpenFileAsync(string path)
    {
        try
        {
            var content = await File.ReadAllTextAsync(path);
            var ext = Path.GetExtension(path);
            _isCodeFile = CodeExtensions.Contains(ext);
            _detectedLanguage = LanguageMap.GetValueOrDefault(ext, ext.Length > 0 ? ext.TrimStart('.').ToUpper() : "plaintext");

            if (_isCodeFile)
            {
                _mdEditor.IsVisible = false;
                _codeEditor.IsVisible = true;
                SetupSyntaxHighlighting(ext);
                _codeEditor.Text = content;
                _ = CheckSyntaxAsync(path, content, _detectedLanguage);
            }
            else
            {
                _codeEditor.IsVisible = false;
                _mdEditor.IsVisible = true;
                _mdEditor.Text = content;
            }

            _currentFile = path;
            _saveBtn.IsEnabled = true;
            UpdateStatusForCurrentFile();
            RefreshPreview();
        }
        catch (Exception ex)
        {
            _statusBar.Text = $"Error: {ex.Message}";
        }
    }

    private void SetupSyntaxHighlighting(string ext)
    {
        var highlightingName = ext.ToLowerInvariant() switch
        {
            ".cs" => "C#",
            ".csproj" => "XML",
            ".xml" => "XML",
            ".html" => "HTML",
            ".css" => "CSS",
            ".js" => "JavaScript",
            ".ts" => "TypeScript",
            ".json" => "JSON",
            ".py" => "Python",
            ".java" => "Java",
            ".php" => "PHP",
            ".cpp" or ".c" or ".h" or ".hpp" => "C++",
            ".sql" => "SQL",
            ".yaml" or ".yml" => "YAML",
            _ => null
        };

        if (highlightingName != null)
        {
            var highlighting = HighlightingManager.Instance.GetDefinition(highlightingName);
            if (highlighting != null)
                _codeEditor.SyntaxHighlighting = highlighting;
        }
    }

    private sealed record DiagnosticItem(int Line, string Message, string Severity);

    private async Task CheckSyntaxAsync(string path, string code, string language)
    {
        var items = await Task.Run(() => language switch
        {
            "C#" or "C# Script" => CheckCSharp(code, path),
            "JSON" => CheckJson(code),
            "XML" or "MSBuild" or "HTML" => CheckXml(code),
            "YAML" => CheckYaml(code),
            "Python" => CheckPython(code),
            "JavaScript" or "TypeScript" or "TSX" or "JSX" => CheckJavaScript(code),
            "SQL" => CheckSql(code),
            _ => new List<DiagnosticItem>()
        });

        Dispatcher.UIThread.Post(() => ShowDiagnostics(items));
    }

    private static List<DiagnosticItem> CheckCSharp(string code, string path)
    {
        try
        {
            var tree = CSharpSyntaxTree.ParseText(code, path: path);
            return tree.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => new DiagnosticItem(
                    d.Location.GetLineSpan().StartLinePosition.Line + 1,
                    d.GetMessage(),
                    d.Severity == DiagnosticSeverity.Error ? "error" : "warning"))
                .ToList();
        }
        catch { return new(); }
    }

    private static List<DiagnosticItem> CheckJson(string code)
    {
        try
        {
            System.Text.Json.JsonDocument.Parse(code);
            return new();
        }
        catch (System.Text.Json.JsonException ex)
        {
            return new() { new(GetJsonErrorLine(code, ex.BytePositionInLine > 0 ? -1 : 0), $"JSON: {ex.Message}", "error") };
        }
    }

    private static int GetJsonErrorLine(string code, long _)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(code);
            return 0; // no error
        }
        catch (System.Text.Json.JsonException ex)
        {
            var offset = ex.BytePositionInLine > 0 ? (int)ex.BytePositionInLine : code.Length;
            var lines = code[..Math.Min(offset, code.Length)].Split('\n');
            return lines.Length;
        }
    }

    private static List<DiagnosticItem> CheckXml(string code)
    {
        try
        {
            using var reader = System.Xml.XmlReader.Create(new System.IO.StringReader($"<root>{code}</root>"),
                new System.Xml.XmlReaderSettings { ConformanceLevel = System.Xml.ConformanceLevel.Fragment });
            while (reader.Read()) { }
            return new();
        }
        catch (System.Xml.XmlException ex)
        {
            return new() { new(ex.LineNumber, $"XML: {ex.Message}", "error") };
        }
    }

    private static List<DiagnosticItem> CheckYaml(string code)
    {
        var items = new List<DiagnosticItem>();
        var lines = code.Split('\n');
        var indentStack = new Stack<int>();
        indentStack.Push(0);
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;
            var indent = line.Length - line.TrimStart().Length;
            if (line.Contains('\t') && line.TrimStart().StartsWith('-'))
                items.Add(new(i + 1, "YAML: tabs not allowed (use spaces)", "warning"));

            if (indent > indentStack.Peek() && indent - indentStack.Peek() > 1 && !line.TrimStart().StartsWith('-'))
                items.Add(new(i + 1, "YAML: inconsistent indentation", "warning"));

            if (indent > 0 && indent != indentStack.Peek())
                indentStack.Push(indent);
        }
        return items;
    }

    private static List<DiagnosticItem> CheckPython(string code)
    {
        var items = new List<DiagnosticItem>();
        var lines = code.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;
            if (line.Contains('\t'))
                items.Add(new(i + 1, "Python: use spaces, not tabs (PEP 8)", "warning"));

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("def ") && !trimmed.EndsWith(':'))
                items.Add(new(i + 1, "Python: function definition missing ':'", "error"));
            if (trimmed.StartsWith("class ") && !trimmed.EndsWith(':'))
                items.Add(new(i + 1, "Python: class definition missing ':'", "error"));
            if ((trimmed.StartsWith("if ") || trimmed.StartsWith("elif ") || trimmed.StartsWith("else")) && !trimmed.EndsWith(':') && !trimmed.StartsWith("else"))
                items.Add(new(i + 1, "Python: condition missing ':'", "error"));
            if ((trimmed.StartsWith("for ") || trimmed.StartsWith("while ")) && !trimmed.EndsWith(':'))
                items.Add(new(i + 1, "Python: loop missing ':'", "error"));
            if (trimmed.StartsWith("try:") && !trimmed.EndsWith(':'))
                items.Add(new(i + 1, "Python: try block missing ':'", "error"));
        }
        return items;
    }

    private static List<DiagnosticItem> CheckJavaScript(string code)
    {
        var items = new List<DiagnosticItem>();
        var lines = code.Split('\n');
        var braceDepth = 0;
        var parenDepth = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            braceDepth += line.Count(c => c == '{') - line.Count(c => c == '}');
            parenDepth += line.Count(c => c == '(') - line.Count(c => c == ')');
            if (braceDepth < 0) { items.Add(new(i + 1, "JS: unexpected '}'", "error")); braceDepth = 0; }
            if (parenDepth < 0) { items.Add(new(i + 1, "JS: unexpected ')'", "error")); parenDepth = 0; }
        }
        if (braceDepth > 0) items.Add(new(lines.Length, $"JS: {braceDepth} unclosed '{{'", "warning"));
        if (parenDepth > 0) items.Add(new(lines.Length, $"JS: {parenDepth} unclosed '('", "warning"));
        return items;
    }

    private static List<DiagnosticItem> CheckSql(string code)
    {
        var items = new List<DiagnosticItem>();
        var upper = code.ToUpperInvariant();
        var keywords = new[] { "SELECT", "FROM", "WHERE", "INSERT", "UPDATE", "DELETE", "CREATE", "DROP", "ALTER" };
        var hasKeyword = keywords.Any(k => upper.Contains(k));
        if (!hasKeyword && code.Length > 10 && !code.TrimStart().StartsWith("--"))
            items.Add(new(1, "SQL: no recognizable SQL keywords found", "warning"));
        if (upper.Contains("DROP") || upper.Contains("DELETE") || upper.Contains("TRUNCATE"))
            items.Add(new(1, "SQL: destructive operation (DROP/DELETE/TRUNCATE)", "warning"));
        return items;
    }

    private void ShowDiagnostics(List<DiagnosticItem> diagnostics)
    {
        var existingPanel = _previewPanel.Children
            .OfType<StackPanel>()
            .FirstOrDefault(p => p.Name == "DiagnosticsPanel");
        if (existingPanel != null)
            _previewPanel.Children.Remove(existingPanel);

        if (diagnostics.Count == 0)
        {
            _previewPanel.Children.Add(new TextBlock
            {
                Text = "✓ No issues detected",
                Foreground = LtaiTheme.Sbb(LtaiTheme.AccentSystem),
                FontSize = 11,
                Margin = new(0, 4, 0, 0),
                Name = "DiagnosticsPanel"
            });
            return;
        }

        var panel = new StackPanel { Name = "DiagnosticsPanel", Spacing = 4, Margin = new(0, 8, 0, 0) };

        var errors = diagnostics.Count(d => d.Severity == "error");
        var warnings = diagnostics.Count(d => d.Severity == "warning");
        var summary = errors > 0 ? $"Errors: {errors}, Warnings: {warnings}" : $"Warnings: {warnings}";
        panel.Children.Add(new TextBlock
        {
            Text = summary,
            Foreground = errors > 0 ? Brushes.OrangeRed : Brushes.Goldenrod,
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            Margin = new(0, 0, 0, 4)
        });

        foreach (var d in diagnostics.Take(15))
        {
            var color = d.Severity == "error" ? Colors.OrangeRed : Colors.Goldenrod;
            panel.Children.Add(new TextBlock
            {
                Text = $"  [{d.Line}] {d.Message}",
                Foreground = LtaiTheme.Sbb(color),
                FontFamily = new("Consolas"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap
            });
        }

        _previewPanel.Children.Add(panel);
    }

    private void ShowDiagnostics(List<Microsoft.CodeAnalysis.Diagnostic> diagnostics)
    {
        var existingPanel = _previewPanel.Children
            .OfType<StackPanel>()
            .FirstOrDefault(p => p.Name == "DiagnosticsPanel");
        if (existingPanel != null)
            _previewPanel.Children.Remove(existingPanel);

        if (diagnostics.Count == 0) return;

        var panel = new StackPanel { Name = "DiagnosticsPanel", Spacing = 4, Margin = new(0, 8, 0, 0) };

        panel.Children.Add(new TextBlock
        {
            Text = $"Errors: {diagnostics.Count}",
            Foreground = Brushes.OrangeRed,
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            Margin = new(0, 0, 0, 4)
        });

        foreach (var d in diagnostics.Take(10))
        {
            var line = d.Location.GetLineSpan().StartLinePosition.Line + 1;
            panel.Children.Add(new TextBlock
            {
                Text = $"  [{line}] {d.GetMessage()}",
                Foreground = LtaiTheme.Sbb(Colors.OrangeRed),
                FontFamily = new("Consolas"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap
            });
        }

        _previewPanel.Children.Add(panel);
    }

    private void UpdateStatusForCurrentFile()
    {
        if (_currentFile == null) return;
        var fileName = Path.GetFileName(_currentFile);
        var lines = EditorText.Count(c => c == '\n') + 1;
        var lang = _isCodeFile ? $" | {_detectedLanguage}" : " | Markdown";
        _statusBar.Text = $"{fileName} | {lines} lines{lang}";
    }

    private async Task SaveFileAsync()
    {
        if (_currentFile == null) return;
        try
        {
            await File.WriteAllTextAsync(_currentFile, EditorText);
            UpdateStatusForCurrentFile();
            _statusBar.Text = $"Saved: {Path.GetFileName(_currentFile)}";
        }
        catch (Exception ex)
        {
            _statusBar.Text = $"Save failed: {ex.Message}";
        }
    }

    private async void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            await SaveFileAsync();
        }
    }

    private void RefreshPreview()
    {
        _previewPanel.Children.Clear();
        var text = EditorText;
        if (string.IsNullOrEmpty(text))
        {
            _previewPanel.Children.Add(new TextBlock
            {
                Text = "No content",
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
                FontStyle = FontStyle.Italic,
                FontSize = 12
            });
            return;
        }

        if (_isCodeFile)
        {
            RenderCodePreview(text);
            return;
        }

        var stb = new SelectableTextBlock
        {
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        };
        MarkdownRenderer.Render(text, stb.Inlines!);
        _previewPanel.Children.Add(stb);
    }

    private void RenderCodePreview(string text)
    {
        var lines = text.Split('\n');
        var totalLines = lines.Length;
        var totalChars = text.Length;
        var blankLines = lines.Count(l => string.IsNullOrWhiteSpace(l));

        var header = new TextBlock
        {
            Text = $"Code Preview: {_detectedLanguage}",
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
            FontSize = 14,
            FontWeight = FontWeight.Bold,
            Margin = new(0, 0, 0, 8)
        };

        var fileInfo = new TextBlock
        {
            Text = $"File: {Path.GetFileName(_currentFile ?? "untitled")}",
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };

        var stats = new StackPanel { Spacing = 2, Margin = new(0, 4, 0, 0) };
        stats.Children.Add(new TextBlock { Text = $"{totalChars:N0} characters", Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim), FontSize = 11 });
        stats.Children.Add(new TextBlock { Text = $"{totalLines:N0} lines", Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim), FontSize = 11 });
        stats.Children.Add(new TextBlock { Text = $"{blankLines:N0} blank lines", Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim), FontSize = 11 });

        var outlineText = $"namespace {ExtractNamespace(text) ?? "—"}\n" +
                          $"classes: {ExtractClasses(text)}  methods: {ExtractMethods(text)}";

        var outline = new TextBlock
        {
            Text = outlineText,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
            FontFamily = new("Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new(0, 8, 0, 0)
        };

        _previewPanel.Children.Add(header);
        _previewPanel.Children.Add(fileInfo);
        _previewPanel.Children.Add(stats);
        _previewPanel.Children.Add(new Border { Height = 1, Background = LtaiTheme.Sbb(LtaiTheme.Border), Margin = new(0, 6) });
        _previewPanel.Children.Add(outline);
    }

    private static string? ExtractNamespace(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, @"namespace\s+([\w.]+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static int ExtractClasses(string text)
    {
        return System.Text.RegularExpressions.Regex.Matches(text, @"\b(?:class|struct|record|interface|enum)\s+\w+").Count;
    }

    private static int ExtractMethods(string text)
    {
        return System.Text.RegularExpressions.Regex.Matches(text,
            @"\b(?:public|private|protected|internal|static|async|virtual|override|abstract|sealed)\s+(?:static\s+)?(?:async\s+)?[\w<>\[\],\s]+\s+(\w+)\s*\(").Count;
    }

    private void OnNewFile(object? sender, RoutedEventArgs e)
    {
        var flyout = new MenuFlyout();

        flyout.Items.Add(CreateNewMenuItem("Skill", _ => CreateNewFile("skills/l0_atomic",
            "# skill: new_skill\n" +
            "domain: general\n" +
            "layer: L0\n" +
            "version: 1.0.0\n" +
            "intent: Describe what this skill does\n" +
            "confidence: 0.85\n" +
            "\n" +
            "## triggers\n" +
            "- pattern: \"example trigger\" (weight: 1.0)\n" +
            "\n" +
            "## 步骤\n" +
            "1. Describe step one\n" +
            "2. Describe step two\n" +
            "\n" +
            "## 验证\n" +
            "must_contain: expected output pattern\n")));

        flyout.Items.Add(CreateNewMenuItem("Memory", _ => CreateNewFile("memory",
            "# memory: new_memory\n" +
            "domain: general\n" +
            "confidence: 0.85\n" +
            "\n" +
            "## summary\n" +
            "Brief summary of this memory\n" +
            "\n" +
            "## facts\n" +
            "- Fact one\n" +
            "- Fact two\n" +
            "\n" +
            "## context\n" +
            "When and why this memory was created\n" +
            "\n" +
            "## tags\n" +
            "- general\n" +
            "\n" +
            "## triggers\n" +
            "- pattern: \"example\" (weight: 1.0)\n")));

        flyout.Items.Add(CreateNewMenuItem("Prompt", _ => CreateNewFile("prompts",
            "# prompt: new_prompt\n" +
            "domain: general\n" +
            "description: Describe what this prompt does\n" +
            "\n" +
            "## template\n" +
            "You are a helpful assistant. Your task: {{task_description}}\n" +
            "\nContext: {{context}}\n" +
            "\nRespond concisely.\n" +
            "\n" +
            "## variables\n" +
            "- task_description: What the assistant should do (required)\n" +
            "- context: Additional background info\n" +
            "\n" +
            "## triggers\n" +
            "- pattern: \"example\" (weight: 1.0)\n" +
            "\n" +
            "## tags\n" +
            "- general\n")));

        flyout.Items.Add(CreateNewMenuItem("Tool (Shell)", _ => CreateNewFile("tools",
            "# tool: new_tool\n" +
            "domain: general\n" +
            "type: shell\n" +
            "description: Describe what this tool does\n" +
            "timeout: 60\n" +
            "\n" +
            "## parameters\n" +
            "- input: string (required) — Input parameter description\n" +
            "\n" +
            "## command\n" +
            "echo {{input}}\n" +
            "\n" +
            "## triggers\n" +
            "- pattern: \"example\" (weight: 1.0)\n" +
            "\n" +
            "## tags\n" +
            "- general\n")));

        flyout.Items.Add(CreateNewMenuItem("Tool (Service)", _ => CreateNewFile("tools",
            "# tool: new_service_tool\n" +
            "domain: general\n" +
            "type: service\n" +
            "description: Describe what this tool calls\n" +
            "\n" +
            "## service\n" +
            "service_name: Full.Type.Name\n" +
            "method_name: MethodName\n" +
            "is_static: true\n" +
            "\n" +
            "## parameters\n" +
            "- input: string (required) — Input parameter\n" +
            "\n" +
            "## triggers\n" +
            "- pattern: \"example\" (weight: 1.0)\n" +
            "\n" +
            "## tags\n" +
            "- general\n")));

        flyout.Items.Add(CreateNewMenuItem("Config", _ => CreateNewFile("config",
            "# option: new_config\n" +
            "section: LTAI:NewSection\n" +
            "description: Describe this config section\n" +
            "\n" +
            "## keys\n" +
            "- key_name: string (default: default_value) — Description of this key\n" +
            "- another_key: int (default: 42)\n" +
            "\n" +
            "## tags\n" +
            "- config\n")));

        flyout.Items.Add(new MenuItem { Header = "-" });

        flyout.Items.Add(CreateNewMenuItem("C# File", _ => CreateNewCodeFile("src", ".cs",
            "namespace LTAI;\n" +
            "\n" +
            "public sealed class NewClass\n" +
            "{\n" +
            "    public void DoSomething()\n" +
            "    {\n" +
            "    }\n" +
            "}\n")));

        flyout.Items.Add(CreateNewMenuItem("Python File", _ => CreateNewCodeFile("src", ".py",
            "#!/usr/bin/env python3\n" +
            "\"\"\"Module description.\"\"\"\n" +
            "\n" +
            "\n" +
            "def main():\n" +
            "    pass\n" +
            "\n" +
            "\n" +
            "if __name__ == \"__main__\":\n" +
            "    main()\n")));

        flyout.Items.Add(CreateNewMenuItem("TypeScript File", _ => CreateNewCodeFile("src", ".ts",
            "export interface Options {\n" +
            "    name: string;\n" +
            "}\n" +
            "\n" +
            "export function main(options: Options): void {\n" +
            "    console.log(options.name);\n" +
            "}\n")));

        flyout.Items.Add(CreateNewMenuItem("JSON Config", _ => CreateNewCodeFile("config", ".json",
            "{\n" +
            "    \"name\": \"example\",\n" +
            "    \"version\": \"1.0.0\"\n" +
            "}\n")));

        if (sender is Button btn)
            flyout.ShowAt(btn);
    }

    private static MenuItem CreateNewMenuItem(string header, Action<object?> handler)
    {
        var item = new MenuItem { Header = header };
        item.Click += (s, e) => handler(s);
        return item;
    }

    private async void CreateNewFile(string relDir, string template)
    {
        var dialog = new TextBox
        {
            Watermark = "Enter file name (without .md)",
            FontFamily = new("Consolas"),
            FontSize = 13,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            Background = LtaiTheme.Sbb(LtaiTheme.BgInput),
            MinWidth = 300
        };

        var panel = new StackPanel { Spacing = 8, Margin = new(12) };
        panel.Children.Add(new TextBlock
        {
            Text = $"Create new .md file in {relDir}/",
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            FontSize = 13
        });
        panel.Children.Add(dialog);

        var btnStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
        var cancelBtn = new Button
        {
            Content = "Cancel",
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary)
        };
        var okBtn = new Button
        {
            Content = "Create",
            Background = LtaiTheme.Sbb(LtaiTheme.AccentSystem),
            Foreground = Brushes.White,
            FontWeight = FontWeight.Bold
        };
        btnStack.Children.Add(cancelBtn);
        btnStack.Children.Add(okBtn);
        panel.Children.Add(btnStack);

        var popup = new Border
        {
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border),
            BorderThickness = new(1),
            CornerRadius = new(6),
            Child = panel,
            MaxWidth = 400
        };

        var overlay = new Panel();
        overlay.Children.Add(popup);
        popup.HorizontalAlignment = HorizontalAlignment.Center;
        popup.VerticalAlignment = VerticalAlignment.Center;

        var host = new Border { Child = overlay, Background = LtaiTheme.Sbb(Color.Parse("#66000000")) };
        var container = this.Parent ?? this;
        if (container is Panel parentPanel)
            parentPanel.Children.Add(host);

        var tcs = new TaskCompletionSource<string?>();

        cancelBtn.Click += (_, _) => { tcs.TrySetResult(null); };
        okBtn.Click += (_, _) => tcs.TrySetResult(dialog.Text?.Trim());
        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) tcs.TrySetResult(dialog.Text?.Trim());
            if (e.Key == Key.Escape) tcs.TrySetResult(null);
        };

        _ = dialog.Focus();

        var name = await tcs.Task;
        if (container is Panel p) p.Children.Remove(host);
        if (string.IsNullOrWhiteSpace(name)) return;

        var dir = Path.Combine(_workspaceRoot, relDir);
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, $"{name}.md");
        await File.WriteAllTextAsync(filePath, template);
        await OpenFileAsync(filePath);
        PopulateFileTree();
        _statusBar.Text = $"Created: {name}.md";
    }

    private async void CreateNewCodeFile(string relDir, string extension, string template)
    {
        var dialog = new TextBox
        {
            Watermark = $"Enter file name (without {extension})",
            FontFamily = new("Consolas"),
            FontSize = 13,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            Background = LtaiTheme.Sbb(LtaiTheme.BgInput),
            MinWidth = 300
        };

        var panel = new StackPanel { Spacing = 8, Margin = new(12) };
        panel.Children.Add(new TextBlock
        {
            Text = $"Create new {extension} file in {relDir}/",
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            FontSize = 13
        });
        panel.Children.Add(dialog);

        var btnStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
        var cancelBtn = new Button
        {
            Content = "Cancel",
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary)
        };
        var okBtn = new Button
        {
            Content = "Create",
            Background = LtaiTheme.Sbb(LtaiTheme.AccentSystem),
            Foreground = Brushes.White,
            FontWeight = FontWeight.Bold
        };
        btnStack.Children.Add(cancelBtn);
        btnStack.Children.Add(okBtn);
        panel.Children.Add(btnStack);

        var popup = new Border
        {
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border),
            BorderThickness = new(1),
            CornerRadius = new(6),
            Child = panel,
            MaxWidth = 400
        };

        var overlay = new Panel();
        overlay.Children.Add(popup);
        popup.HorizontalAlignment = HorizontalAlignment.Center;
        popup.VerticalAlignment = VerticalAlignment.Center;

        var host = new Border { Child = overlay, Background = LtaiTheme.Sbb(Color.Parse("#66000000")) };
        var container = this.Parent ?? this;
        if (container is Panel parentPanel)
            parentPanel.Children.Add(host);

        var tcs = new TaskCompletionSource<string?>();

        cancelBtn.Click += (_, _) => { tcs.TrySetResult(null); };
        okBtn.Click += (_, _) => tcs.TrySetResult(dialog.Text?.Trim());
        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) tcs.TrySetResult(dialog.Text?.Trim());
            if (e.Key == Key.Escape) tcs.TrySetResult(null);
        };

        _ = dialog.Focus();

        var name = await tcs.Task;
        if (container is Panel p) p.Children.Remove(host);
        if (string.IsNullOrWhiteSpace(name)) return;

        var dir = Path.Combine(_workspaceRoot, relDir);
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, $"{name}{extension}");
        await File.WriteAllTextAsync(filePath, template);
        await OpenFileAsync(filePath);
        PopulateFileTree();
        _statusBar.Text = $"Created: {name}{extension}";
    }
}
