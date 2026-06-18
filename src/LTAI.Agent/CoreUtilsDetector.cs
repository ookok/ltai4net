using System.Diagnostics;

namespace LTAI.Agent;

/// <summary>
/// Detects whether Unix-compatible coreutils (grep, wc, sort, cat, etc.)
/// are available on the current system. On Linux/macOS they're always present.
/// On Windows, checks <c>winget install Microsoft.Coreutils</c>.
/// Uses a 5-minute TTL cache so mid-session installs are picked up.
/// </summary>
public static class CoreUtilsDetector
{
    private static bool? _cached;
    private static DateTime _lastCheck = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static readonly string[] KeyCommands = ["grep", "wc", "sort", "cat", "head", "tail"];

    public static bool IsAvailable
    {
        get
        {
            if (_cached.HasValue && DateTime.UtcNow - _lastCheck < CacheTtl)
                return _cached.Value;
            return Refresh();
        }
    }

    public static bool Refresh()
    {
        _lastCheck = DateTime.UtcNow;
        if (!OperatingSystem.IsWindows())
        {
            _cached = true;
            return true;
        }
        _cached = KeyCommands.Any(cmd => CommandExists(cmd));
        return _cached.Value;
    }

    private static bool CommandExists(string name)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("where", name)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            });
            if (proc == null) return false;
            proc.WaitForExit(2000);
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    public static void PrintReminder()
    {
        if (IsAvailable || !OperatingSystem.IsWindows()) return;
        Console.Error.WriteLine();
        Console.Error.WriteLine("╔════════════════════════════════════════════════════════════╗");
        Console.Error.WriteLine("║ Unix 工具集 (coreutils) 未安装。推荐安装获得完整命令支持。║");
        Console.Error.WriteLine("║   winget install Microsoft.Coreutils                       ║");
        Console.Error.WriteLine("╚════════════════════════════════════════════════════════════╝");
        Console.Error.WriteLine();
    }
}
