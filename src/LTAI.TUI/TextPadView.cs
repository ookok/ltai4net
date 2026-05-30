using System.Text;
using Spectre.Console;

namespace LTAI.TUI;

public static class TextPadView
{
    private static string _currentDir = "";
    private static string? _currentFile;
    private static bool _editMode;

    private static readonly HashSet<string> TextExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".py", ".js", ".ts", ".go", ".rs", ".java", ".sh", ".bash",
        ".md", ".txt", ".json", ".xml", ".yaml", ".yml", ".toml", ".ini",
        ".cfg", ".conf", ".css", ".html", ".htm", ".jsx", ".tsx", ".sln",
        ".csproj", ".props", ".targets", ".gitignore", ".env", ".editorconfig",
    };
    private static readonly HashSet<string> CodeExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".py", ".js", ".ts", ".go", ".rs", ".java",
        ".jsx", ".tsx", ".css", ".html", ".sh", ".bash",
    };

    public static void Render(string rootDir)
    {
        _currentDir = rootDir;
        var running = true;
        while (running)
        {
            Console.Clear();
            AnsiConsole.MarkupLine($"[bold]文件浏览器[/] — [grey]{_currentDir}[/]");
            if (_currentFile != null && File.Exists(_currentFile))
            {
                RenderFileView();
                switch (Console.ReadKey(true).Key)
                {
                    case ConsoleKey.Escape: _currentFile = null; break;
                    case ConsoleKey.Tab: _editMode = !_editMode; break;
                    case ConsoleKey.S when _editMode: EditFile(); break;
                }
            }
            else
            {
                ShowDirTree();
                var files = GetListing();
                if (files.Count == 0) { running = false; continue; }
                var choice = AnsiConsole.Prompt(
                    new SelectionPrompt<string>().Title("[yellow]选择文件:[/]").PageSize(20).AddChoices(files));
                if (string.IsNullOrEmpty(choice)) continue;
                var fp = Path.GetFullPath(Path.Combine(_currentDir, choice));
                if (Directory.Exists(fp)) _currentDir = fp;
                else if (File.Exists(fp)) _currentFile = fp;
            }
        }
    }

    private static List<string> GetListing()
    {
        var items = new List<string>();
        try
        {
            items.AddRange(Directory.GetDirectories(_currentDir)
                .Select(d => $"[cyan]📁 {Path.GetFileName(d)}/[/]"));
            items.AddRange(Directory.GetFiles(_currentDir)
                .Where(f => TextExts.Contains(Path.GetExtension(f)))
                .Select(f => $"{Icon(f)} {Path.GetFileName(f)}"));
        }
        catch { }
        return items;
    }

    private static void ShowDirTree()
    {
        try
        {
            var tree = new Tree($"[bold cyan]{(Path.GetFileName(_currentDir) ?? _currentDir)}/[/]");
            foreach (var d in Directory.GetDirectories(_currentDir).OrderBy(Path.GetFileName).Take(15))
            {
                var n = tree.AddNode($"[cyan]{Path.GetFileName(d)}/[/]");
                foreach (var f in Directory.GetFiles(d).Where(f => TextExts.Contains(Path.GetExtension(f))).OrderBy(Path.GetFileName).Take(10))
                    n.AddNode($"{Icon(f)} {Path.GetFileName(f)}");
                foreach (var sub in Directory.GetDirectories(d).OrderBy(Path.GetFileName).Take(5))
                    n.AddNode($"[cyan]{Path.GetFileName(sub)}/…[/]");
            }
            foreach (var f in Directory.GetFiles(_currentDir).Where(f => TextExts.Contains(Path.GetExtension(f))).OrderBy(Path.GetFileName).Take(20))
                tree.AddNode($"{Icon(f)} {Path.GetFileName(f)}");
            AnsiConsole.Write(tree);
        }
        catch { AnsiConsole.MarkupLine("[red]无法读取目录[/]"); }
    }

    private static void RenderFileView()
    {
        try
        {
            var ext = Path.GetExtension(_currentFile!);
            var isCode = CodeExts.Contains(ext);
            var lines = File.ReadAllLines(_currentFile!);
            var sb = new StringBuilder();
            var pad = lines.Length.ToString().Length;
            for (int i = 0; i < Math.Min(lines.Length, 500); i++)
            {
                var line = lines[i].EscapeMarkup();
                if (isCode) line = Highlight(line, ext);
                sb.AppendLine($"[grey]{(i + 1).ToString().PadLeft(pad)}[/] {line}");
            }
            if (lines.Length > 500)
                sb.AppendLine($"[grey]... 仅显示前 500 行，共 {lines.Length} 行[/]");
            var mode = _editMode ? "[yellow]编辑中[/]" : "[green]只读[/]";
            AnsiConsole.Write(new Panel(sb.ToString().TrimEnd())
                .Header($"[bold]{Path.GetFileName(_currentFile)}[/] — {mode}")
                .BorderColor(_editMode ? Color.Yellow : Color.Green).Expand());
        }
        catch { AnsiConsole.MarkupLine("[red]无法读取文件[/]"); }
    }

    private static void EditFile()
    {
        AnsiConsole.MarkupLine("[yellow]输入新内容 (空行=保存, /cancel=取消):[/]");
        var lines = new List<string>();
        while (true)
        {
            var line = Console.ReadLine() ?? "";
            if (line == "/cancel") return;
            if (line == "") break;
            lines.Add(line);
        }
        try { File.WriteAllLines(_currentFile!, lines); AnsiConsole.MarkupLine("[green]已保存[/]"); }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[red]保存失败: {ex.Message}[/]"); }
        Console.ReadKey(true);
    }

    private static string Icon(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".cs" => "[yellow]📄[/]", ".md" => "[blue]📝[/]", ".json" => "[green]📋[/]",
        ".py" => "[green]🐍[/]", ".js" or ".ts" => "[yellow]🟨[/]", ".html" => "[blue]🌐[/]",
        ".xml" => "[teal]📰[/]", ".yml" or ".yaml" => "[silver]⚙️[/]",
        ".csproj" or ".sln" => "[magenta]🏗️[/]", ".gitignore" => "[grey]🔒[/]", _ => "[grey]📄[/]"
    };

    private static string Highlight(string line, string ext)
    {
        var kws = ext switch
        {
            ".cs" => new[] { "using", "namespace", "class", "struct", "interface", "enum", "record",
                "public", "private", "protected", "internal", "static", "readonly", "virtual",
                "abstract", "sealed", "async", "await", "return", "new", "this", "base",
                "if", "else", "for", "foreach", "while", "do", "switch", "case", "break",
                "continue", "try", "catch", "finally", "throw", "var", "void", "int", "string",
                "bool", "long", "double", "float", "char", "byte", "object", "null", "true", "false",
                "Task", "ValueTask", "get", "set", "value", "init", "required", "params", "is", "as",
                "typeof", "nameof", "lock" },
            ".py" => new[] { "def", "class", "import", "from", "as", "return", "if", "elif", "else",
                "for", "while", "try", "except", "finally", "with", "async", "await", "pass",
                "None", "True", "False", "self", "in", "not", "and", "or", "lambda", "yield" },
            _ => Array.Empty<string>()
        };
        foreach (var kw in kws)
        {
            var i = line.IndexOf(kw, StringComparison.Ordinal);
            if (i >= 0 && (i == 0 || !char.IsLetterOrDigit(line[i - 1]))
                && (i + kw.Length >= line.Length || !char.IsLetterOrDigit(line[i + kw.Length])))
                return $"{line[..i]}[bold yellow]{kw}[/]{line[(i + kw.Length)..]}";
        }
        return line;
    }
}
