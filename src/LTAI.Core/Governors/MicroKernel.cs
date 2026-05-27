using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.Governors;

// ============================================================================
// Kernel Result — unified return type for all 6 primitives
// ============================================================================

public sealed record KernelResult
{
    public bool Success { get; init; }
    public string Data { get; init; } = "";
    public string Error { get; init; } = "";
    public long ElapsedMs { get; init; }
    public string TraceId { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public Dictionary<string, object> Metadata { get; init; } = new();

    public static KernelResult Ok(string data, long elapsedMs = 0, string? traceId = null) => new()
    {
        Success = true, Data = data, ElapsedMs = elapsedMs,
        TraceId = traceId ?? Guid.NewGuid().ToString("N")[..8]
    };

    public static KernelResult Fail(string error, long elapsedMs = 0) => new()
    {
        Success = false, Error = error, ElapsedMs = elapsedMs
    };

    public static KernelResult Timeout(string op, long elapsedMs) => new()
    {
        Success = false, Error = $"{op} timed out", ElapsedMs = elapsedMs
    };
}

// ============================================================================
// Kernel HTTP Request model
// ============================================================================

public sealed record KernelHttpRequest
{
    public string Url { get; init; } = "";
    public string Method { get; init; } = "GET";
    public string? Body { get; init; }
    public Dictionary<string, string> Headers { get; init; } = new();
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}

// ============================================================================
// Kernel Operation — for ExecuteAsync (CLI/Tool execution)
// ============================================================================

public sealed record KernelOp
{
    public string Command { get; init; } = "";
    public string? Arguments { get; init; }
    public string? WorkingDirectory { get; init; }
    public string? Stdin { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);
    public Dictionary<string, string> Environment { get; init; } = new();
}

// ============================================================================
// Circuit Breaker State
// ============================================================================

public sealed class KernelCircuitBreaker
{
    private readonly int _failureThreshold;
    private readonly TimeSpan _cooldown;
    private int _failureCount;
    private DateTime _lastFailure = DateTime.MinValue;
    private readonly object _lock = new();

    public KernelCircuitBreaker(int failureThreshold = 5, TimeSpan? cooldown = null)
    {
        _failureThreshold = failureThreshold;
        _cooldown = cooldown ?? TimeSpan.FromSeconds(30);
    }

    public bool IsOpen
    {
        get
        {
            lock (_lock)
            {
                if (_failureCount < _failureThreshold) return false;
                if (DateTime.UtcNow - _lastFailure > _cooldown)
                {
                    _failureCount = 0;
                    return false;
                }
                return true;
            }
        }
    }

    public void RecordSuccess()
    {
        lock (_lock) { _failureCount = 0; }
    }

    public void RecordFailure()
    {
        lock (_lock)
        {
            _failureCount++;
            _lastFailure = DateTime.UtcNow;
        }
    }
}

// ============================================================================
// Audit Trail Entry
// ============================================================================

public sealed record KernelAuditEntry
{
    public string TraceId { get; init; } = "";
    public string Primitive { get; init; } = "";
    public bool Success { get; init; }
    public long ElapsedMs { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string? Summary { get; init; }
    public int? DataLength { get; init; }
}

// ============================================================================
// IMicroKernel — 6-primitive interface
// ============================================================================

public interface IMicroKernel
{
    Task<KernelResult> ExecuteAsync(KernelOp op, CancellationToken ct = default);
    Task<KernelResult> ReadFileAsync(string path, CancellationToken ct = default);
    Task<KernelResult> WriteFileAsync(string path, string content, CancellationToken ct = default);
    Task<KernelResult> GitOpAsync(string opCode, string args, CancellationToken ct = default);
    Task<KernelResult> HttpRequestAsync(KernelHttpRequest req, CancellationToken ct = default);
    Task<KernelResult> InvokeSkillAsync(string skillName, string input, CancellationToken ct = default);
    Task<KernelResult> QueryMemoryAsync(string query, int topK, CancellationToken ct = default);
    Task<KernelResult> ScheduleAsync(string taskId, string command, TimeSpan interval, bool recurring, CancellationToken ct = default);
    Task<KernelResult> CancelScheduleAsync(string taskId, CancellationToken ct = default);
    IReadOnlyList<KernelAuditEntry> GetAuditTrail(int limit = 100);
    bool IsHealthy { get; }
}

// ============================================================================
// MicroKernel — concrete implementation
// ============================================================================

public sealed class MicroKernel : IMicroKernel
{
    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private readonly ConcurrentQueue<KernelAuditEntry> _auditTrail = new();
    private readonly KernelCircuitBreaker _circuitBreaker;
    private readonly string _workspaceRoot;
    private Func<string, string, CancellationToken, Task<string>>? _gitOpHandler;
    private Func<string, string, CancellationToken, Task<string>>? _skillHandler;
    private Func<string, int, CancellationToken, Task<string>>? _memoryHandler;
    private readonly int _maxAuditEntries;
    private readonly ConcurrentDictionary<string, (Task Task, CancellationTokenSource Cts)> _scheduledTasks = new();

    public MicroKernel(
        string workspaceRoot,
        HttpClient? http = null,
        Func<string, string, CancellationToken, Task<string>>? gitOpHandler = null,
        Func<string, string, CancellationToken, Task<string>>? skillHandler = null,
        Func<string, int, CancellationToken, Task<string>>? memoryHandler = null,
        int maxAuditEntries = 1000,
        ILogger? logger = null)
    {
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _http = http ?? new HttpClient();
        _gitOpHandler = gitOpHandler;
        _skillHandler = skillHandler;
        _memoryHandler = memoryHandler;
        _maxAuditEntries = maxAuditEntries;
        _logger = logger ?? NullLogger.Instance;
        _circuitBreaker = new KernelCircuitBreaker();
    }

    public bool IsHealthy => !_circuitBreaker.IsOpen;

    // ========================================================================
    // Primitive 1: ExecuteAsync — CLI / Process execution
    // ========================================================================

    public async Task<KernelResult> ExecuteAsync(KernelOp op, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var traceId = Guid.NewGuid().ToString("N")[..8];

        try
        {
            if (_circuitBreaker.IsOpen)
                return KernelResult.Fail("Circuit breaker open — too many failures", sw.ElapsedMilliseconds);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(op.Timeout);

            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = op.Command,
                    Arguments = op.Arguments ?? "",
                    WorkingDirectory = op.WorkingDirectory ?? _workspaceRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = !string.IsNullOrEmpty(op.Stdin),
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            foreach (var (k, v) in op.Environment)
                proc.StartInfo.Environment[k] = v;

            proc.Start();

            if (!string.IsNullOrEmpty(op.Stdin))
            {
                await proc.StandardInput.WriteAsync(op.Stdin);
                proc.StandardInput.Close();
            }

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            var waitTask = proc.WaitForExitAsync(timeoutCts.Token);
            await Task.WhenAll(stdoutTask, stderrTask, waitTask).ConfigureAwait(false);

            sw.Stop();

            if (proc.ExitCode != 0)
            {
                var err = stderrTask.Result;
                if (string.IsNullOrEmpty(err)) err = stdoutTask.Result;
                var result = KernelResult.Fail($"Exit code {proc.ExitCode}: {err[..Math.Min(err.Length, 500)]}", sw.ElapsedMilliseconds);
                Audit(traceId, "execute", false, sw.ElapsedMilliseconds, err[..Math.Min(err.Length, 200)]);
                _circuitBreaker.RecordFailure();
                return result;
            }

            Audit(traceId, "execute", true, sw.ElapsedMilliseconds, stdoutTask.Result[..Math.Min(stdoutTask.Result.Length, 100)]);
            _circuitBreaker.RecordSuccess();
            return KernelResult.Ok(stdoutTask.Result, sw.ElapsedMilliseconds, traceId);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            Audit(traceId, "execute", false, sw.ElapsedMilliseconds, "timeout");
            _circuitBreaker.RecordFailure();
            return KernelResult.Timeout(op.Command, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            Audit(traceId, "execute", false, sw.ElapsedMilliseconds, ex.Message);
            _circuitBreaker.RecordFailure();
            return KernelResult.Fail(ex.Message, sw.ElapsedMilliseconds);
        }
    }

    // ========================================================================
    // Primitive 2: ReadFileAsync
    // ========================================================================

    public async Task<KernelResult> ReadFileAsync(string path, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var traceId = Guid.NewGuid().ToString("N")[..8];

        try
        {
            var fullPath = ResolvePath(path);
            if (!File.Exists(fullPath))
            {
                sw.Stop();
                return KernelResult.Fail($"File not found: {path}", sw.ElapsedMilliseconds);
            }

            var content = await File.ReadAllTextAsync(fullPath, ct).ConfigureAwait(false);
            sw.Stop();

            if (content.Length > 10_000)
                Audit(traceId, "read_file", true, sw.ElapsedMilliseconds, $"{content.Length} bytes");
            else
                Audit(traceId, "read_file", true, sw.ElapsedMilliseconds, content[..Math.Min(content.Length, 100)]);

            return KernelResult.Ok(content, sw.ElapsedMilliseconds, traceId);
        }
        catch (Exception ex)
        {
            sw.Stop();
            Audit(traceId, "read_file", false, sw.ElapsedMilliseconds, ex.Message);
            return KernelResult.Fail(ex.Message, sw.ElapsedMilliseconds);
        }
    }

    // ========================================================================
    // Primitive 3: WriteFileAsync
    // ========================================================================

    public async Task<KernelResult> WriteFileAsync(string path, string content, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var traceId = Guid.NewGuid().ToString("N")[..8];

        try
        {
            var fullPath = ResolvePath(path);
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(fullPath, content, ct).ConfigureAwait(false);
            sw.Stop();

            Audit(traceId, "write_file", true, sw.ElapsedMilliseconds, $"{content.Length} bytes → {path}");
            return KernelResult.Ok($"Written {content.Length} bytes", sw.ElapsedMilliseconds, traceId);
        }
        catch (Exception ex)
        {
            sw.Stop();
            Audit(traceId, "write_file", false, sw.ElapsedMilliseconds, ex.Message);
            return KernelResult.Fail(ex.Message, sw.ElapsedMilliseconds);
        }
    }

    // ========================================================================
    // Primitive 4: GitOpAsync
    // ========================================================================

    public async Task<KernelResult> GitOpAsync(string opCode, string args, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var traceId = Guid.NewGuid().ToString("N")[..8];

        try
        {
            if (_gitOpHandler == null)
            {
                sw.Stop();
                return KernelResult.Fail("Git operation handler not configured", sw.ElapsedMilliseconds);
            }

            var result = await _gitOpHandler(opCode, args, ct).ConfigureAwait(false);
            sw.Stop();

            Audit(traceId, "git", true, sw.ElapsedMilliseconds, $"{opCode} {args[..Math.Min(args.Length, 50)]}");
            return KernelResult.Ok(result, sw.ElapsedMilliseconds, traceId);
        }
        catch (Exception ex)
        {
            sw.Stop();
            Audit(traceId, "git", false, sw.ElapsedMilliseconds, ex.Message);
            return KernelResult.Fail(ex.Message, sw.ElapsedMilliseconds);
        }
    }

    // ========================================================================
    // Primitive 5: HttpRequestAsync
    // ========================================================================

    public async Task<KernelResult> HttpRequestAsync(KernelHttpRequest req, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var traceId = Guid.NewGuid().ToString("N")[..8];

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(req.Timeout);

            using var request = new HttpRequestMessage(new HttpMethod(req.Method), req.Url);

            if (!string.IsNullOrEmpty(req.Body) && req.Method is "POST" or "PUT" or "PATCH")
                request.Content = new StringContent(req.Body, global::System.Text.Encoding.UTF8, "application/json");

            foreach (var (k, v) in req.Headers)
                request.Headers.TryAddWithoutValidation(k, v);

            using var response = await _http.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                var errResult = KernelResult.Fail($"HTTP {(int)response.StatusCode}: {body[..Math.Min(body.Length, 200)]}", sw.ElapsedMilliseconds);
                Audit(traceId, "http", false, sw.ElapsedMilliseconds, $"{(int)response.StatusCode} {req.Url}");
                _circuitBreaker.RecordFailure();
                return errResult;
            }

            Audit(traceId, "http", true, sw.ElapsedMilliseconds, $"{req.Method} {req.Url}: {body.Length} bytes");
            _circuitBreaker.RecordSuccess();
            return KernelResult.Ok(body, sw.ElapsedMilliseconds, traceId);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            Audit(traceId, "http", false, sw.ElapsedMilliseconds, "timeout");
            _circuitBreaker.RecordFailure();
            return KernelResult.Timeout($"HTTP {req.Method} {req.Url}", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            Audit(traceId, "http", false, sw.ElapsedMilliseconds, ex.Message);
            _circuitBreaker.RecordFailure();
            return KernelResult.Fail(ex.Message, sw.ElapsedMilliseconds);
        }
    }

    // ========================================================================
    // Primitive 6: InvokeSkillAsync
    // ========================================================================

    public async Task<KernelResult> InvokeSkillAsync(string skillName, string input, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var traceId = Guid.NewGuid().ToString("N")[..8];

        try
        {
            if (_skillHandler == null)
            {
                sw.Stop();
                return KernelResult.Fail("Skill handler not configured", sw.ElapsedMilliseconds);
            }

            var result = await _skillHandler(skillName, input, ct).ConfigureAwait(false);
            sw.Stop();

            Audit(traceId, "skill", true, sw.ElapsedMilliseconds, $"{skillName}: {(result.Length > 100 ? result[..100] : result)}");
            return KernelResult.Ok(result, sw.ElapsedMilliseconds, traceId);
        }
        catch (Exception ex)
        {
            sw.Stop();
            Audit(traceId, "skill", false, sw.ElapsedMilliseconds, ex.Message);
            return KernelResult.Fail(ex.Message, sw.ElapsedMilliseconds);
        }
    }

    // ========================================================================
    // Primitive 6b: QueryMemoryAsync
    // ========================================================================

    public async Task<KernelResult> QueryMemoryAsync(string query, int topK, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var traceId = Guid.NewGuid().ToString("N")[..8];

        try
        {
            if (_memoryHandler == null)
            {
                sw.Stop();
                return KernelResult.Fail("Memory handler not configured", sw.ElapsedMilliseconds);
            }

            var result = await _memoryHandler(query, topK, ct).ConfigureAwait(false);
            sw.Stop();

            Audit(traceId, "memory", true, sw.ElapsedMilliseconds, $"topK={topK}: {result.Length} bytes");
            return KernelResult.Ok(result, sw.ElapsedMilliseconds, traceId);
        }
        catch (Exception ex)
        {
            sw.Stop();
            Audit(traceId, "memory", false, sw.ElapsedMilliseconds, ex.Message);
            return KernelResult.Fail(ex.Message, sw.ElapsedMilliseconds);
        }
    }

    // ========================================================================
    // Audit Trail
    // ========================================================================

    public IReadOnlyList<KernelAuditEntry> GetAuditTrail(int limit = 100)
    {
        return _auditTrail.TakeLast(limit).ToList().AsReadOnly();
    }

    private void Audit(string traceId, string primitive, bool success, long elapsedMs, string? summary)
    {
        var entry = new KernelAuditEntry
        {
            TraceId = traceId,
            Primitive = primitive,
            Success = success,
            ElapsedMs = elapsedMs,
            Summary = summary
        };

        _auditTrail.Enqueue(entry);

        while (_auditTrail.Count > _maxAuditEntries)
            _auditTrail.TryDequeue(out _);
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path)) return path;
        return Path.Combine(_workspaceRoot, path);
    }

    public async Task<KernelResult> ScheduleAsync(string taskId, string command, TimeSpan interval, bool recurring, CancellationToken ct = default)
    {
        if (_scheduledTasks.ContainsKey(taskId))
            return new KernelResult { Success = false, Error = $"Task '{taskId}' already scheduled", TraceId = taskId };

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var task = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, cts.Token).ConfigureAwait(false);
                    var op = new KernelOp { Command = command };
                    await ExecuteAsync(op, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Scheduled task {TaskId} failed", taskId);
                }

                if (!recurring) break;
            }
        }, cts.Token);

        _scheduledTasks[taskId] = (task, cts);
        _logger.LogInformation("Scheduled task '{TaskId}': {Command} every {Interval}s (recurring={Recurring})",
            taskId, command, interval.TotalSeconds, recurring);

        return new KernelResult { Success = true, TraceId = taskId };
    }

    public async Task<KernelResult> CancelScheduleAsync(string taskId, CancellationToken ct = default)
    {
        if (_scheduledTasks.TryRemove(taskId, out var entry))
        {
            entry.Cts.Cancel();
            try { await entry.Task.ConfigureAwait(false); } catch { }
            _logger.LogInformation("Cancelled scheduled task '{TaskId}'", taskId);
            return new KernelResult { Success = true, TraceId = taskId };
        }

        return new KernelResult { Success = false, Error = $"Task '{taskId}' not found" };
    }
}
