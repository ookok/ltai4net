using System.Text;
using Spectre.Console;

namespace LTAI.TUI;

public sealed class TuiInputBox
{
    private readonly StringBuilder _buffer = new();
    private int _cursorPos;
    private readonly List<string> _history = new();
    private int _historyIdx = -1;
    private readonly string _projectRoot;
    private string _pastedContent = "";

    public TuiInputBox(string projectRoot)
    {
        _projectRoot = projectRoot;
    }

    public async Task<string> ReadInputAsync(string prompt = "Query")
    {
        _buffer.Clear();
        _cursorPos = 0;
        _pastedContent = "";

        AnsiConsole.Markup($"[green]{prompt}:[/] ");
        AnsiConsole.Markup("[grey](Ctrl+V paste path, @file, ↑↓ history, Esc cancel)[/]");
        AnsiConsole.WriteLine();

        while (true)
        {
            var key = Console.ReadKey(true);

            if (key.Key == ConsoleKey.Enter)
            {
                var result = GetFinalContent();
                AnsiConsole.WriteLine();
                if (!string.IsNullOrWhiteSpace(result))
                {
                    _history.Add(result);
                    _historyIdx = _history.Count;
                }
                return result;
            }

            if (key.Key == ConsoleKey.Escape)
            {
                AnsiConsole.WriteLine();
                return "";
            }

            if (key.Key == ConsoleKey.UpArrow && _history.Count > 0)
            {
                NavigateHistory(-1);
                RedrawInput(prompt);
                continue;
            }

            if (key.Key == ConsoleKey.DownArrow && _history.Count > 0)
            {
                NavigateHistory(1);
                RedrawInput(prompt);
                continue;
            }

            if (key.Key == ConsoleKey.LeftArrow) { _cursorPos = Math.Max(0, _cursorPos - 1); continue; }
            if (key.Key == ConsoleKey.RightArrow) { _cursorPos = Math.Min(_buffer.Length, _cursorPos + 1); continue; }
            if (key.Key == ConsoleKey.Backspace && _cursorPos > 0) { _buffer.Remove(_cursorPos - 1, 1); _cursorPos--; RedrawInput(prompt); continue; }
            if (key.Key == ConsoleKey.Delete && _cursorPos < _buffer.Length) { _buffer.Remove(_cursorPos, 1); RedrawInput(prompt); continue; }
            if (key.Key == ConsoleKey.Home) { _cursorPos = 0; continue; }
            if (key.Key == ConsoleKey.End) { _cursorPos = _buffer.Length; continue; }

            if (key.Modifiers == ConsoleModifiers.Control && key.Key == ConsoleKey.V)
            {
                var pasted = await ReadClipboardAsync();
                if (!string.IsNullOrEmpty(pasted))
                {
                    var resolved = await ResolvePastedContentAsync(pasted);
                    _buffer.Insert(_cursorPos, resolved);
                    _cursorPos += resolved.Length;
                    RedrawInput(prompt);
                }
                continue;
            }

            if (key.Modifiers == ConsoleModifiers.Control && key.Key == ConsoleKey.K)
            {
                _buffer.Length = _cursorPos;
                RedrawInput(prompt);
                continue;
            }

            if (key.KeyChar >= 32)
            {
                _buffer.Insert(_cursorPos, key.KeyChar);
                _cursorPos++;
                RedrawInput(prompt);
            }
        }
    }

    private string GetFinalContent()
    {
        var text = _buffer.ToString();

        if (text.StartsWith("@"))
        {
            var path = text[1..].Trim();
            text = LoadFileContent(path);
        }

        if (text.StartsWith("@@"))
        {
            var path = text[2..].Trim();
            text = LoadFolderContent(path);
        }

        return text;
    }

    private string LoadFileContent(string path)
    {
        try
        {
            if (!File.Exists(path)) path = Path.Combine(_projectRoot, path);
            if (!File.Exists(path)) return _buffer.ToString();

            var content = File.ReadAllText(path);
            var ext = Path.GetExtension(path);
            return $"[File: {Path.GetFileName(path)}]\n```{ext.TrimStart('.')}\n{content[..Math.Min(content.Length, 5000)]}\n```\n\nAnalyze this file.";
        }
        catch
        {
            return _buffer.ToString();
        }
    }

    private string LoadFolderContent(string path)
    {
        try
        {
            if (!Directory.Exists(path)) path = Path.Combine(_projectRoot, path);
            if (!Directory.Exists(path)) return _buffer.ToString();

            var sb = new StringBuilder();
            sb.AppendLine($"[Folder: {Path.GetFileName(path)}]");

            foreach (var file in Directory.GetFiles(path, "*.*", SearchOption.AllDirectories).Take(50))
            {
                var rel = Path.GetRelativePath(path, file);
                var size = new FileInfo(file).Length;
                sb.AppendLine($"  {rel} ({FormatSize(size)})");
            }

            sb.AppendLine("\nAnalyze this project structure.");
            return sb.ToString();
        }
        catch
        {
            return _buffer.ToString();
        }
    }

    private async Task<string> ReadClipboardAsync()
    {
        return await Task.FromResult("");
    }

    private async Task<string> ResolvePastedContentAsync(string pasted)
    {
        pasted = pasted.Trim();

        if (pasted.StartsWith("@") && pasted.Length > 1)
            return pasted;

        if (File.Exists(pasted))
        {
            var content = File.ReadAllText(pasted);
            var ext = Path.GetExtension(pasted);
            return $"[File: {Path.GetFileName(pasted)}]\n```{ext.TrimStart('.')}\n{content[..Math.Min(content.Length, 3000)]}\n```\n\n";
        }

        if (Directory.Exists(pasted))
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[Folder: {Path.GetFileName(pasted)}]");
            foreach (var f in Directory.GetFiles(pasted, "*.*", SearchOption.AllDirectories).Take(30))
                sb.AppendLine($"  {Path.GetRelativePath(pasted, f)}");
            return sb.ToString();
        }

        return pasted;
    }

    private void NavigateHistory(int direction)
    {
        if (_history.Count == 0) return;
        _historyIdx = Math.Clamp(_historyIdx + direction, 0, _history.Count - 1);
        _buffer.Clear();
        _buffer.Append(_history[_historyIdx]);
        _cursorPos = _buffer.Length;
    }

    private void RedrawInput(string prompt)
    {
        Console.SetCursorPosition(0, Console.CursorTop);
        Console.Write(new string(' ', Console.WindowWidth));
        Console.SetCursorPosition(0, Console.CursorTop);
        AnsiConsole.Markup($"[green]{prompt}:[/] [white]{EscapeMarkup(_buffer.ToString())}[/]");
    }

    private static string EscapeMarkup(string text) =>
        text.Replace("[", "[[").Replace("]", "]]");

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes}B",
        < 1024 * 1024 => $"{bytes / 1024}KB",
        _ => $"{bytes / (1024 * 1024)}MB"
    };
}
