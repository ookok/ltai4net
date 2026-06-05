using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace LTAI.TUI;

public static class TextPadView
{
    private static string _currentDir = "";
    private static string? _currentFile;
    private static bool _editMode;
    private static int _scrollOffset;
    private static int _totalLines;
    private static int _pageLines;

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

    private static string? _gitBranch;
    private static readonly Dictionary<string, string> _gitStatusCache = new();

    // P1: Pending chat request — set by TextPadView, consumed by TuiApp
    public static string? PendingChatRequest;
    // P3: Current file being viewed (for Command Palette context)
    public static string? CurrentFileForContext => _currentFile;

    private static void RefreshGitStatus()
    {
        _gitStatusCache.Clear();
        _gitBranch = null;
        try
        {
            var branchPsi = new ProcessStartInfo("git", "rev-parse --abbrev-ref HEAD")
            {
                WorkingDirectory = _currentDir,
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
            };
            using var bp = new Process { StartInfo = branchPsi };
            bp.Start();
            _gitBranch = bp.StandardOutput.ReadToEnd().Trim();
            bp.WaitForExit(2000);
            if (string.IsNullOrEmpty(_gitBranch) || _gitBranch == "HEAD") { _gitBranch = null; return; }

            var psi = new ProcessStartInfo("git", "status --porcelain --untracked-files=normal")
            {
                WorkingDirectory = _currentDir,
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
            };
            using var p = new Process { StartInfo = psi };
            p.Start();
            foreach (var line in p.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries))
                if (line.Length >= 4) _gitStatusCache[line[3..].Trim()] = line[..2].Trim();
            p.WaitForExit(3000);
        }
        catch { _gitBranch = null; }
    }

    private static string GitStatusDot(string? status)
    {
        return status switch
        {
            "M" or "M " or "MM" => "[blue]●[/] ",
            "A" or "A " => "[green]●[/] ",
            "D" or "D " => "[red]●[/] ",
            "R" or "R " => "[yellow]●[/] ",
            "?" or "??" => "[grey]●[/] ",
            "C" or "C " => "[purple]●[/] ",
            "U" or "UU" => "[maroon]●[/] ",
            _ => null
        };
    }

    private static bool DirHasGitChanges(string dirPath)
    {
        var relDir = Path.GetRelativePath(_currentDir, dirPath).Replace('\\', '/');
        return _gitStatusCache.Keys.Any(k => k == relDir || k.StartsWith(relDir + "/"));
    }

    public static void Render(string rootDir)
    {
        _currentDir = rootDir;
        _currentFile = null;
        _editMode = false;
        _editorCaretLine = -1;
        RefreshGitStatus();
        var running = true;
        while (running)
        {
            Console.Clear();
            var branchTag = _gitBranch != null ? $" [blue]🌿 {_gitBranch}[/]" : "";
            AnsiConsole.MarkupLine($"[bold]文件浏览器[/] — [grey]{_currentDir}[/]{branchTag}");
            if (_currentFile != null && File.Exists(_currentFile))
            {
                if (_editorCaretLine < 0) _editorCaretLine = _pageLines / 2 + 1;
                RenderFileView();
                var key = Console.ReadKey(true);
                switch (key.Key)
                {
                    case ConsoleKey.Escape: _currentFile = null; _scrollOffset = 0; break;
                    case ConsoleKey.Tab: _editMode = !_editMode; break;
                    case ConsoleKey.S when _editMode: EditFile(); break;
                    case ConsoleKey.Delete: DeleteFile(); break;
                    case ConsoleKey.F2: RenameFile(); break;
                    case ConsoleKey.UpArrow: if (_scrollOffset > 0) { _scrollOffset--; _editorCaretLine = Math.Max(1, _editorCaretLine - 1); } break;
                    case ConsoleKey.DownArrow: if (_scrollOffset < _totalLines - 1) { _scrollOffset++; _editorCaretLine = Math.Min(_totalLines, _editorCaretLine + 1); } break;
                    case ConsoleKey.PageUp: _scrollOffset = Math.Max(0, _scrollOffset - _pageLines); _editorCaretLine = _scrollOffset + _pageLines / 2 + 1; break;
                    case ConsoleKey.PageDown: _scrollOffset = Math.Min(_totalLines - 1, _scrollOffset + _pageLines); _editorCaretLine = _scrollOffset + _pageLines / 2 + 1; break;
                    case ConsoleKey.Home: _scrollOffset = 0; _editorCaretLine = 1; break;
                    case ConsoleKey.End: _scrollOffset = Math.Max(0, _totalLines - _pageLines); _editorCaretLine = _totalLines; break;
                    case ConsoleKey.G: GoToLine(); break;
                    case ConsoleKey.D when _gitBranch != null: RunCmd($"git diff \"{_currentFile}\""); break;
                    case ConsoleKey.L when _gitBranch != null: RunCmd($"git log --oneline -10"); break;
                    case ConsoleKey.A:
                        PendingChatRequest = $"请分析以下文件：\n文件: {_currentFile}\n```\n{File.ReadAllText(_currentFile)}\n```\n\n请分析代码逻辑、发现潜在问题并给出改进建议。";
                        return; // 退出 TextPadView，TuiApp 会处理 PendingChatRequest
                    case ConsoleKey.F when key.Modifiers == ConsoleModifiers.Control: SearchInFile(); break;
                }
            }
            else
            {
                ShowDirTree();
                var files = GetListing();
                if (files.Count == 0 && !ShowActions()) { running = false; continue; }
                if (files.Count > 0)
                {
                    var choice = AnsiConsole.Prompt(
                        new SelectionPrompt<string>().Title("[yellow]选择:[/]").PageSize(22).AddChoices(files));
                    if (string.IsNullOrEmpty(choice)) { if (!ShowActions()) continue; else continue; }
                    var fp = Path.GetFullPath(Path.Combine(_currentDir, choice));
                    if (Directory.Exists(fp)) { _currentDir = fp; RefreshGitStatus(); continue; }
                    if (File.Exists(fp)) { _currentFile = fp; continue; }
                    // 处理功能项
                    if (choice.Contains("新建文件")) { NewFile(); continue; }
                    if (choice.Contains("新建文件夹")) { NewDir(); continue; }
                }
                if (!ShowActions()) running = false;
            }
        }
    }

    private static bool ShowActions()
    {
        var actions = new List<string>();
        // 项目感知操作
        var hasProject = Directory.GetFiles(_currentDir, "*.csproj").Any() || Directory.GetFiles(_currentDir, "*.sln").Any();
        if (hasProject)
        {
            actions.Add("[yellow]🛠 Build[/]");
            actions.Add("[yellow]🧪 Test[/]");
            actions.Add("[yellow]▶ Run[/]");
        }
        actions.Add("[green]📄 新建文件[/]");
        actions.Add("[green]📁 新建文件夹[/]");
        if (_gitBranch != null)
        {
            actions.Add("[blue]📊 Git 状态[/]");
            actions.Add("[blue]📜 Git 日志[/]");
        }
        actions.Add("[grey]⬆ 上级目录[/]");
        actions.Add("[red]✕ 退出[/]");
        var act = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("[yellow]操作:[/]").PageSize(10).AddChoices(actions));
        if (act.Contains("Build")) { RunCmd("dotnet build"); return true; }
        if (act.Contains("Test")) { RunCmd("dotnet test"); return true; }
        if (act.Contains("Run")) { RunCmd("dotnet run"); return true; }
        if (act.Contains("新建文件")) { NewFile(); return true; }
        if (act.Contains("新建文件夹")) { NewDir(); return true; }
        if (act.Contains("Git 状态")) { RunCmd("git status"); return true; }
        if (act.Contains("Git 日志")) { RunCmd("git log --oneline -10"); return true; }
        if (act.Contains("上级目录"))
        {
            var parent = Directory.GetParent(_currentDir);
            if (parent != null) { _currentDir = parent.FullName; RefreshGitStatus(); }
            return true;
        }
        return false; // exit
    }

    private static List<string> GetListing()
    {
        var items = new List<string>();
        try
        {
            var dirs = Directory.GetDirectories(_currentDir).OrderBy(Path.GetFileName);
            foreach (var d in dirs)
            {
                var dirName = Path.GetFileName(d);
                var dirMark = DirHasGitChanges(d) ? "[blue]●[/] " : "";
                items.Add($"{dirMark}[cyan]📁 {dirName}/[/]");
            }

            var files = Directory.GetFiles(_currentDir)
                .Where(f => TextExts.Contains(Path.GetExtension(f)))
                .OrderBy(Path.GetFileName);
            foreach (var f in files)
            {
                var rel = Path.GetFileName(f);
                var icon = Icon(f);
                var statusMark = "";
                if (_gitStatusCache.TryGetValue(rel, out var st))
                {
                    var dot = GitStatusDot(st);
                    if (dot != null) statusMark = dot;
                }
                items.Add($"{statusMark}{icon} {rel}");
            }
        }
        catch { }
        return items;
    }

    private static void RunCmd(string cmd)
    {
        AnsiConsole.MarkupLine($"[grey]执行: {cmd}[/]");
        try
        {
            var parts = cmd.Split(' ', 2);
            var psi = new ProcessStartInfo(parts[0], parts.Length > 1 ? parts[1] : "")
            {
                WorkingDirectory = _currentDir,
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            using var p = new Process { StartInfo = psi };
            p.Start();
            var output = p.StandardOutput.ReadToEnd();
            var error = p.StandardError.ReadToEnd();
            p.WaitForExit(120_000);
            var isOk = p.ExitCode == 0;
            var isGit = cmd.StartsWith("git ");

            if (isGit)
            {
                var content = new StringBuilder();
                if (!string.IsNullOrEmpty(output))
                {
                    foreach (var line in output.Split('\n'))
                    {
                        if (line.Contains("error:") || line.Contains("fatal:"))
                            content.AppendLine($"[red]{line.EscapeMarkup()}[/]");
                        else if (line.StartsWith("On branch ") || line.StartsWith("HEAD "))
                            content.AppendLine($"[blue]{line.EscapeMarkup()}[/]");
                        else if (line.Contains("nothing to commit") || line.Contains("up-to-date"))
                            content.AppendLine($"[green]{line.EscapeMarkup()}[/]");
                        else if (line.TrimStart().StartsWith("modified:"))
                            content.AppendLine($"[yellow]{line.EscapeMarkup()}[/]");
                        else if (line.TrimStart().StartsWith("new file:"))
                            content.AppendLine($"[green]{line.EscapeMarkup()}[/]");
                        else if (line.TrimStart().StartsWith("deleted:"))
                            content.AppendLine($"[red]{line.EscapeMarkup()}[/]");
                        else if (string.IsNullOrWhiteSpace(line) || line.StartsWith(' '))
                            content.AppendLine($"[grey]{line.EscapeMarkup()}[/]");
                        else
                            content.AppendLine($"[white]{line.EscapeMarkup()}[/]");
                    }
                }
                if (!string.IsNullOrEmpty(error))
                    content.AppendLine($"[red]{error.EscapeMarkup()}[/]");
                AnsiConsole.Write(new Panel(content.ToString().TrimEnd())
                    .Header(isOk ? "[green]✅ git[/]" : "[red]❌ git[/]")
                    .Border(BoxBorder.Rounded)
                    .Expand());
            }
            else
            {
                var text = $"{output}\n{error}".TrimEnd();
                AnsiConsole.Write(new Panel(text.Length > 0 ? text.EscapeMarkup() : "(无输出)")
                    .Header(isOk ? "[green]✅ 成功[/]" : "[red]❌ 失败[/]")
                    .Border(BoxBorder.Rounded)
                    .Expand());
            }
            // P1: 命令失败 → 可以按 A 发送给 AI 修复
            if (!isOk)
            {
                AnsiConsole.MarkupLine("\n[yellow]按 A 键将错误发送给 AI 分析修复，其他键返回...[/]");
                var k = Console.ReadKey(true);
                if (k.Key == ConsoleKey.A)
                {
                    var cmdDisplay = cmd.Length > 80 ? cmd[..77] + "..." : cmd;
                    PendingChatRequest = $"命令执行出错，请分析以下错误并修复：\n```\n{cmdDisplay}\n```\n错误信息：\n```\n{output}\n{error}\n```".Trim();
                    return;
                }
            }
        }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[red]错误: {ex.Message}[/]"); }
        Console.ReadKey(true);
    }

    private static void NewFile()
    {
        var name = AnsiConsole.Ask<string>("[yellow]文件名:[/]");
        if (string.IsNullOrWhiteSpace(name)) return;
        try { File.WriteAllText(Path.Combine(_currentDir, name), ""); } catch (Exception ex) { AnsiConsole.MarkupLine($"[red]错误: {ex.Message}[/]"); }
    }

    private static void NewDir()
    {
        var name = AnsiConsole.Ask<string>("[yellow]文件夹名:[/]");
        if (string.IsNullOrWhiteSpace(name)) return;
        try { Directory.CreateDirectory(Path.Combine(_currentDir, name)); } catch (Exception ex) { AnsiConsole.MarkupLine($"[red]错误: {ex.Message}[/]"); }
    }

    private static void DeleteFile()
    {
        if (_currentFile == null) return;
        if (!AnsiConsole.Confirm($"[red]确认删除 {Path.GetFileName(_currentFile)}?[/]", false)) return;
        try { File.Delete(_currentFile); _currentFile = null; } catch (Exception ex) { AnsiConsole.MarkupLine($"[red]错误: {ex.Message}[/]"); }
    }

    private static void RenameFile()
    {
        if (_currentFile == null) return;
        var newName = AnsiConsole.Ask<string>($"[yellow]新文件名 ({Path.GetFileName(_currentFile)}):[/]");
        if (string.IsNullOrWhiteSpace(newName)) return;
        try { File.Move(_currentFile, Path.Combine(Path.GetDirectoryName(_currentFile)!, newName)); _currentFile = null; }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[red]错误: {ex.Message}[/]"); }
    }

    private static void GoToLine()
    {
        if (_currentFile == null) return;
        var line = AnsiConsole.Ask<int>($"[yellow]跳转到行 (1-{_totalLines}):[/]");
        _scrollOffset = Math.Clamp(line - 5, 0, _totalLines - 1);
        _editorCaretLine = line;
    }

    private static void SearchInFile()
    {
        if (_currentFile == null) return;
        var keyword = AnsiConsole.Ask<string>("[yellow]搜索:[/]");
        if (string.IsNullOrWhiteSpace(keyword)) return;
        try
        {
            var lines = File.ReadAllLines(_currentFile);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    _scrollOffset = Math.Max(0, i - 2);
                    _editorCaretLine = i + 1;
                    AnsiConsole.MarkupLine($"[green]找到:[/] 第 {i + 1} 行 — {lines[i].Trim().EscapeMarkup()}");
                }
            }
            Console.ReadKey(true);
        }
        catch { }
    }

    private static void ShowDirTree()
    {
        try
        {
            var branchTag = _gitBranch != null ? $" [blue]🌿 {_gitBranch}[/]" : "";
            var tree = new Tree($"[bold cyan]{Path.GetFileName(_currentDir) ?? _currentDir}/{branchTag}[/]");
            foreach (var d in Directory.GetDirectories(_currentDir).OrderBy(Path.GetFileName).Take(15))
            {
                var dirName = Path.GetFileName(d);
                var dirMark = DirHasGitChanges(d) ? "[blue]● [/]" : "";
                var n = tree.AddNode($"{dirMark}[cyan]{dirName}/[/]");
                foreach (var f in Directory.GetFiles(d).Where(f => TextExts.Contains(Path.GetExtension(f))).OrderBy(Path.GetFileName).Take(10))
                {
                    var rel = Path.GetFileName(f);
                    var dot = "";
                    if (_gitStatusCache.TryGetValue(rel, out var st)) { var d2 = GitStatusDot(st); if (d2 != null) dot = d2; }
                    n.AddNode($"{dot}{Icon(f)} {rel}");
                }
                foreach (var sub in Directory.GetDirectories(d).OrderBy(Path.GetFileName).Take(5))
                {
                    var subMark = DirHasGitChanges(sub) ? "[blue]● [/]" : "";
                    n.AddNode($"{subMark}[cyan]{Path.GetFileName(sub)}/…[/]");
                }
            }
            foreach (var f in Directory.GetFiles(_currentDir).Where(f => TextExts.Contains(Path.GetExtension(f))).OrderBy(Path.GetFileName).Take(20))
            {
                var rel = Path.GetFileName(f);
                var dot = "";
                if (_gitStatusCache.TryGetValue(rel, out var st)) { var d2 = GitStatusDot(st); if (d2 != null) dot = d2; }
                tree.AddNode($"{dot}{Icon(f)} {rel}");
            }
            AnsiConsole.Write(tree);
            AnsiConsole.MarkupLine("[dim]↵ 选择  Del 删除  F2 重命名  Esc 上级  Q 退出[/]");
        }
        catch { AnsiConsole.MarkupLine("[red]无法读取目录[/]"); }
    }

    private static void RenderFileView()
    {
        try
        {
            var ext = Path.GetExtension(_currentFile!);
            var isCode = CodeExts.Contains(ext);
            var fi = new FileInfo(_currentFile!);
            var sb = new StringBuilder();
            var termHeight = Math.Max(Console.WindowHeight - 6, 10);
            _pageLines = termHeight - 1;

            var lines = new List<string>();
            using var reader = new StreamReader(_currentFile!);
            int skipped = 0;
            while (skipped < _scrollOffset) { if (reader.ReadLine() == null) break; skipped++; }
            for (int i = 0; i < termHeight; i++)
            {
                var line = reader.ReadLine();
                if (line == null) break;
                var display = line.EscapeMarkup();
                if (isCode) display = Highlight(display, ext);
                lines.Add(display);
            }

            _totalLines = _scrollOffset + lines.Count;
            if (!reader.EndOfStream) _totalLines = EstimateLines(_currentFile!);

            var pad = _totalLines.ToString().Length;
            for (int i = 0; i < lines.Count; i++)
            {
                var lineNum = _scrollOffset + i + 1;
                var marker = _editorCaretLine >= 0 && lineNum == _editorCaretLine ? "[yellow]▎[/]" : " ";
                sb.AppendLine($"[grey]{lineNum.ToString().PadLeft(pad)}[/]{marker}{lines[i]}");
            }

            // 编码 + 文件信息
            var encoding = DetectEncoding(_currentFile!);
            var mode = _editMode ? "[yellow]编辑中[/]" : "[green]只读[/]";
            var pct = _totalLines > 0 ? _scrollOffset * 100 / Math.Max(_totalLines - _pageLines, 1) : 0;
            if (_totalLines > _scrollOffset + lines.Count)
                sb.AppendLine($"[grey]... 已滚动至第 {_scrollOffset + 1} 行，共 ~{_totalLines} 行 ({fi.Length / 1024}KB)  [{pct}%]  {encoding}[/]");
            else
                sb.AppendLine($"[green]文件末 — {lines.Count} 行 ({fi.Length / 1024}KB)  {encoding}[/]");

            sb.AppendLine($"[dim]↑↓滚动 PgUp/PgDn翻页 Home/End首尾 Tab编辑 G跳转 D-diff L-log Ctrl+F搜索 Del删除 F2重命名 Esc返回[/]");
            AnsiConsole.Write(new Panel(sb.ToString().TrimEnd())
                .Header($"[bold]{Path.GetFileName(_currentFile)}[/] — {mode}")
                .BorderColor(_editMode ? Color.Yellow : Color.Green).Expand());
        }
        catch { AnsiConsole.MarkupLine("[red]无法读取文件[/]"); }
    }

    private static int _editorCaretLine;
    private static string DetectEncoding(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1);
            var bom = new byte[4];
            var read = fs.Read(bom, 0, 4);
            if (read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF) return "UTF-8 BOM";
            if (read >= 2 && bom[0] == 0xFF && bom[1] == 0xFE) return "UTF-16 LE";
            if (read >= 2 && bom[0] == 0xFE && bom[1] == 0xFF) return "UTF-16 BE";
        }
        catch { }
        return "UTF-8";
    }

    private static int EstimateLines(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (fi.Length == 0) return 0;
            using var reader = new StreamReader(path);
            var sample = new char[10240];
            var read = reader.Read(sample, 0, sample.Length);
            if (read == 0) return 0;
            var sampleLines = new string(sample, 0, read).Split('\n').Length;
            return sampleLines <= 1 ? (int)(fi.Length / 80) : (int)(fi.Length / ((float)read / sampleLines));
        }
        catch { return 0; }
    }

    private static void EditFile()
    {
        var lines = new List<string>();
        if (_currentFile != null && File.Exists(_currentFile))
            lines.AddRange(File.ReadAllLines(_currentFile));
        AnsiConsole.MarkupLine($"[yellow]当前 {lines.Count} 行，输入新内容 (空行=保存, /cancel=取消):[/]");
        if (lines.Count > 0)
        {
            foreach (var l in lines.Take(10))
                AnsiConsole.MarkupLine($"  [grey]{l.EscapeMarkup()}[/]");
            if (lines.Count > 10)
                AnsiConsole.MarkupLine($"  [grey]... {lines.Count - 10} 行隐藏[/]");
        }
        var newLines = new List<string>();
        while (true)
        {
            var line = Console.ReadLine() ?? "";
            if (line == "/cancel") return;
            if (line == "") break;
            newLines.Add(line);
        }
        if (newLines.Count == 0) { AnsiConsole.MarkupLine("[grey]未作修改[/]"); return; }
        try { File.WriteAllLines(_currentFile!, newLines); AnsiConsole.MarkupLine("[green]已保存[/]"); }
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

    private static readonly Dictionary<string, Regex> _highlightCache = new();

    private static string Highlight(string line, string ext)
    {
        if (!_highlightCache.TryGetValue(ext, out var regex))
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
            var pattern = kws.Length > 0
                ? $@"\b({string.Join("|", kws.OrderByDescending(k => k.Length).Select(Regex.Escape))})\b"
                : "";
            regex = !string.IsNullOrEmpty(pattern) ? new Regex(pattern, RegexOptions.Compiled) : null!;
            _highlightCache[ext] = regex!;
        }
        if (regex == null) return line;
        return regex.Replace(line, m => $"[bold yellow]{m.Groups[1].Value}[/]");
    }
}
