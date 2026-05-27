using System.Diagnostics;

namespace LTAI.Cli;

public sealed class ProcessLauncher
{
    private static readonly Dictionary<string, Process> Running = new();

    public static bool IsRunning(string name) =>
        Running.TryGetValue(name, out var p) && !p.HasExited;

    public static Process Start(string name, string exePath, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        var proc = Process.Start(psi)!;
        Running[name] = proc;
        return proc;
    }

    public static void Stop(string name)
    {
        if (Running.TryGetValue(name, out var proc))
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(5000);
            }
            Running.Remove(name);
        }
    }

    public static void StopAll()
    {
        foreach (var (name, proc) in Running)
        {
            if (!proc.HasExited)
                proc.Kill(entireProcessTree: true);
        }
        Running.Clear();
    }

    public static List<(string Name, int Pid, string Status)> ListProcesses()
    {
        var result = new List<(string, int, string)>();
        foreach (var (name, proc) in Running)
        {
            var status = proc.HasExited ? "exited" : "running";
            result.Add((name, proc.Id, status));
        }
        return result;
    }
}
