using System.Runtime.InteropServices;
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
    private readonly List<StringBuilder> _multiLineBuffer = new();

    public TuiInputBox(string projectRoot)
    {
        _projectRoot = projectRoot;
    }

    public async Task<string> ReadInputAsync(string prompt = "Query")
    {
        Console.WriteLine();
        Console.Write($"{prompt}: ");
        var line = Console.ReadLine() ?? "";
        if (!string.IsNullOrWhiteSpace(line))
        {
            _history.Add(line);
            _historyIdx = _history.Count;
        }
        Console.WriteLine();
        return line;
    }

    private string GetFinalContent()
    {
        if (_multiLineBuffer.Count > 0)
        {
            _multiLineBuffer.Add(new StringBuilder(_buffer.ToString()));
            var sb = new StringBuilder();
            foreach (var line in _multiLineBuffer)
                sb.AppendLine(line.ToString());
            _multiLineBuffer.Clear();
            return ResolveContent(sb.ToString().TrimEnd());
        }

        return ResolveContent(_buffer.ToString());
    }

    private string ResolveContent(string text)
    {
        if (text.StartsWith("@") && !text.StartsWith("@@"))
        {
            var path = text[1..].Trim();
            return LoadFileContent(path);
        }

        if (text.StartsWith("@@"))
        {
            var path = text[2..].Trim();
            return LoadFolderContent(path);
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

    private static async Task<string> ReadClipboardAsync()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var psi = new System.Diagnostics.ProcessStartInfo("powershell", "-Command \"Get-Clipboard\"")
                {
                    RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
                };
                using var p = System.Diagnostics.Process.Start(psi);
                if (p != null)
                {
                    var result = await p.StandardOutput.ReadToEndAsync();
                    await p.WaitForExitAsync();
                    return result.TrimEnd();
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var psi = new System.Diagnostics.ProcessStartInfo("pbpaste")
                {
                    RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
                };
                using var p = System.Diagnostics.Process.Start(psi);
                if (p != null)
                {
                    var result = await p.StandardOutput.ReadToEndAsync();
                    await p.WaitForExitAsync();
                    return result;
                }
            }
            else
            {
                var psi = new System.Diagnostics.ProcessStartInfo("xclip", "-o -selection clipboard")
                {
                    RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
                };
                using var p = System.Diagnostics.Process.Start(psi);
                if (p != null)
                {
                    var result = await p.StandardOutput.ReadToEndAsync();
                    await p.WaitForExitAsync();
                    return result;
                }
            }
        }
        catch { }

        return "";
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
        var top = Console.CursorTop;
        Console.SetCursorPosition(0, top > 0 ? top - 1 : 0);
        Console.Write(new string(' ', Math.Min(Console.WindowWidth, 120)));
        Console.SetCursorPosition(0, top > 0 ? top - 1 : 0);
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
