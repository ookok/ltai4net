using System.Runtime.InteropServices;
using Spectre.Console;

namespace LTAI.TUI;

public sealed class NotificationService
{
    private bool _enabled = true;

    public bool Enabled { get => _enabled; set => _enabled = value; }

    public void Notify(string title, string body)
    {
        if (!_enabled) return;

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                NotifyWindows(title, body);
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                NotifyMacOS(title, body);
            else
                NotifyLinux(title, body);
        }
        catch
        {
            // Notifications are best-effort
        }
    }

    public async Task NotifyWithDelayAsync(string title, string body, int delayMs = 0)
    {
        if (delayMs > 0) await Task.Delay(delayMs);
        Notify(title, body);
    }

    private static void NotifyWindows(string title, string body)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("powershell", $"-Command \"[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null; $template = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02); $template.GetElementsByTagName('text')[0].AppendChild($template.CreateTextNode('{title}')) | Out-Null; $template.GetElementsByTagName('text')[1].AppendChild($template.CreateTextNode('{body}')) | Out-Null; $toast = [Windows.UI.Notifications.ToastNotification]::new($template); [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('LTAI').Show($toast);\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        };
        try { using var p = System.Diagnostics.Process.Start(psi); p?.WaitForExit(2000); } catch { /* non-fatal */ }
    }

    private static void NotifyMacOS(string title, string body)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("osascript", $"-e 'display notification \"{body}\" with title \"{title}\"'")
        {
            CreateNoWindow = true, UseShellExecute = false
        };
        try { using var p = System.Diagnostics.Process.Start(psi); p?.WaitForExit(2000); } catch { /* non-fatal */ }
    }

    private static void NotifyLinux(string title, string body)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("notify-send", $"\"{title}\" \"{body}\"")
        {
            CreateNoWindow = true, UseShellExecute = false
        };
        try { using var p = System.Diagnostics.Process.Start(psi); p?.WaitForExit(2000); } catch { /* non-fatal */ }
    }
}

public sealed class SessionSearch
{
    private readonly List<(string role, string text)> _history;
    private int _currentMatchIdx = -1;
    private List<(int turnIdx, int pos, string line)> _matches = new();

    public SessionSearch(List<(string role, string text)> history)
    {
        _history = history;
    }

    public bool Search()
    {
        var query = AnsiConsole.Ask<string>("[cyan]Search:[/] ", "");
        if (string.IsNullOrWhiteSpace(query)) return false;

        _matches.Clear();
        _currentMatchIdx = 0;

        for (var i = 0; i < _history.Count; i++)
        {
            var (role, text) = _history[i];
            var lines = text.Split('\n');
            for (var j = 0; j < lines.Length; j++)
            {
                if (lines[j].Contains(query, StringComparison.OrdinalIgnoreCase))
                    _matches.Add((i, j, lines[j]));
            }
        }

        if (_matches.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No matches found.[/]");
            return false;
        }

        AnsiConsole.MarkupLine($"[green]{_matches.Count} matches[/]");
        ShowMatch(0);
        return true;
    }

    public void NextMatch()
    {
        if (_matches.Count == 0) return;
        _currentMatchIdx = (_currentMatchIdx + 1) % _matches.Count;
        ShowMatch(_currentMatchIdx);
    }

    public void PrevMatch()
    {
        if (_matches.Count == 0) return;
        _currentMatchIdx = (_currentMatchIdx - 1 + _matches.Count) % _matches.Count;
        ShowMatch(_currentMatchIdx);
    }

    private void ShowMatch(int idx)
    {
        if (idx < 0 || idx >= _matches.Count) return;
        var (turnIdx, lineIdx, line) = _matches[idx];
        var (role, fullText) = _history[turnIdx];

        AnsiConsole.MarkupLine($"[grey]Match {idx + 1}/{_matches.Count}[/] [green]{role}[/] turn #{turnIdx + 1}, line {lineIdx + 1}:");
        AnsiConsole.MarkupLine($"[yellow]{HighlightMatch(line)}[/]");

        if (lineIdx > 0)
        {
            var prevLine = fullText.Split('\n')[lineIdx - 1];
            AnsiConsole.MarkupLine($"[grey]  ...{prevLine[..Math.Min(prevLine.Length, 80)]}[/]");
        }
        if (lineIdx < fullText.Split('\n').Length - 1)
        {
            var nextLine = fullText.Split('\n')[lineIdx + 1];
            AnsiConsole.MarkupLine($"[grey]  ...{nextLine[..Math.Min(nextLine.Length, 80)]}[/]");
        }
    }

    private static string HighlightMatch(string line)
    {
        return line.Replace("[", "[[").Replace("]", "]]");
    }

    public int MatchCount => _matches.Count;
}
