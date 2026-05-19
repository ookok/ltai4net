using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LTAI.Metrics.Audit;

public record AuditEvent(
    string Id,
    DateTime Timestamp,
    string SessionId,
    string Stage,
    string Phase,
    string Operation,
    string Target,
    string? ParamsHash,
    string? ResultSummary,
    string? SideEffects,
    bool Success,
    string? Error,
    double DurationMs,
    Dictionary<string, object?>? Metadata
);

public sealed class AuditLog
{
    private static readonly Lazy<AuditLog> _instance = new(() => new AuditLog());
    public static AuditLog Instance => _instance.Value;

    private readonly ILogger<AuditLog> _logger;
    private readonly ConcurrentDictionary<string, AuditEvent> _events = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _sessionIndex = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _operationIndex = new();
    private int _totalWritten;
    private int _totalErrors;
    private readonly object _lock = new();

    public AuditLog(ILogger<AuditLog>? logger = null)
    {
        _logger = logger ?? NullLogger<AuditLog>.Instance;
    }

    private static string GenerateId() => $"auevt_{Guid.NewGuid().ToString("N")[..12]}";

    private static string? HashParams(Dictionary<string, object?>? parameters)
    {
        if (parameters == null) return null;
        var sorted = new SortedDictionary<string, object?>(parameters);
        var json = JsonSerializer.Serialize(sorted);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash)[..16];
    }

    public string Record(
        string stage,
        string phase,
        string operation,
        string target,
        Dictionary<string, object?>? parameters,
        string? result,
        string? sideEffects,
        bool success,
        string? error,
        double durationMs,
        string? sessionId = null,
        Dictionary<string, object?>? metadata = null)
    {
        var id = GenerateId();
        var timestamp = DateTime.UtcNow;
        var paramsHash = HashParams(parameters);
        sessionId ??= id;

        var auditEvent = new AuditEvent(
            id,
            timestamp,
            sessionId,
            stage,
            phase,
            operation,
            target,
            paramsHash,
            result,
            sideEffects,
            success,
            error,
            durationMs,
            metadata
        );

        _events[id] = auditEvent;

        lock (_lock)
        {
            var sessionSet = _sessionIndex.GetOrAdd(sessionId, _ => new HashSet<string>());
            sessionSet.Add(id);

            var opSet = _operationIndex.GetOrAdd(operation, _ => new HashSet<string>());
            opSet.Add(id);
        }

        Interlocked.Increment(ref _totalWritten);

        if (!success)
        {
            Interlocked.Increment(ref _totalErrors);
        }

        if (_events.Count > 10000)
        {
            lock (_lock)
            {
                if (_events.Count > 10000)
                {
                    var oldest = _events.Values.OrderBy(e => e.Timestamp).FirstOrDefault();
                    if (oldest != null && _events.TryRemove(oldest.Id, out _))
                    {
                        if (_sessionIndex.TryGetValue(oldest.SessionId, out var sessSet))
                        {
                            sessSet.Remove(oldest.Id);
                            if (sessSet.Count == 0)
                                _sessionIndex.TryRemove(oldest.SessionId, out _);
                        }
                        if (_operationIndex.TryGetValue(oldest.Operation, out var opSet))
                        {
                            opSet.Remove(oldest.Id);
                            if (opSet.Count == 0)
                                _operationIndex.TryRemove(oldest.Operation, out _);
                        }
                    }
                }
            }
        }

        return id;
    }

    public string RecordStart(
        string stage,
        string operation,
        string target,
        Dictionary<string, object?>? parameters = null,
        string? sessionId = null,
        Dictionary<string, object?>? metadata = null)
    {
        return Record(stage, "start", operation, target, parameters, null, null, true, null, 0, sessionId, metadata);
    }

    public string RecordEnd(
        string stage,
        string operation,
        string target,
        string? result,
        string? sideEffects,
        bool success,
        string? error,
        double durationMs,
        string? sessionId = null,
        Dictionary<string, object?>? metadata = null)
    {
        return Record(stage, "end", operation, target, null, result, sideEffects, success, error, durationMs, sessionId, metadata);
    }

    public List<AuditEvent> Query(
        string? sessionId = null,
        string? stage = null,
        string? operation = null,
        bool? success = null,
        DateTime? since = null,
        DateTime? until = null,
        int limit = 100)
    {
        var query = _events.Values.AsEnumerable();

        if (sessionId != null)
            query = query.Where(e => e.SessionId == sessionId);

        if (stage != null)
            query = query.Where(e => e.Stage == stage);

        if (operation != null)
            query = query.Where(e => e.Operation == operation);

        if (success.HasValue)
            query = query.Where(e => e.Success == success.Value);

        if (since.HasValue)
            query = query.Where(e => e.Timestamp >= since.Value);

        if (until.HasValue)
            query = query.Where(e => e.Timestamp <= until.Value);

        return query
            .OrderByDescending(e => e.Timestamp)
            .Take(limit)
            .ToList();
    }

    public List<AuditEvent> ReconstructChain(string sessionId)
    {
        return _events.Values
            .Where(e => e.SessionId == sessionId)
            .OrderBy(e => e.Timestamp)
            .ToList();
    }

    public Dictionary<string, object> GetFailureReport(string? sessionId = null)
    {
        var events = sessionId != null
            ? _events.Values.Where(e => e.SessionId == sessionId).ToList()
            : _events.Values.ToList();

        var failures = events.Where(e => !e.Success).ToList();

        var operationsBreakdown = events
            .GroupBy(e => e.Operation)
            .ToDictionary(g => g.Key, g => (object)g.Count());

        var sideEffectsList = events
            .Where(e => e.SideEffects != null)
            .Select(e => e.SideEffects!)
            .ToList();

        return new Dictionary<string, object>
        {
            ["totalEvents"] = events.Count,
            ["failures"] = failures.Count,
            ["totalDurationMs"] = events.Sum(e => e.DurationMs),
            ["operations"] = operationsBreakdown,
            ["sideEffects"] = sideEffectsList,
            ["recentFailures"] = failures
                .OrderByDescending(e => e.Timestamp)
                .Take(10)
                .Select(e => new Dictionary<string, object?>
                {
                    ["id"] = e.Id,
                    ["timestamp"] = e.Timestamp,
                    ["stage"] = e.Stage,
                    ["operation"] = e.Operation,
                    ["target"] = e.Target,
                    ["error"] = e.Error,
                    ["durationMs"] = e.DurationMs
                })
                .ToList<object>()
        };
    }

    public Dictionary<string, object> GetStats()
    {
        return new Dictionary<string, object>
        {
            ["total_written"] = Volatile.Read(ref _totalWritten),
            ["total_errors"] = Volatile.Read(ref _totalErrors),
            ["in_memory"] = _events.Count,
            ["session_index"] = _sessionIndex.Count,
            ["operation_index"] = _operationIndex.Count
        };
    }
}
