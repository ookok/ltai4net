using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace LTAI.Agent.Tools;

/// <summary>
/// Background job management tools.
/// Ported from DeepSeek-Reasonix jobs.ts + shell.ts pattern.
/// </summary>
public sealed class JobTools
{
    // In-memory registry of running jobs
    private static readonly Dictionary<int, JobEntry> _jobs = new();
    private static int _nextId = 1;
    private static readonly object _lock = new();

    private sealed record JobEntry(int Id, string Command, Process Process, DateTime StartedAt, StringBuilder Output);

    [Description("Start a command in the background and return job ID")]
    public async Task<string> StartJob(
        [Description("Command to execute")] string command,
        [Description("Working directory (default: current)")] string? cwd = null,
        [Description("Wait seconds for startup signal")] int waitSec = 3)
    {
        try
        {
            // Parse command into executable + argument array to avoid shell injection
            var parts = ParseCommand(command);
            if (parts.Length == 0)
                return "Error: Empty command";

            var psi = new ProcessStartInfo
            {
                FileName = parts[0],
                WorkingDirectory = cwd ?? Directory.GetCurrentDirectory(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            // Add arguments individually — no shell interpretation
            for (int i = 1; i < parts.Length; i++)
                psi.ArgumentList.Add(parts[i]);

            var proc = new Process { StartInfo = psi };
            var output = new StringBuilder();

            proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine("[ERR] " + e.Data); };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            int id;
            lock (_lock) { id = _nextId++; _jobs[id] = new JobEntry(id, command, proc, DateTime.UtcNow, output); }

            // Wait for startup signal or timeout
            if (waitSec > 0)
            {
                var signaled = proc.WaitForExit(waitSec * 1000);
                if (!signaled)
                    return $"Job #{id} started (PID {proc.Id}), running in background";
            }

            return $"Job #{id} started (PID {proc.Id})";
        }
        catch (Exception ex)
        {
            return $"Failed to start job: {ex.Message}";
        }
    }

    /// <summary>Parse a command string into [executable, arg1, arg2, ...].
    /// Rejects shell metacharacters outside quotes — no shell interpretation.</summary>
    private static string[] ParseCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return [];

        var tokens = new List<string>();
        var current = new StringBuilder();
        bool inQuote = false;
        char quoteChar = '\0';

        for (int i = 0; i < command.Length; i++)
        {
            var c = command[i];

            if (inQuote)
            {
                if (c == quoteChar)
                {
                    inQuote = false;
                    continue;
                }
                current.Append(c);
                continue;
            }

            if (c == '\'' || c == '"')
            {
                inQuote = true;
                quoteChar = c;
                continue;
            }

            // Shell metacharacters are rejected — no pipe/redirect/chaining
            if (c is '$' or '`' or '|' or '>' or '<' or '&' or ';' or '(' or ')' or '#')
                throw new InvalidOperationException(
                    $"Shell metacharacter '{c}' is not allowed. Use explicit commands (e.g. write a script file for pipelines).");

            if (c == ' ' || c == '\t')
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
            tokens.Add(current.ToString());

        return tokens.ToArray();
    }

    [Description("List all background jobs")]
    public static string ListJobs()
    {
        lock (_lock)
        {
            if (_jobs.Count == 0) return "No background jobs.";

            var sb = new StringBuilder();
            sb.AppendLine("## Background Jobs\n");
            sb.AppendLine("| ID | Command | Status | PID | Runtime |");
            sb.AppendLine("|----|---------|--------|-----|---------|");

            foreach (var (id, job) in _jobs.OrderBy(j => j.Key))
            {
                var alive = !job.Process.HasExited;
                var runtime = DateTime.UtcNow - job.StartedAt;
                var cmd = job.Command.Length > 40 ? job.Command[..37] + "..." : job.Command;
                sb.AppendLine($"| {id} | {cmd} | {(alive ? "🟢 Running" : "🔴 Exited")} | {job.Process.Id} | {runtime:mm\\:ss} |");
            }
            return sb.ToString();
        }
    }

    [Description("Get output of a background job")]
    public static string GetJobOutput(
        [Description("Job ID")] int jobId,
        [Description("Tail lines (0 = all)")] int tailLines = 50)
    {
        lock (_lock)
        {
            if (!_jobs.TryGetValue(jobId, out var job))
                return $"Job #{jobId} not found";

            string text;
            lock (job.Output) text = job.Output.ToString();

            if (tailLines > 0)
            {
                var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                text = string.Join("\n", lines.TakeLast(tailLines));
            }

            var alive = !job.Process.HasExited;
            return $"Job #{jobId} — {(alive ? "🟢 Running" : "🔴 Exited (code: " + job.Process.ExitCode + ")")}\n\n{text}";
        }
    }

    [Description("Wait for a background job to finish")]
    public async Task<string> WaitForJob(
        [Description("Job ID")] int jobId,
        [Description("Timeout in seconds")] int timeoutSec = 30)
    {
        lock (_lock)
        {
            if (!_jobs.TryGetValue(jobId, out var job))
                return $"Job #{jobId} not found";

            if (job.Process.HasExited)
                return $"Job #{jobId} already exited (code: {job.Process.ExitCode})";
        }

        Process proc;
        lock (_lock) proc = _jobs[jobId].Process;

        var completed = proc.WaitForExit(Math.Clamp(timeoutSec, 1, 300) * 1000);

        if (completed)
        {
            lock (proc) { proc.WaitForExit(); }
            return $"Job #{jobId} completed (exit code: {proc.ExitCode})";
        }

        return $"Job #{jobId} still running after {timeoutSec}s";
    }

    [Description("Stop/kill a background job")]
    public static string StopJob(
        [Description("Job ID")] int jobId)
    {
        lock (_lock)
        {
            if (!_jobs.TryGetValue(jobId, out var job))
                return $"Job #{jobId} not found";

            if (job.Process.HasExited)
                return $"Job #{jobId} already exited";

            try
            {
                job.Process.Kill(entireProcessTree: true);
                job.Process.WaitForExit(5000);
                return $"Job #{jobId} stopped";
            }
            catch (Exception ex)
            {
                return $"Failed to stop job #{jobId}: {ex.Message}";
            }
        }
    }

    /// <summary>Clean up completed jobs older than threshold.</summary>
    public static int CleanupCompleted(TimeSpan? maxAge = null)
    {
        maxAge ??= TimeSpan.FromHours(1);
        var cutoff = DateTime.UtcNow - maxAge.Value;
        var removed = 0;

        lock (_lock)
        {
            var stale = _jobs.Where(j => j.Value.Process.HasExited && j.Value.StartedAt < cutoff).ToList();
            foreach (var (id, _) in stale)
            {
                _jobs.Remove(id);
                removed++;
            }
        }
        return removed;
    }
}
