using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;

namespace LTAI.Agent.Tools;

public sealed class BackgroundJobService : IDisposable
{
    private readonly ConcurrentDictionary<string, JobEntry> _jobs = new();
    private int _nextJobId;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public event Action<string, JobEntry>? JobCompleted;

    [Description("启动后台 shell 命令并返回作业 ID。用 ListJobs/GetJobOutput 监控进度。")]
    public async Task<string> StartJob(
        [Description("Shell 命令")] string command)
    {
        var id = Interlocked.Increment(ref _nextJobId).ToString();
        var entry = new JobEntry { Command = command, StartedAtUtc = DateTime.UtcNow };
        _jobs[id] = entry;

        _ = RunJobCoreAsync(id, entry, command);

        return $"Job #{id} started.";
    }

    private async Task RunJobCoreAsync(string id, JobEntry entry, string command)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Environment.OSVersion.Platform == PlatformID.Win32NT ? "cmd.exe" : "/bin/bash",
                Arguments = Environment.OSVersion.Platform == PlatformID.Win32NT ? $"/c {command}" : $"-c \"{command}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = new Process { StartInfo = psi };
            process.Start();
            entry.Output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            entry.Error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            entry.ExitCode = process.ExitCode;
        }
        catch (Exception ex)
        {
            entry.Error = ex.Message;
        }
        finally
        {
            entry.Completed = true;
            JobCompleted?.Invoke(id, entry);
            // Schedule cleanup after 60s with cancellation support
            ScheduleCleanup(id);
        }
    }

    private void ScheduleCleanup(string id)
    {
        // F3: catch-all exception handler — unobserved exception from Task.Run
        // in .NET Core 3.0+ crashes the finalizer thread.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(60_000, _cts.Token).ConfigureAwait(false);
                _jobs.TryRemove(id, out _);
            }
            catch (OperationCanceledException) { /* service shutting down */ }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"BGJS cleanup error: {ex.Message}");
            }
        }, _cts.Token);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }

    [Description("列出所有后台作业及状态")]
    public string ListJobs()
    {
        if (_jobs.IsEmpty) return "No background jobs.";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Background Jobs\n");
        sb.AppendLine("| ID | Status | Exit Code |");
        sb.AppendLine("|----|--------|-----------|");
        foreach (var kv in _jobs.OrderBy(kv => kv.Key))
        {
            var j = kv.Value;
            var status = j.Completed ? "✅ Done" : "⏳ Running";
            var code = j.Completed ? j.ExitCode?.ToString() ?? "?" : "-";
            sb.AppendLine($"| {kv.Key} | {status} | {code} |");
        }
        return sb.ToString();
    }

    [Description("获取已完成后台作业的输出")]
    public string GetJobOutput(
        [Description("作业 ID")] string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var entry))
            return $"Job #{jobId} not found.";
        if (!entry.Completed)
            return $"Job #{jobId} still running.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## Job #{jobId} Output\nExit Code: {entry.ExitCode}\n");
        if (!string.IsNullOrEmpty(entry.Output))
            sb.AppendLine("### stdout\n" + entry.Output);
        if (!string.IsNullOrEmpty(entry.Error))
            sb.AppendLine("### stderr\n" + entry.Error);
        return sb.ToString();
    }

    [Description("等待后台作业完成并返回输出")]
    public async Task<string> WaitForJob(
        [Description("作业 ID")] string jobId,
        [Description("超时秒数")] int timeoutSec = 300)
    {
        if (!_jobs.TryGetValue(jobId, out var entry))
            return $"Job #{jobId} not found.";

        var sw = Stopwatch.StartNew();
        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSec, 1, 600));
        while (!entry.Completed && sw.Elapsed < timeout)
            await Task.Delay(500, _cts.Token).ConfigureAwait(false);

        if (!entry.Completed)
            return $"Job #{jobId} did not complete within {timeoutSec}s.";
        return GetJobOutput(jobId);
    }

    [Description("停止运行中的后台作业")]
    public string StopJob(
        [Description("作业 ID")] string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var entry))
            return $"Job #{jobId} not found.";
        if (entry.Completed)
            return $"Job #{jobId} already completed.";
        entry.Completed = true;
        entry.Error = "Cancelled";
        return $"Job #{jobId} stopped.";
    }

    [Description("清除超过 1 小时的已完成作业")]
    public void CleanupOldJobs()
    {
        var cutoff = DateTime.UtcNow.AddHours(-1);
        foreach (var kv in _jobs)
        {
            if (kv.Value.Completed)
                _jobs.TryRemove(kv.Key, out _);
        }
    }

    /// <summary>
    /// P14.15: Web API surface. Snapshot of every live job keyed by ID, in
    /// stable order. Caller is expected to serialize — fields are all
    /// JSON-friendly primitives.
    /// </summary>
    public IReadOnlyDictionary<string, JobEntry> SnapshotJobs() => _jobs;

    /// <summary>P14.15: lookup a single job by ID. Returns null if missing.</summary>
    public JobEntry? GetJobEntry(string jobId) =>
        _jobs.TryGetValue(jobId, out var entry) ? entry : null;
}

public sealed class JobEntry
{
    public bool Completed;
    public int? ExitCode;
    public string? Output;
    public string? Error;
    public DateTime StartedAtUtc = DateTime.UtcNow;
    public string? Command;
}
