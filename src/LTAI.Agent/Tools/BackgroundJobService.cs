using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;

namespace LTAI.Agent.Tools;

/// <summary>
/// Metrics snapshot for <see cref="BackgroundJobService"/>. Thread-safe
/// read-only view surfaced in the DevUI dashboard.
/// </summary>
public sealed record BackgroundJobMetrics(
    long StartedCount,
    long CompletedCount,
    int RunningCount,
    int TotalJobs);

public sealed class BackgroundJobService : IDisposable
{
    private readonly ConcurrentDictionary<string, JobEntry> _jobs = new();
    private readonly ConcurrentDictionary<string, Process> _runningProcesses = new();
    // F13: Per-session job tracking. Maps session ID → set of job IDs.
    private readonly ConcurrentDictionary<string, HashSet<string>> _sessionJobs = new(StringComparer.Ordinal);
    private static readonly AsyncLocal<string?> _currentSessionId = new();
    private int _nextJobId;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;
    private readonly int _expirationSeconds;
    private long _startedCount;
    private long _completedCount;
    private readonly ToolTrustService? _trust;

    /// <summary>Set by ChatAgent before tool calls to scope jobs to a session.</summary>
    public static string? CurrentSessionId { get => _currentSessionId.Value; set => _currentSessionId.Value = value; }

    private string EffectiveSession => CurrentSessionId ?? "default";

    /// <summary>Default 60s cleanup for completed jobs. Override via constructor.</summary>
    public BackgroundJobService(int expirationSeconds = 60, ToolTrustService? trust = null)
    {
        _expirationSeconds = Math.Max(10, expirationSeconds);
        _trust = trust;
    }

    public event Action<string, JobEntry>? JobCompleted;

    public BackgroundJobMetrics GetMetrics() => new(
        Interlocked.Read(ref _startedCount),
        Interlocked.Read(ref _completedCount),
        _runningProcesses.Count,
        _jobs.Count);

    [Description("启动后台 shell 命令并返回作业 ID。用 ListJobs/GetJobOutput 监控进度。注意：后台命令不受沙箱限制，请确认后再使用。")]
    public async Task<string> StartJob(
        [Description("Shell 命令")] string command,
        [Description("确认执行。此命令不受沙箱限制，有安全风险。")] bool confirm = false)
    {
        if (!confirm && (_trust == null || _trust.RequiresConfirm("BackgroundJobService.StartJob")))
            return "⛔ 后台作业已取消：StartJob 不受沙箱限制，需设置 confirm=true 确认后执行。";

        var id = Interlocked.Increment(ref _nextJobId).ToString();
        var entry = new JobEntry { Command = command, StartedAtUtc = DateTime.UtcNow };
        _jobs[id] = entry;
        // F13: Track job per session
        var sess = EffectiveSession;
        _sessionJobs.AddOrUpdate(sess, _ => [id], (_, set) => { lock (set) set.Add(id); return set; });
        Interlocked.Increment(ref _startedCount);

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
                Arguments = Environment.OSVersion.Platform == PlatformID.Win32NT ? $"/c \"{command}\"" : $"-c '{command}'",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var process = new Process { StartInfo = psi };
            process.Start();
            _runningProcesses[id] = process;
            entry.Output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            entry.Error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            process.WaitForExit();
            entry.ExitCode = process.ExitCode;
        }
        catch (Exception ex)
        {
            entry.Error = ex.Message;
        }
        finally
        {
            entry.Completed = true;
            entry.CompletedEvent.Set();
            Interlocked.Increment(ref _completedCount);
            _runningProcesses.TryRemove(id, out var p);
            p?.Dispose();
            JobCompleted?.Invoke(id, entry);
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
                await Task.Delay(_expirationSeconds * 1000, _cts.Token).ConfigureAwait(false);
                _jobs.TryRemove(id, out _);
            }
            catch (OperationCanceledException) { /* service shutting down */ }
            catch (Exception) { }
        }, _cts.Token);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();

        foreach (var kv in _runningProcesses)
        {
            try { kv.Value.Kill(entireProcessTree: true); } catch { }
            kv.Value.Dispose();
        }
        _runningProcesses.Clear();
    }

    [Description("列出所有后台作业及状态")]
    public string ListJobs()
    {
        // F13: Only list jobs from the current session. Snapshot under lock.
        var scope = EffectiveSession;
        if (!_sessionJobs.TryGetValue(scope, out var ids)) return "No background jobs in this session.";
        string[] snapshot;
        lock (ids) { snapshot = [.. ids]; }
        if (snapshot.Length == 0) return "No background jobs in this session.";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Background Jobs\n");
        sb.AppendLine("| ID | Status | Exit Code |");
        sb.AppendLine("|----|--------|-----------|");
        foreach (var id in snapshot.OrderBy(id => id))
        {
            if (!_jobs.TryGetValue(id, out var j)) continue;
            var status = j.Completed ? "✅ Done" : "⏳ Running";
            var code = j.Completed ? j.ExitCode?.ToString() ?? "?" : "-";
            sb.AppendLine($"| {id} | {status} | {code} |");
        }
        return sb.ToString();
    }

    public string GetJobOutput(
        [Description("作业 ID")] string jobId)
    {
        // F13: Verify session ownership
        if (!IsOwnedBySession(jobId)) return $"Job #{jobId} not found.";
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
    /// <summary>F13: Check if a job belongs to the current session (or default session). Thread-safe via lock on the HashSet.</summary>
    private bool IsOwnedBySession(string jobId)
    {
        var scope = EffectiveSession;
        if (!_sessionJobs.TryGetValue(scope, out var ids)) return false;
        lock (ids) { return ids.Contains(jobId); }
    }

    public async Task<string> WaitForJob(
        [Description("作业 ID")] string jobId,
        [Description("超时秒数")] int timeoutSec = 300)
    {
        if (!IsOwnedBySession(jobId)) return $"Job #{jobId} not found.";
        if (!_jobs.TryGetValue(jobId, out var entry))
            return $"Job #{jobId} not found.";

        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSec, 1, 600));
        try { entry.CompletedEvent.Wait(timeout, _cts.Token); }
        catch (OperationCanceledException) { }

        if (!entry.Completed)
            return $"Job #{jobId} did not complete within {timeoutSec}s.";
        return GetJobOutput(jobId);
    }

    [Description("停止运行中的后台作业（杀死进程）")]
    public string StopJob(
        [Description("作业 ID")] string jobId)
    {
        if (!IsOwnedBySession(jobId)) return $"Job #{jobId} not found.";
        if (!_jobs.TryGetValue(jobId, out var entry))
            return $"Job #{jobId} not found.";
        if (entry.Completed)
            return $"Job #{jobId} already completed.";

        if (_runningProcesses.TryRemove(jobId, out var proc))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* process already gone */ }
            proc.Dispose();
        }

        entry.Completed = true;
        entry.Error = "Cancelled";
        return $"Job #{jobId} killed.";
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
    public IReadOnlyDictionary<string, JobEntry> SnapshotJobs() => new Dictionary<string, JobEntry>(_jobs);

    /// <summary>P14.15: lookup a single job by ID. Returns null if missing.</summary>
    public JobEntry? GetJobEntry(string jobId) =>
        _jobs.TryGetValue(jobId, out var entry) ? entry : null;
}

public sealed class JobEntry
{
    public volatile bool Completed;
    public int? ExitCode;
    public string? Output;
    public string? Error;
    public DateTime StartedAtUtc = DateTime.UtcNow;
    public string? Command;
    public readonly ManualResetEventSlim CompletedEvent = new(false);
}
