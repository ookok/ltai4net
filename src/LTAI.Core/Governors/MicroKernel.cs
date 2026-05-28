using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using LTAI.Core.System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.Governors;

// ============================================================================
// Kernel Result — unified return type for all primitives
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
    public string? Niche { get; init; }
    public Dictionary<string, string> Headers { get; init; } = new();
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}

// ============================================================================
// Kernel Permission flags
// ============================================================================

[Flags]
public enum KernelPermission
{
    None = 0,
    Read = 1 << 0,
    Write = 1 << 1,
    Execute = 1 << 2,
    Network = 1 << 3,
    Git = 1 << 4,
    Skill = 1 << 5,
    Memory = 1 << 6,
    Config = 1 << 7,
    Gene = 1 << 8,
    All = ~None
}

// ============================================================================
// Kernel Sandbox Configuration
// ============================================================================

public sealed record KernelSandboxConfig
{
    public HashSet<string> AllowedPaths { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> BlockedPaths { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> AllowedDomains { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> BlockedDomains { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> AllowedCommands { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public TimeSpan DefaultProcessTimeout { get; init; } = TimeSpan.FromSeconds(120);
    public long MaxFileReadBytes { get; init; } = 50 * 1024 * 1024;
    public long MaxFileWriteBytes { get; init; } = 10 * 1024 * 1024;
    public int MaxConcurrentOps { get; init; } = 16;
    public int MaxConcurrentProcesses { get; init; } = 4;
    public long MaxTotalBytesWritten { get; init; } = 100 * 1024 * 1024;
    public int RollbackFailureThreshold { get; init; } = 10;
    public bool EnforceNetworkFence { get; init; } = true;

    public static KernelSandboxConfig DevelopmentDefaults(string workspaceRoot)
    {
        var ws = Path.GetFullPath(workspaceRoot);
        return new KernelSandboxConfig
        {
            AllowedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ws,
                Path.Combine(ws, "rules"),
                Path.Combine(ws, "skills"),
                Path.Combine(ws, "memory"),
                Path.Combine(ws, "prompts"),
                Path.Combine(ws, "config"),
                Path.Combine(ws, "src"),
                Path.Combine(ws, "tests"),
                Path.Combine(ws, "docs"),
                Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
            },
            AllowedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "api.deepseek.com",
                "dashscope.aliyuncs.com",
                "api.openai.com",
                "api.anthropic.com",
                "huggingface.co",
                "api.github.com",
                "github.com",
                "localhost"
            },
            BlockedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.Combine(ws, ".git"),
            },
            BlockedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "169.254.169.254",
                "metadata.google.internal"
            },
            AllowedCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "dotnet",
                "git",
                "npm",
                "npx",
                "node",
                "python",
                "python3",
                "pwsh",
                "powershell",
                "cmd",
                "bash",
                "wsl",
                "docker",
                "curl",
                "gh",
                "7z",
                "tar",
                "zip",
                "unzip",
                "ffmpeg",
                "ffprobe",
                "sqlite3",
            }
        };
    }

    public static KernelSandboxConfig NicheIsolation(string workspaceRoot, string niche, string? worktreePath = null)
    {
        var ws = Path.GetFullPath(workspaceRoot);
        var nicheDir = worktreePath ?? Path.Combine(ws, "worktrees", niche);
        return new KernelSandboxConfig
        {
            AllowedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                nicheDir,
                Path.Combine(nicheDir, "src"),
                Path.Combine(nicheDir, "tests"),
                Path.Combine(nicheDir, "docs"),
                Path.Combine(ws, "rules"),
                Path.Combine(ws, "skills"),
            },
            BlockedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.Combine(nicheDir, ".git"),
                Path.Combine(nicheDir, "bin"),
                Path.Combine(nicheDir, "obj"),
            }
        };
    }
}

// ============================================================================
// Kernel Operation — for ExecuteAsync (CLI/Tool execution)
// ============================================================================

public sealed record KernelOp
{
    public string Command { get; init; } = "";
    public string? Arguments { get; init; }
    public string? WorkingDirectory { get; init; }
    public string? Niche { get; init; }
    public string? Stdin { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);
    public Dictionary<string, string> Environment { get; init; } = new();
    public KernelPermission RequiredPermission { get; init; } = KernelPermission.Execute;
}

// ============================================================================
// Circuit Breaker State
// ============================================================================

public sealed class KernelCircuitBreaker
{
    private readonly int _failureThreshold;
    private readonly TimeSpan _cooldown;
    private int _failureCount;
    private int _consecutiveFailures;
    private DateTime _lastFailure = DateTime.MinValue;
    private readonly object _lock = new();
    public event Action<int>? OnRollbackTriggered;

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

    public int ConsecutiveFailures
    {
        get { lock (_lock) return _consecutiveFailures; }
    }

    public void RecordSuccess()
    {
        lock (_lock) { _failureCount = 0; _consecutiveFailures = 0; }
    }

    public void RecordFailure()
    {
        lock (_lock)
        {
            _failureCount++;
            _consecutiveFailures++;
            _lastFailure = DateTime.UtcNow;
        }
    }
}

// ============================================================================
// Vital Sign — collected metrics for observability
// ============================================================================

public sealed record KernelVitalSign
{
    public string Primitive { get; init; } = "";
    public long CallCount { get; init; }
    public long SuccessCount { get; init; }
    public long FailureCount { get; init; }
    public double AvgLatencyMs { get; init; }
    public double P50LatencyMs { get; init; }
    public double P95LatencyMs { get; init; }
    public double P99LatencyMs { get; init; }
    public double SuccessRate => CallCount > 0 ? (double)SuccessCount / CallCount : 0;
    public DateTime LastUpdated { get; init; } = DateTime.UtcNow;
}

// ============================================================================
// Kernel Event — for publish/subscribe mechanism
// ============================================================================

public sealed record KernelEvent
{
    public string EventType { get; init; } = "";
    public string? Niche { get; init; }
    public Dictionary<string, object> Payload { get; init; } = new();
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public delegate Task KernelEventCallback(KernelEvent evt, CancellationToken ct);

// ============================================================================
// Kernel Snapshot — for backup/restore
// ============================================================================

public sealed record KernelSnapshot
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Description { get; init; } = "";
    public DateTime CapturedAt { get; init; } = DateTime.UtcNow;
    public Dictionary<string, string> ConfigState { get; init; } = new();
    public List<string> ActiveGeneIds { get; init; } = new();
    public Dictionary<string, double> GeneFitnessValues { get; init; } = new();
    public List<KernelVitalSign> VitalsAtCapture { get; init; } = new();
}

// ============================================================================
// Audit Trail Entry
// ============================================================================

public sealed record KernelAuditEntry
{
    public string TraceId { get; init; } = "";
    /// <summary>Unified correlation ID from ITraceContext — links MicroKernel audit entries to ParetoRouter, ArchitectLoop, and PartStreamStore events.</summary>
    public string? CorrelationId { get; init; }
    public string Primitive { get; init; } = "";
    public bool Success { get; init; }
    public long ElapsedMs { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string? Summary { get; init; }
    public int? DataLength { get; init; }
    public double? RiskScore { get; init; }
}

// ============================================================================
// IMicroKernel — Agent OS microkernel interface (8-function architecture)
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

    Task<KernelResult> AdjustParameterAsync(string component, string key, object value, CancellationToken ct = default);
    Task<KernelResult> LoadGeneAsync(string geneId, string niche, CancellationToken ct = default);
    Task<KernelResult> UnloadGeneAsync(string geneId, CancellationToken ct = default);
    Task<KernelResult> SnapshotAsync(string description, CancellationToken ct = default);
    Task<KernelResult> RestoreAsync(string snapshotId, CancellationToken ct = default);

    string Subscribe(string eventType, KernelEventCallback callback, string? niche = null);
    bool Unsubscribe(string subscriptionId);

    string IssueCapToken(string subject, KernelPermission permissions, string targetPath, TimeSpan ttl);
    Task<KernelResult> WriteFileWithToken(string capToken, string content, CancellationToken ct = default);
    bool RevokeCapToken(string capToken);
    CapTokenInfo? ValidateCapToken(string capToken);

    IReadOnlyList<KernelAuditEntry> GetAuditTrail(int limit = 100);
    IReadOnlyList<KernelVitalSign> GetVitalSigns();
    KernelVitalSign GetAggregatedVitals();
    IReadOnlyList<KernelSnapshot> GetSnapshots();
    bool IsHealthy { get; }
}

// ============================================================================
// MicroKernel — concrete implementation (Agent OS microkernel)
// ============================================================================

public sealed class MicroKernel : IMicroKernel
{
    public static IMicroKernel? Default { get; set; }

    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private readonly ConcurrentQueue<KernelAuditEntry> _auditTrail = new();
    private readonly KernelCircuitBreaker _circuitBreaker;
    private readonly string _workspaceRoot;
    private readonly KernelSandboxConfig _sandboxConfig;
    private readonly SemanticDiffAgent? _diffAgent;
    private Func<string, string, CancellationToken, Task<string>>? _gitOpHandler;
    private Func<string, string, CancellationToken, Task<string>>? _skillHandler;
    private Func<string, int, CancellationToken, Task<string>>? _memoryHandler;
    private readonly int _maxAuditEntries;
    private readonly ConcurrentDictionary<string, (Task Task, CancellationTokenSource Cts)> _scheduledTasks = new();

    private readonly ConcurrentDictionary<string, (string EventType, string? Niche, KernelEventCallback Callback)> _subscriptions = new();
    private readonly ConcurrentQueue<KernelSnapshot> _snapshots = new();
    private readonly KernelCapToken _capToken;

    private readonly ConcurrentDictionary<string, KernelSandboxConfig> _nicheSandboxes = new();

    // Per-config PathPrefixSet cache for O(log n) sandbox path lookups (was O(n))
    private readonly ConcurrentDictionary<KernelSandboxConfig, (PathPrefixSet Allowed, PathPrefixSet Blocked)> _pathPrefixCache = new();

    private int _activeProcesses;
    private long _totalBytesWritten;

    private readonly ConcurrentDictionary<string, (long[] Latencies, long Successes, long Failures)> _vitals = new();
    private readonly object _vitalsLock = new();
    private static readonly string[] _vitalPrimitives =
    {
        "execute", "read_file", "write_file", "git", "http", "skill", "memory", "schedule"
    };

    private SemaphoreSlim? _concurrencyGate;
    private int _rollbackInProgress;
    private readonly ITraceContext? _traceContext;

    public GenePool? GenePool { get; set; }
    public BootstrapTeacher? Teacher { get; set; }

    public MicroKernel(
        string workspaceRoot,
        HttpClient? http = null,
        Func<string, string, CancellationToken, Task<string>>? gitOpHandler = null,
        Func<string, string, CancellationToken, Task<string>>? skillHandler = null,
        Func<string, int, CancellationToken, Task<string>>? memoryHandler = null,
        KernelSandboxConfig? sandboxConfig = null,
        SemanticDiffAgent? diffAgent = null,
        int maxAuditEntries = 1000,
        ILogger? logger = null,
        ITraceContext? traceContext = null)
    {
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _http = http ?? new HttpClient();
        _gitOpHandler = gitOpHandler;
        _skillHandler = skillHandler;
        _memoryHandler = memoryHandler;
        _sandboxConfig = sandboxConfig ?? KernelSandboxConfig.DevelopmentDefaults(workspaceRoot);
        _diffAgent = diffAgent;
        _maxAuditEntries = maxAuditEntries;
        _logger = logger ?? NullLogger.Instance;
        _circuitBreaker = new KernelCircuitBreaker(_sandboxConfig.RollbackFailureThreshold);
        _traceContext = traceContext;
        _circuitBreaker.OnRollbackTriggered += async (count) => await HandleRollbackAsync(count);
        _concurrencyGate = new SemaphoreSlim(_sandboxConfig.MaxConcurrentOps);
        _capToken = new KernelCapToken(_workspaceRoot);

        foreach (var p in _vitalPrimitives)
            _vitals[p] = (Array.Empty<long>(), 0, 0);
    }

    public bool IsHealthy => !_circuitBreaker.IsOpen;

    public void SetNicheSandbox(string niche, KernelSandboxConfig config)
    {
        _nicheSandboxes[niche] = config;
        _logger.LogInformation("MicroKernel: registered sandbox for niche '{Niche}' (allowedPaths={AllowedCount})",
            niche, config.AllowedPaths.Count);
    }

    private KernelSandboxConfig GetSandboxForNiche(string? niche)
    {
        if (!string.IsNullOrEmpty(niche) && _nicheSandboxes.TryGetValue(niche, out var nicheConfig))
            return nicheConfig;
        return _sandboxConfig;
    }

    // ========================================================================
    // Primitive 1: ExecuteAsync — CLI / Process execution (sandboxed)
    // ========================================================================

    public async Task<KernelResult> ExecuteAsync(KernelOp op, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var traceId = Guid.NewGuid().ToString("N")[..8];

        try
        {
            if (_circuitBreaker.IsOpen)
                return AuditAndFail(traceId, "execute", "Circuit breaker open — too many failures", sw);

            if (Interlocked.Increment(ref _activeProcesses) > _sandboxConfig.MaxConcurrentProcesses)
            {
                Interlocked.Decrement(ref _activeProcesses);
                return AuditAndFail(traceId, "execute",
                    $"Process limit exceeded ({_activeProcesses - 1}/{_sandboxConfig.MaxConcurrentProcesses})", sw);
            }

            var sandboxCheck = ValidateExecutePath(op);
            if (!sandboxCheck.Safe)
            {
                Interlocked.Decrement(ref _activeProcesses);
                return AuditAndFail(traceId, "execute", sandboxCheck.Reason, sw, sandboxCheck.RiskScore);
            }

            await _concurrencyGate!.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(op.Timeout);

                var procStartInfo = new ProcessStartInfo
                {
                    FileName = op.Command,
                    Arguments = op.Arguments ?? "",
                    WorkingDirectory = op.WorkingDirectory ?? _workspaceRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = !string.IsNullOrEmpty(op.Stdin),
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                foreach (var (k, v) in op.Environment)
                    procStartInfo.Environment[k] = v;

                using var proc = new Process { StartInfo = procStartInfo };
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
                    RecordVital("execute", false, sw.ElapsedMilliseconds);
                    _circuitBreaker.RecordFailure();
                    return result;
                }

                Audit(traceId, "execute", true, sw.ElapsedMilliseconds, stdoutTask.Result[..Math.Min(stdoutTask.Result.Length, 100)]);
                RecordVital("execute", true, sw.ElapsedMilliseconds);
                _circuitBreaker.RecordSuccess();
                return KernelResult.Ok(stdoutTask.Result, sw.ElapsedMilliseconds, traceId);
            }
            finally
            {
                _concurrencyGate.Release();
                Interlocked.Decrement(ref _activeProcesses);
            }
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            Interlocked.Decrement(ref _activeProcesses);
            Audit(traceId, "execute", false, sw.ElapsedMilliseconds, "timeout");
            RecordVital("execute", false, sw.ElapsedMilliseconds);
            _circuitBreaker.RecordFailure();
            return KernelResult.Timeout(op.Command, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            Interlocked.Decrement(ref _activeProcesses);
            Audit(traceId, "execute", false, sw.ElapsedMilliseconds, ex.Message);
            RecordVital("execute", false, sw.ElapsedMilliseconds);
            _circuitBreaker.RecordFailure();
            return KernelResult.Fail(ex.Message, sw.ElapsedMilliseconds);
        }
    }

    // ========================================================================
    // Primitive 2: ReadFileAsync (sandboxed)
    // ========================================================================

    public async Task<KernelResult> ReadFileAsync(string path, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var traceId = Guid.NewGuid().ToString("N")[..8];

        try
        {
            var (fullPath, check) = ValidatePath(path, KernelPermission.Read);
            if (!check.Safe)
                return AuditAndFail(traceId, "read_file", check.Reason, sw, check.RiskScore);

            if (!File.Exists(fullPath))
            {
                sw.Stop();
                return KernelResult.Fail($"File not found: {path}", sw.ElapsedMilliseconds);
            }

            var fi = new FileInfo(fullPath);
            if (fi.Length > _sandboxConfig.MaxFileReadBytes)
                return AuditAndFail(traceId, "read_file", $"File too large: {fi.Length} bytes (> {_sandboxConfig.MaxFileReadBytes})", sw);

            var content = await File.ReadAllTextAsync(fullPath, ct).ConfigureAwait(false);
            sw.Stop();

            var summary = content.Length > 100 ? $"{content.Length} bytes" : content;
            Audit(traceId, "read_file", true, sw.ElapsedMilliseconds, summary);
            RecordVital("read_file", true, sw.ElapsedMilliseconds);
            return KernelResult.Ok(content, sw.ElapsedMilliseconds, traceId);
        }
        catch (Exception ex)
        {
            sw.Stop();
            Audit(traceId, "read_file", false, sw.ElapsedMilliseconds, ex.Message);
            RecordVital("read_file", false, sw.ElapsedMilliseconds);
            return KernelResult.Fail(ex.Message, sw.ElapsedMilliseconds);
        }
    }

    // ========================================================================
    // Primitive 3: WriteFileAsync — atomic temp+rename (sandboxed)
    // ========================================================================

    public async Task<KernelResult> WriteFileAsync(string path, string content, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var traceId = Guid.NewGuid().ToString("N")[..8];

        try
        {
            var (fullPath, check) = ValidatePath(path, KernelPermission.Write);
            if (!check.Safe)
                return AuditAndFail(traceId, "write_file", check.Reason, sw, check.RiskScore);

            if (content.Length > _sandboxConfig.MaxFileWriteBytes)
                return AuditAndFail(traceId, "write_file", $"Content too large: {content.Length} bytes (> {_sandboxConfig.MaxFileWriteBytes})", sw);

            if (Interlocked.Add(ref _totalBytesWritten, content.Length) > _sandboxConfig.MaxTotalBytesWritten)
            {
                Interlocked.Add(ref _totalBytesWritten, -content.Length);
                return AuditAndFail(traceId, "write_file",
                    $"Total write quota exceeded ({_totalBytesWritten}/{_sandboxConfig.MaxTotalBytesWritten})", sw);
            }

            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var tmpPath = fullPath + ".tmp." + Guid.NewGuid().ToString("N")[..8];
            await File.WriteAllTextAsync(tmpPath, content, ct).ConfigureAwait(false);
            File.Move(tmpPath, fullPath, true);

            sw.Stop();

            Audit(traceId, "write_file", true, sw.ElapsedMilliseconds, $"{content.Length} bytes → {path}");
            RecordVital("write_file", true, sw.ElapsedMilliseconds);
            return KernelResult.Ok($"Written {content.Length} bytes", sw.ElapsedMilliseconds, traceId);
        }
        catch (Exception ex)
        {
            sw.Stop();
            Audit(traceId, "write_file", false, sw.ElapsedMilliseconds, ex.Message);
            RecordVital("write_file", false, sw.ElapsedMilliseconds);
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
            RecordVital("git", true, sw.ElapsedMilliseconds);
            return KernelResult.Ok(result, sw.ElapsedMilliseconds, traceId);
        }
        catch (Exception ex)
        {
            sw.Stop();
            Audit(traceId, "git", false, sw.ElapsedMilliseconds, ex.Message);
            RecordVital("git", false, sw.ElapsedMilliseconds);
            return KernelResult.Fail(ex.Message, sw.ElapsedMilliseconds);
        }
    }

    // ========================================================================
    // Primitive 5: HttpRequestAsync (network-fenced)
    // ========================================================================

    public async Task<KernelResult> HttpRequestAsync(KernelHttpRequest req, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var traceId = Guid.NewGuid().ToString("N")[..8];

        try
        {
            var fenceCheck = ValidateNetworkFence(req.Url, req.Niche);
            if (!fenceCheck.Safe)
                return AuditAndFail(traceId, "http", fenceCheck.Reason, sw, fenceCheck.RiskScore);

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
                RecordVital("http", false, sw.ElapsedMilliseconds);
                _circuitBreaker.RecordFailure();
                return errResult;
            }

            Audit(traceId, "http", true, sw.ElapsedMilliseconds, $"{req.Method} {req.Url}: {body.Length} bytes");
            RecordVital("http", true, sw.ElapsedMilliseconds);
            _circuitBreaker.RecordSuccess();
            return KernelResult.Ok(body, sw.ElapsedMilliseconds, traceId);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            Audit(traceId, "http", false, sw.ElapsedMilliseconds, "timeout");
            RecordVital("http", false, sw.ElapsedMilliseconds);
            _circuitBreaker.RecordFailure();
            return KernelResult.Timeout($"HTTP {req.Method} {req.Url}", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            Audit(traceId, "http", false, sw.ElapsedMilliseconds, ex.Message);
            RecordVital("http", false, sw.ElapsedMilliseconds);
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
            RecordVital("skill", true, sw.ElapsedMilliseconds);
            return KernelResult.Ok(result, sw.ElapsedMilliseconds, traceId);
        }
        catch (Exception ex)
        {
            sw.Stop();
            Audit(traceId, "skill", false, sw.ElapsedMilliseconds, ex.Message);
            RecordVital("skill", false, sw.ElapsedMilliseconds);
            return KernelResult.Fail(ex.Message, sw.ElapsedMilliseconds);
        }
    }

    // ========================================================================
    // Primitive 7: QueryMemoryAsync
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
            RecordVital("memory", true, sw.ElapsedMilliseconds);
            return KernelResult.Ok(result, sw.ElapsedMilliseconds, traceId);
        }
        catch (Exception ex)
        {
            sw.Stop();
            Audit(traceId, "memory", false, sw.ElapsedMilliseconds, ex.Message);
            RecordVital("memory", false, sw.ElapsedMilliseconds);
            return KernelResult.Fail(ex.Message, sw.ElapsedMilliseconds);
        }
    }

    // ========================================================================
    // Primitive 8: ScheduleAsync
    // ========================================================================

    public Task<KernelResult> ScheduleAsync(string taskId, string command, TimeSpan interval, bool recurring, CancellationToken ct = default)
    {
        if (_scheduledTasks.ContainsKey(taskId))
            return Task.FromResult(new KernelResult { Success = false, Error = $"Task '{taskId}' already scheduled", TraceId = taskId });

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var task = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, cts.Token).ConfigureAwait(false);
                    var op = new KernelOp { Command = command, Timeout = _sandboxConfig.DefaultProcessTimeout };
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
        RecordVital("schedule", true, 0);
        _logger.LogInformation("Scheduled task '{TaskId}': {Command} every {Interval}s (recurring={Recurring})",
            taskId, command, interval.TotalSeconds, recurring);

        return Task.FromResult(new KernelResult { Success = true, TraceId = taskId });
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

    // ========================================================================
    // Evolution Primitive 1: AdjustParameterAsync
    // ========================================================================

    public async Task<KernelResult> AdjustParameterAsync(string component, string key, object value, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var traceId = Guid.NewGuid().ToString("N")[..8];

        try
        {
            switch (component.ToLowerInvariant())
            {
                case "teacher" or "bootstrapteacher":
                    if (Teacher == null)
                        return KernelResult.Fail("Teacher not configured", sw.ElapsedMilliseconds);

                    switch (key.ToLowerInvariant())
                    {
                        case "teachingquota":
                            Teacher.TeachingQuota = Convert.ToInt32(value);
                            break;
                        case "teachingaccuracythreshold":
                            Teacher.TeachingAccuracyThreshold = Convert.ToDouble(value);
                            break;
                        case "shadowingaccuracythreshold":
                            Teacher.ShadowingAccuracyThreshold = Convert.ToDouble(value);
                            break;
                        case "stalematethreshold":
                            Teacher.StalemateThreshold = Convert.ToInt32(value);
                            break;
                        case "stalematerelaxstep":
                            Teacher.StalemateRelaxStep = Convert.ToDouble(value);
                            break;
                        case "maxrelaxation":
                            Teacher.MaxRelaxation = Convert.ToDouble(value);
                            break;
                        default:
                            return KernelResult.Fail($"Unknown parameter '{key}' for component '{component}'", sw.ElapsedMilliseconds);
                    }
                    break;

                default:
                    return KernelResult.Fail($"Unknown component '{component}'", sw.ElapsedMilliseconds);
            }

            sw.Stop();
            Audit(traceId, "config", true, sw.ElapsedMilliseconds, $"set {component}.{key} = {value}");
            PublishEvent("config.changed", null, new Dictionary<string, object>
            {
                ["component"] = component, ["key"] = key, ["value"] = value
            });
            return KernelResult.Ok($"Adjusted {component}.{key} = {value}", sw.ElapsedMilliseconds, traceId);
        }
        catch (Exception ex)
        {
            sw.Stop();
            Audit(traceId, "config", false, sw.ElapsedMilliseconds, ex.Message);
            return KernelResult.Fail(ex.Message, sw.ElapsedMilliseconds);
        }
    }

    // ========================================================================
    // Evolution Primitive 2: LoadGeneAsync
    // ========================================================================

    public Task<KernelResult> LoadGeneAsync(string geneId, string niche, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            if (GenePool == null)
                return Task.FromResult(KernelResult.Fail("GenePool not configured", sw.ElapsedMilliseconds));

            var gene = GenePool.AllGenes.FirstOrDefault(g => g.Id == geneId);
            if (gene == null)
                return Task.FromResult(KernelResult.Fail($"Gene '{geneId}' not found in pool", sw.ElapsedMilliseconds));

            var activated = gene with { Niche = niche, Source = "kernel_loaded", CreatedAt = DateTime.UtcNow };
            GenePool.AddGene(activated);
            GenePool.UpdateFitness(geneId, 0.7);

            sw.Stop();
            var traceId = Guid.NewGuid().ToString("N")[..8];
            Audit(traceId, "gene", true, sw.ElapsedMilliseconds, $"loaded {geneId} → {niche}");
            PublishEvent("gene.loaded", niche, new Dictionary<string, object>
            {
                ["geneId"] = geneId, ["niche"] = niche
            });
            return Task.FromResult(KernelResult.Ok($"Gene {geneId} loaded to niche {niche}", sw.ElapsedMilliseconds, traceId));
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Task.FromResult(KernelResult.Fail(ex.Message, sw.ElapsedMilliseconds));
        }
    }

    // ========================================================================
    // Evolution Primitive 3: UnloadGeneAsync
    // ========================================================================

    public Task<KernelResult> UnloadGeneAsync(string geneId, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            if (GenePool == null)
                return Task.FromResult(KernelResult.Fail("GenePool not configured", sw.ElapsedMilliseconds));

            var removed = GenePool.RemoveGene(geneId);

            sw.Stop();
            var traceId = Guid.NewGuid().ToString("N")[..8];
            if (removed)
            {
                Audit(traceId, "gene", true, sw.ElapsedMilliseconds, $"unloaded {geneId}");
                PublishEvent("gene.unloaded", null, new Dictionary<string, object>
                {
                    ["geneId"] = geneId
                });
                return Task.FromResult(KernelResult.Ok($"Gene {geneId} unloaded", sw.ElapsedMilliseconds, traceId));
            }

            return Task.FromResult(KernelResult.Fail($"Gene '{geneId}' not found in pool", sw.ElapsedMilliseconds));
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Task.FromResult(KernelResult.Fail(ex.Message, sw.ElapsedMilliseconds));
        }
    }

    // ========================================================================
    // Evolution Primitive 4: SnapshotAsync
    // ========================================================================

    public Task<KernelResult> SnapshotAsync(string description, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            var vitalsSnapshot = GetVitalSigns();
            var topGenes = GenePool?.SelectTopN(20).ToList() ?? new List<Gene>();
            var activeGeneIds = topGenes.Select(g => g.Id).ToList();
            var geneFitnessSnapshot = topGenes.ToDictionary(g => g.Id, g => g.Fitness);
            var configState = new Dictionary<string, string>();

            if (Teacher != null)
            {
                configState["Teacher.TeachingQuota"] = Teacher.TeachingQuota.ToString();
                configState["Teacher.TeachingAccuracyThreshold"] = Teacher.TeachingAccuracyThreshold.ToString("F3");
                configState["Teacher.ShadowingAccuracyThreshold"] = Teacher.ShadowingAccuracyThreshold.ToString("F3");
                configState["Teacher.StalemateThreshold"] = Teacher.StalemateThreshold.ToString();
                configState["Teacher.Phase"] = Teacher.Phase.ToString();
            }

            configState["Sandbox.MaxConcurrentProcesses"] = _sandboxConfig.MaxConcurrentProcesses.ToString();
            configState["Sandbox.MaxTotalBytesWritten"] = _sandboxConfig.MaxTotalBytesWritten.ToString();
            configState["Kernel.TotalBytesWritten"] = _totalBytesWritten.ToString();

            var snapshot = new KernelSnapshot
            {
                Description = description,
                ConfigState = configState,
                ActiveGeneIds = activeGeneIds,
                GeneFitnessValues = geneFitnessSnapshot,
                VitalsAtCapture = vitalsSnapshot.ToList()
            };

            _snapshots.Enqueue(snapshot);
            while (_snapshots.Count > 20)
                _snapshots.TryDequeue(out _);

            sw.Stop();
            var traceId = Guid.NewGuid().ToString("N")[..8];
            Audit(traceId, "snapshot", true, sw.ElapsedMilliseconds, $"{snapshot.Id}: {description}");
            PublishEvent("system.snapshot", null, new Dictionary<string, object>
            {
                ["snapshotId"] = snapshot.Id, ["description"] = description
            });
            return Task.FromResult(KernelResult.Ok($"Snapshot {snapshot.Id} captured", sw.ElapsedMilliseconds, traceId));
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Task.FromResult(KernelResult.Fail(ex.Message, sw.ElapsedMilliseconds));
        }
    }

    // ========================================================================
    // Evolution Primitive 5: RestoreAsync
    // ========================================================================

    public Task<KernelResult> RestoreAsync(string snapshotId, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            var snapshot = _snapshots.FirstOrDefault(s => s.Id == snapshotId);
            if (snapshot == null)
                return Task.FromResult(KernelResult.Fail($"Snapshot '{snapshotId}' not found", sw.ElapsedMilliseconds));

            if (Teacher != null)
            {
                foreach (var (key, value) in snapshot.ConfigState)
                {
                    var parts = key.Split('.');
                    if (parts.Length != 2) continue;

                    if (parts[0] == "Teacher")
                    {
                        switch (parts[1])
                        {
                            case "TeachingQuota":
                                Teacher.TeachingQuota = int.Parse(value);
                                break;
                            case "TeachingAccuracyThreshold":
                                Teacher.TeachingAccuracyThreshold = double.Parse(value);
                                break;
                            case "ShadowingAccuracyThreshold":
                                Teacher.ShadowingAccuracyThreshold = double.Parse(value);
                                break;
                        }
                    }
                }
            }

            if (GenePool != null && snapshot.GeneFitnessValues.Count > 0)
            {
                foreach (var (geneId, fitness) in snapshot.GeneFitnessValues)
                    GenePool.UpdateFitness(geneId, fitness);
                _logger.LogInformation("Restored fitness for {Count} genes", snapshot.GeneFitnessValues.Count);
            }

            sw.Stop();
            var traceId = Guid.NewGuid().ToString("N")[..8];
            Audit(traceId, "restore", true, sw.ElapsedMilliseconds, $"restored snapshot {snapshotId}");
            PublishEvent("system.restored", null, new Dictionary<string, object>
            {
                ["snapshotId"] = snapshotId
            });
            return Task.FromResult(KernelResult.Ok($"Restored snapshot {snapshotId}: {snapshot.ConfigState.Count} config keys", sw.ElapsedMilliseconds, traceId));
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Task.FromResult(KernelResult.Fail(ex.Message, sw.ElapsedMilliseconds));
        }
    }

    // ========================================================================
    // Event Subscription
    // ========================================================================

    public string Subscribe(string eventType, KernelEventCallback callback, string? niche = null)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        _subscriptions[id] = (eventType, niche, callback);
        _logger.LogDebug("Subscribed {Id} to event '{Type}' (niche={Niche})", id, eventType, niche ?? "*");
        return id;
    }

    public bool Unsubscribe(string subscriptionId)
    {
        return _subscriptions.TryRemove(subscriptionId, out _);
    }

    private void PublishEvent(string eventType, string? niche, Dictionary<string, object> payload)
    {
        var evt = new KernelEvent { EventType = eventType, Niche = niche, Payload = payload };
        var ct = CancellationToken.None;

        foreach (var (_, (subType, subNiche, callback)) in _subscriptions)
        {
            if (!MatchesEvent(subType, subNiche, eventType, niche)) continue;

            _ = Task.Run(async () =>
            {
                try { await callback(evt, ct).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogDebug(ex, "Event subscriber failed for {Type}", eventType); }
            });
        }
    }

    private static bool MatchesEvent(string subType, string? subNiche, string eventType, string? eventNiche)
    {
        if (!string.Equals(subType, eventType, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(subType, "*", StringComparison.Ordinal))
            return false;

        if (subNiche != null && !string.Equals(subNiche, eventNiche, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    // ========================================================================
    // Rollback Circuit Breaker
    // ========================================================================

    private async Task HandleRollbackAsync(int consecutiveFailures)
    {
        if (Interlocked.CompareExchange(ref _rollbackInProgress, 1, 0) != 0) return;

        try
        {
            _logger.LogWarning(
                "Rollback circuit breaker triggered: {Count} consecutive failures — attempting auto-revert",
                consecutiveFailures);

            await GitOpAsync("revert", "--hard HEAD~1", CancellationToken.None).ConfigureAwait(false);

            if (Teacher != null)
                await Teacher.ResetAsync(CancellationToken.None).ConfigureAwait(false);

            PublishEvent("system.rollback", null, new Dictionary<string, object>
            {
                ["consecutiveFailures"] = consecutiveFailures
            });

            _logger.LogInformation("Auto-rollback completed after {Count} consecutive failures", consecutiveFailures);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-rollback failed");
        }
        finally
        {
            Interlocked.Exchange(ref _rollbackInProgress, 0);
        }
    }

    // ========================================================================
    // Vital Signs Collection
    // ========================================================================

    private void RecordVital(string primitive, bool success, long latencyMs)
    {
        lock (_vitalsLock)
        {
            if (!_vitals.TryGetValue(primitive, out var entry))
            {
                entry = (Array.Empty<long>(), 0, 0);
                _vitals[primitive] = entry;
            }

            var newLatencies = entry.Latencies.Length < 1000
                ? entry.Latencies.Append(latencyMs).ToArray()
                : entry.Latencies.Skip(1).Append(latencyMs).ToArray();

            _vitals[primitive] = (
                newLatencies,
                entry.Successes + (success ? 1 : 0),
                entry.Failures + (success ? 0 : 1)
            );
        }
    }

    public IReadOnlyList<KernelVitalSign> GetVitalSigns()
    {
        lock (_vitalsLock)
        {
            return _vitals.Select(kv =>
            {
                var (latencies, successes, failures) = kv.Value;
                var sorted = latencies.OrderBy(l => l).ToArray();
                return new KernelVitalSign
                {
                    Primitive = kv.Key,
                    CallCount = successes + failures,
                    SuccessCount = successes,
                    FailureCount = failures,
                    AvgLatencyMs = sorted.Length > 0 ? sorted.Average() : 0,
                    P50LatencyMs = sorted.Length > 0 ? Percentile(sorted, 50) : 0,
                    P95LatencyMs = sorted.Length > 0 ? Percentile(sorted, 95) : 0,
                    P99LatencyMs = sorted.Length > 0 ? Percentile(sorted, 99) : 0
                };
            }).ToList().AsReadOnly();
        }
    }

    public KernelVitalSign GetAggregatedVitals()
    {
        lock (_vitalsLock)
        {
            var allLatencies = _vitals.Values.SelectMany(v => v.Latencies).OrderBy(l => l).ToArray();
            var totalSuccesses = _vitals.Values.Sum(v => v.Successes);
            var totalFailures = _vitals.Values.Sum(v => v.Failures);

            return new KernelVitalSign
            {
                Primitive = "aggregate",
                CallCount = totalSuccesses + totalFailures,
                SuccessCount = totalSuccesses,
                FailureCount = totalFailures,
                AvgLatencyMs = allLatencies.Length > 0 ? allLatencies.Average() : 0,
                P50LatencyMs = Percentile(allLatencies, 50),
                P95LatencyMs = Percentile(allLatencies, 95),
                P99LatencyMs = Percentile(allLatencies, 99)
            };
        }
    }

    private static double Percentile(long[] sorted, int percentile)
    {
        if (sorted.Length == 0) return 0;
        var index = (int)Math.Ceiling(percentile / 100.0 * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    public IReadOnlyList<KernelSnapshot> GetSnapshots()
    {
        return _snapshots.ToList().AsReadOnly();
    }

    // ========================================================================
    // Audit Trail
    // ========================================================================

    public IReadOnlyList<KernelAuditEntry> GetAuditTrail(int limit = 100)
    {
        return _auditTrail.TakeLast(limit).ToList().AsReadOnly();
    }

    private void Audit(string traceId, string primitive, bool success, long elapsedMs, string? summary, double riskScore = 0.0)
    {
        var entry = new KernelAuditEntry
        {
            TraceId = traceId,
            CorrelationId = _traceContext?.TraceId,
            Primitive = primitive,
            Success = success,
            ElapsedMs = elapsedMs,
            Summary = summary,
            RiskScore = riskScore
        };

        _auditTrail.Enqueue(entry);

        while (_auditTrail.Count > _maxAuditEntries)
            _auditTrail.TryDequeue(out _);
    }

    private KernelResult AuditAndFail(string traceId, string primitive, string reason, Stopwatch sw, double riskScore = 0.0)
    {
        sw.Stop();
        Audit(traceId, primitive, false, sw.ElapsedMilliseconds, reason, riskScore);
        RecordVital(primitive, false, sw.ElapsedMilliseconds);
        _circuitBreaker.RecordFailure();
        return KernelResult.Fail(reason, sw.ElapsedMilliseconds);
    }

    // ========================================================================
    // Sandbox Validation
    // ========================================================================

    private (string FullPath, DiffSafetyResult Check) ValidatePath(string path, KernelPermission permission, string? niche = null)
    {
        var fullPath = Path.GetFullPath(
            Path.IsPathRooted(path) ? path : Path.Combine(_workspaceRoot, path));

        var config = GetSandboxForNiche(niche);
        var (allowedSet, blockedSet) = _pathPrefixCache.GetOrAdd(config, static cfg =>
        {
            var allowed = new PathPrefixSet(cfg.AllowedPaths);
            var blocked = new PathPrefixSet(cfg.BlockedPaths);
            return (allowed, blocked);
        });

        if (!allowedSet.ContainsPrefix(fullPath))
            return (fullPath, new DiffSafetyResult
            {
                Safe = false,
                Reason = $"Path '{fullPath}' is not in allowed sandbox paths{(niche != null ? $" [niche: {niche}]" : "")}",
                RiskScore = 0.9
            });

        if (blockedSet.ContainsPrefix(fullPath))
            return (fullPath, new DiffSafetyResult
            {
                Safe = false,
                Reason = $"Path '{fullPath}' is blocked by sandbox{(niche != null ? $" [niche: {niche}]" : "")}",
                RiskScore = 1.0
            });

        return (fullPath, new DiffSafetyResult { Safe = true });
    }

    private DiffSafetyResult ValidateExecutePath(KernelOp op)
    {
        var workingDir = op.WorkingDirectory ?? _workspaceRoot;
        var (_, dirCheck) = ValidatePath(workingDir, KernelPermission.Execute, op.Niche);
        if (!dirCheck.Safe) return dirCheck;

        var config = GetSandboxForNiche(op.Niche);
        if (config.AllowedCommands.Count > 0)
        {
            var cmdName = Path.GetFileNameWithoutExtension((op.Command ?? "").Trim('"'));
            if (!string.IsNullOrEmpty(cmdName) && !config.AllowedCommands.Contains(cmdName))
                return new DiffSafetyResult
                {
                    Safe = false,
                    Reason = $"Command '{cmdName}' is not in allowed command whitelist{(op.Niche != null ? $" [niche: {op.Niche}]" : "")}",
                    RiskScore = 0.85
                };
        }

        if (_diffAgent == null)
            return new DiffSafetyResult { Safe = true };

        var gene = new Gene
        {
            Condition = $"command=\"{(op.Command ?? "")}\"",
            Action = $"exec:{(op.Arguments ?? "")}",
            TargetModule = workingDir,
            RouteLabel = op.RequiredPermission.ToString(),
            Niche = op.Niche ?? "general"
        };

        return _diffAgent.EvaluateGene(gene);
    }

    private DiffSafetyResult ValidateNetworkFence(string url, string? niche = null)
    {
        var config = GetSandboxForNiche(niche);
        if (!config.EnforceNetworkFence)
            return new DiffSafetyResult { Safe = true };

        try
        {
            var uri = new Uri(url);
            var host = uri.Host.ToLowerInvariant();

            var isAllowed = config.AllowedDomains.Count == 0
                || config.AllowedDomains.Any(d =>
                    host == d || host.EndsWith("." + d, StringComparison.Ordinal));

            if (!isAllowed)
                return new DiffSafetyResult
                {
                    Safe = false,
                    Reason = $"Domain '{host}' is not in allowed network fence{(niche != null ? $" [niche: {niche}]" : "")}",
                    RiskScore = 0.75
                };

            var isBlocked = config.BlockedDomains.Any(d =>
                host == d || host.EndsWith("." + d, StringComparison.Ordinal));

            if (isBlocked)
                return new DiffSafetyResult
                {
                    Safe = false,
                    Reason = $"Domain '{host}' is blocked by network fence{(niche != null ? $" [niche: {niche}]" : "")}",
                    RiskScore = 0.95
                };

            return new DiffSafetyResult { Safe = true };
        }
        catch
        {
            return new DiffSafetyResult
            {
                Safe = false,
                Reason = $"Invalid URL: {url}",
                RiskScore = 0.5
            };
        }
    }

    // ========================================================================
    // Capability Token — object-capability security model
    // ========================================================================

    public string IssueCapToken(string subject, KernelPermission permissions, string targetPath, TimeSpan ttl)
    {
        return _capToken.Issue(subject, permissions, targetPath, ttl);
    }

    public async Task<KernelResult> WriteFileWithToken(string capToken, string content, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var traceId = Guid.NewGuid().ToString("N")[..8];

        try
        {
            var info = ValidateCapToken(capToken);
            if (info is not { Valid: true })
            {
                sw.Stop();
                return KernelResult.Fail($"CapToken rejected: {info?.Reason ?? "invalid"}", sw.ElapsedMilliseconds);
            }

            if ((info.Permissions & KernelPermission.Write) == 0)
            {
                sw.Stop();
                return KernelResult.Fail($"CapToken lacks Write permission", sw.ElapsedMilliseconds);
            }

            var hash = (uint)content.GetHashCode();
            var fullPath = Path.GetFullPath(Path.Combine(info.TargetPath, $"{hash:x}.capwrite"));

            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var tmpPath = fullPath + ".tmp." + Guid.NewGuid().ToString("N")[..8];
            await File.WriteAllTextAsync(tmpPath, content, ct).ConfigureAwait(false);
            File.Move(tmpPath, fullPath, true);
            sw.Stop();

            Audit(traceId, "write_file_with_token", true, sw.ElapsedMilliseconds,
                $"{content.Length} bytes → {fullPath} by {info.Subject}");
            RecordVital("write_file", true, sw.ElapsedMilliseconds);
            return KernelResult.Ok($"Written {content.Length} bytes by token", sw.ElapsedMilliseconds, traceId);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return KernelResult.Fail(ex.Message, sw.ElapsedMilliseconds);
        }
    }

    public bool RevokeCapToken(string capToken)
    {
        _capToken.Revoke(capToken);
        return true;
    }

    public CapTokenInfo? ValidateCapToken(string capToken)
    {
        var result = _capToken.Validate(capToken);
        return result.Valid ? result : (result.Reason != null ? result : null);
    }

    // ========================================================================
    // Helpers
    // ========================================================================
}
