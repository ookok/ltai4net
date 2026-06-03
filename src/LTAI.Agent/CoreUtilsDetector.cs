using System.Diagnostics;

namespace LTAI.Agent;

/// <summary>
/// Detects whether Unix-compatible coreutils (grep, wc, sort, cat, etc.)
/// are available on the current system. On Linux/macOS they're always present.
/// On Windows, checks <c>winget install Microsoft.Coreutils</c>.
/// </summary>
public static class CoreUtilsDetector
{
    private static bool? _available;
    private static readonly string[] KeyCommands = ["grep", "wc", "sort", "cat", "head", "tail"];

    public static bool IsAvailable
    {
        get
        {
            if (_available.HasValue) return _available.Value;
            if (!OperatingSystem.IsWindows())
            {
                _available = true;
                return true;
            }
            _available = KeyCommands.Any(cmd => CommandExists(cmd));
            return _available.Value;
        }
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
