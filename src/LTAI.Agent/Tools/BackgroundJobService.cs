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

public sealed class BackgroundJobService : IDisposable, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, JobEntry> _jobs = new();
    private readonly ConcurrentDictionary<string, Process> _runningProcesses = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _sessionJobs = new(StringComparer.Ordinal);
    private static readonly AsyncLocal<string?> _currentSessionId = new();
    private int _nextJobId;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;
    private readonly int _expirationSeconds;
    private readonly int _maxOutputChars;
    private readonly int _maxConcurrentJobs;
    private readonly int _processTimeoutSeconds;
    private long _startedCount;
    private long _completedCount;
    private volatile bool _paused;
    private readonly ManualResetEventSlim _pauseEvent = new(true);
    private readonly SemaphoreSlim _concurrencyGate;
    private readonly ConcurrentQueue<(string JobId, DateTime ExpiresAt)> _cleanupQueue = new();
    private int _cleanupLoopStarted;

    public static string? CurrentSessionId { get => _currentSessionId.Value; set => _currentSessionId.Value = value; }

    private string EffectiveSession => CurrentSessionId ?? "default";

    public BackgroundJobService(
        int? expirationSeconds = null, int? maxOutputChars = null,
        int? maxConcurrentJobs = null, int? processTimeoutSeconds = null)
        : this(
            expirationSeconds ?? ReadEnvInt("LTAI_JOB_EXPIRATION_SEC", 60),
            maxOutputChars ?? ReadEnvInt("LTAI_JOB_MAX_OUTPUT_CHARS", 100_000),
            maxConcurrentJobs ?? ReadEnvInt("LTAI_JOB_MAX_CONCURRENT", 10),
            processTimeoutSeconds ?? ReadEnvInt("LTAI_JOB_PROCESS_TIMEOUT_SEC", 300))
    { }

    private static int ReadEnvInt(string key, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(key), out var v) ? Math.Max(1, v) : fallback;

    private BackgroundJobService(int expirationSeconds, int maxOutputChars,
        int maxConcurrentJobs, int processTimeoutSeconds)
    {
        _expirationSeconds = Math.Max(10, expirationSeconds);
        _maxOutputChars = Math.Max(1024, maxOutputChars);
        _maxConcurrentJobs = Math.Max(1, maxConcurrentJobs);
        _processTimeoutSeconds = Math.Max(10, processTimeoutSeconds);
        _concurrencyGate = new SemaphoreSlim(_maxConcurrentJobs, _maxConcurrentJobs);
    }

    private void EnsureCleanupLoop()
    {
        if (Interlocked.CompareExchange(ref _cleanupLoopStarted, 1, 0) == 0)
        {
            _ = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(Math.Min(_expirationSeconds * 500, 30_000), _cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { break; }

                    var now = DateTime.UtcNow;
                    while (_cleanupQueue.TryPeek(out var item) && item.ExpiresAt < now)
                    {
                        if (_cleanupQueue.TryDequeue(out _))
                            _jobs.TryRemove(item.JobId, out _);
                    }
                }
            }, _cts.Token);
        }
    }

    public event Action<string, JobEntry>? JobCompleted;

    public BackgroundJobMetrics GetMetrics() => new(
        Interlocked.Read(ref _startedCount),
        Interlocked.Read(ref _completedCount),
        _runningProcesses.Count,
        _jobs.Count);

    [Description("启动后台 shell 命令并返回作业 ID。用 ListJobs/GetJobOutput 监控进度。注意：后台命令不受沙箱限制，执行前由 MAF ToolApprovalAgent 审批。")]
    public async Task<string> StartJob(
        [Description("Shell 命令")] string command)
    {
        if (!await _concurrencyGate.WaitAsync(0).ConfigureAwait(false))
            return $"Job rejected: max concurrent jobs ({_maxConcurrentJobs}) reached. Wait for running jobs to complete.";

        var id = Interlocked.Increment(ref _nextJobId).ToString();
        var entry = new JobEntry { Command = command, StartedAtUtc = DateTime.UtcNow };
        _jobs[id] = entry;
        var sess = EffectiveSession;
        _sessionJobs.AddOrUpdate(sess, _ => [id], (_, set) => { lock (set) set.Add(id); return set; });
        Interlocked.Increment(ref _startedCount);

        _ = RunJobCoreAsync(id, entry, command);

        return $"Job #{id} started.";
    }

    private void ReleaseConcurrency(string id)
    {
        _concurrencyGate.Release();
    }

    [Description("暂停所有后台作业。运行中的作业完成后不会启动新作业。")]
    public string Pause()
    {
        _paused = true;
        _pauseEvent.Reset();
        return "Background jobs paused. New jobs will queue until Resume().";
    }

    [Description("恢复所有后台作业。")]
    public string Resume()
    {
        _paused = false;
        _pauseEvent.Set();
        return "Background jobs resumed.";
    }

    [Description("检查后台作业是否已暂停。")]
    public bool IsPaused() => _paused;

    private async Task RunJobCoreAsync(string id, JobEntry entry, string command)
    {
        try
        {
            // Wait if paused
            try { _pauseEvent.Wait(_cts.Token); }
            catch (OperationCanceledException) { ReleaseConcurrency(id); return; }

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

            // Stream output with truncation to avoid OOM on large output
            var stdoutSb = new System.Text.StringBuilder();
            var stderrSb = new System.Text.StringBuilder();
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_processTimeoutSeconds));
            var stdoutTask = StreamOutputAsync(process.StandardOutput, stdoutSb, timeoutCts.Token);
            var stderrTask = StreamOutputAsync(process.StandardError, stderrSb, timeoutCts.Token);

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, timeoutCts.Token);
            try
            {
                await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch
                {
                    // non-critical, best-effort
                }
                entry.Error = $"Process killed: timeout ({_processTimeoutSeconds}s)";
                entry.ExitCode = -1;
                await stdoutTask.ConfigureAwait(false);
                await stderrTask.ConfigureAwait(false);
                entry.Output = TruncateOutput(stdoutSb.ToString());
                if (stderrSb.Length > 0) entry.Error += "\n" + TruncateOutput(stderrSb.ToString());
                entry.Completed = true;
                entry.CompletedEvent.Set();
                Interlocked.Increment(ref _completedCount);
                _runningProcesses.TryRemove(id, out var p);
                p?.Dispose();
                JobCompleted?.Invoke(id, entry);
                ScheduleCleanup(id);
                return;
            }

            await stdoutTask.ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);
            entry.Output = TruncateOutput(stdoutSb.ToString());
            entry.Error = stderrSb.Length > 0 ? TruncateOutput(stderrSb.ToString()) : null;
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
            ReleaseConcurrency(id);
        }
    }

    /// <summary>Stream process output into StringBuilder, truncating at _maxOutputChars.</summary>
    private async Task StreamOutputAsync(System.IO.StreamReader reader, System.Text.StringBuilder sb, CancellationToken ct = default)
    {
        var buf = new char[4096];
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false);
                if (read == 0) break;
                var remaining = _maxOutputChars - sb.Length;
                if (remaining <= 0) continue;
                var toAdd = Math.Min(read, remaining);
                sb.Append(buf, 0, toAdd);
                if (sb.Length >= _maxOutputChars)
                {
                    sb.Append($"\n... [output truncated at {_maxOutputChars} chars]");
                }
            }
        }
        catch { /* reader closed */ }
    }

    private void ScheduleCleanup(string id)
    {
        EnsureCleanupLoop();
        _cleanupQueue.Enqueue((id, DateTime.UtcNow.AddSeconds(_expirationSeconds)));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
        _concurrencyGate.Dispose();

        foreach (var kv in _runningProcesses)
        {
            try { kv.Value.Kill(entireProcessTree: true); } catch
            {
                // non-critical, best-effort
            }
            kv.Value.Dispose();
        }
        _runningProcesses.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
        _concurrencyGate.Dispose();

        foreach (var kv in _runningProcesses)
        {
            try { kv.Value.Kill(entireProcessTree: true); } catch
            {
                // non-critical, best-effort
            }
            kv.Value.Dispose();
        }
        _runningProcesses.Clear();

        // Wait briefly for cleanup tasks to complete
        try { await Task.Delay(500).ConfigureAwait(false); } catch
        {
            // non-critical, best-effort
        }
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
        try { await Task.Run(() => entry.CompletedEvent.Wait(timeout, _cts.Token)).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            // expected cancellation
        }

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
        entry.CompletedEvent.Set();
        _jobs.TryRemove(jobId, out _); // immediately remove, no cleanup delay
        ReleaseConcurrency(jobId);
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

    /// <summary>Truncate output to prevent OOM on large responses.</summary>
    private string TruncateOutput(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= _maxOutputChars)
            return text;
        return text[.._maxOutputChars] +
            $"\n... [truncated {text.Length - _maxOutputChars} chars, limit={_maxOutputChars}]";
    }
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

    public void Dispose()
    {
        CompletedEvent.Dispose();
    }
}
