using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LTAI.Metrics.Monitoring;

public enum EventType
{
    Election,
    ToolCall,
    Cache,
    Eval,
    Synthesize,
    Modify,
    Consolidate,
    Error,
    Provider,
    Network,
    System
}

public enum EventSeverity
{
    Info,
    Warn,
    Error,
    Success
}

public record ActivityEvent(
    EventType Type,
    string Agent,
    string Message,
    DateTime Timestamp,
    EventSeverity Severity,
    Dictionary<string, object?>? Metadata = null
);

public sealed class ActivityFeed
{
    public static readonly Lazy<ActivityFeed> Instance = new(() => new ActivityFeed());

    private readonly ILogger<ActivityFeed> _logger;
    private readonly List<ActivityEvent> _buffer = new();
    private readonly List<Action<ActivityEvent>> _subscribers = new();
    private readonly object _lock = new();

    private const int MaxBufferSize = 500;
    private const int MaxSubscribers = 10;

    public ActivityFeed(ILogger<ActivityFeed>? logger = null)
    {
        _logger = logger ?? NullLogger<ActivityFeed>.Instance;
    }

    public ActivityEvent Log(
        EventType eventType,
        string agent,
        string message,
        EventSeverity severity = EventSeverity.Info,
        Dictionary<string, object?>? metadata = null)
    {
        var activityEvent = new ActivityEvent(
            eventType,
            agent,
            message,
            DateTime.UtcNow,
            severity,
            metadata);

        lock (_lock)
        {
            _buffer.Add(activityEvent);
            while (_buffer.Count > MaxBufferSize)
            {
                _buffer.RemoveAt(0);
            }

            foreach (var subscriber in _subscribers)
            {
                try
                {
                    subscriber(activityEvent);
                }
                catch
                {
                }
            }
        }

        _logger.LogInformation(
            "[ActivityFeed] {Type} | {Agent} | {Severity} | {Message}",
            eventType, agent, severity, message);

        return activityEvent;
    }

    public void Election(string provider, double score, string reason)
    {
        var message = $"Provider: {provider}, Score: {score:F2}, Reason: {reason}";
        Log(EventType.Election, provider, message, EventSeverity.Success);
    }

    public void ToolCall(string tool, bool success, double latencyMs, string? details = null)
    {
        var message = $"Tool: {tool}, Success: {success}, Latency: {latencyMs:F2}ms";
        if (!string.IsNullOrWhiteSpace(details))
        {
            message += $", Details: {details}";
        }

        Log(EventType.ToolCall, tool, message,
            success ? EventSeverity.Success : EventSeverity.Error);
    }

    public void CacheHit(string provider, double hitRate, double savings)
    {
        var message = $"Provider: {provider}, HitRate: {hitRate:P1}, Savings: {savings:F2}";
        Log(EventType.Cache, provider, message, EventSeverity.Success);
    }

    public void Synthesize(string tool, bool success, string version)
    {
        var message = $"Tool: {tool}, Success: {success}, Version: {version}";
        Log(EventType.Synthesize, tool, message,
            success ? EventSeverity.Success : EventSeverity.Error);
    }

    public void Modify(List<string> files, bool success)
    {
        var filesList = string.Join(", ", files);
        var message = $"Files: [{filesList}], Success: {success}";
        Log(EventType.Modify, "Modify", message,
            success ? EventSeverity.Success : EventSeverity.Error);
    }

    public void Consolidate(string topic, int count)
    {
        var message = $"Topic: {topic}, Count: {count}";
        Log(EventType.Consolidate, topic, message, EventSeverity.Success);
    }

    public void Error(string agent, string errorMsg, Dictionary<string, object?>? metadata = null)
    {
        Log(EventType.Error, agent, errorMsg, EventSeverity.Error, metadata);
    }

    public void ProviderEvent(string provider, string message, EventSeverity sev)
    {
        Log(EventType.Provider, provider, message, sev);
    }

    public List<ActivityEvent> Query(
        int limit = 20,
        EventType? eventType = null,
        string? agent = null,
        EventSeverity? severity = null)
    {
        lock (_lock)
        {
            var query = _buffer.AsEnumerable();

            if (eventType.HasValue)
            {
                query = query.Where(e => e.Type == eventType.Value);
            }

            if (!string.IsNullOrWhiteSpace(agent))
            {
                query = query.Where(e =>
                    string.Equals(e.Agent, agent, StringComparison.OrdinalIgnoreCase));
            }

            if (severity.HasValue)
            {
                query = query.Where(e => e.Severity == severity.Value);
            }

            return query
                .OrderByDescending(e => e.Timestamp)
                .Take(limit)
                .ToList();
        }
    }

    public void Subscribe(Action<ActivityEvent> callback)
    {
        lock (_lock)
        {
            if (_subscribers.Count < MaxSubscribers)
            {
                _subscribers.Add(callback);
            }
        }
    }

    public void Unsubscribe(Action<ActivityEvent> callback)
    {
        lock (_lock)
        {
            _subscribers.Remove(callback);
        }
    }

    public Dictionary<string, object> GetStats()
    {
        lock (_lock)
        {
            var byType = Enum.GetValues<EventType>()
                .ToDictionary(
                    t => t.ToString(),
                    t => (object)_buffer.Count(e => e.Type == t));

            var bySeverity = Enum.GetValues<EventSeverity>()
                .ToDictionary(
                    s => s.ToString(),
                    s => (object)_buffer.Count(e => e.Severity == s));

            var recentErrors = _buffer
                .Where(e => e.Severity == EventSeverity.Error ||
                            e.Severity == EventSeverity.Warn)
                .OrderByDescending(e => e.Timestamp)
                .Take(5)
                .Select(e => new Dictionary<string, object?>
                {
                    ["type"] = e.Type.ToString(),
                    ["agent"] = e.Agent,
                    ["message"] = e.Message,
                    ["timestamp"] = e.Timestamp,
                    ["severity"] = e.Severity.ToString()
                })
                .ToList<object>();

            return new Dictionary<string, object>
            {
                ["by_type"] = byType,
                ["by_severity"] = bySeverity,
                ["recent_errors"] = recentErrors,
                ["total_events"] = _buffer.Count
            };
        }
    }

    public string Summary24H()
    {
        Dictionary<string, object> stats;
        lock (_lock)
        {
            var cutoff = DateTime.UtcNow.AddHours(-24);

            var recent = _buffer
                .Where(e => e.Timestamp >= cutoff)
                .ToList();

            var typeCounts = Enum.GetValues<EventType>()
                .ToDictionary(t => t.ToString(), t => recent.Count(e => e.Type == t));

            var severityCounts = Enum.GetValues<EventSeverity>()
                .ToDictionary(s => s.ToString(), s => recent.Count(e => e.Severity == s));

            stats = new Dictionary<string, object>
            {
                ["total"] = recent.Count,
                ["by_type"] = typeCounts,
                ["by_severity"] = severityCounts
            };
        }

        var total = (int)stats["total"];
        var lines = new List<string>
        {
            $"=== ActivityFeed 24H Summary ===",
            $"Total Events: {total}"
        };

        lines.Add("By Type:");
        var byType = (Dictionary<string, int>)stats["by_type"];
        foreach (var (key, value) in byType.OrderByDescending(kv => kv.Value))
        {
            lines.Add($"  {key}: {value}");
        }

        lines.Add("By Severity:");
        var bySeverity = (Dictionary<string, int>)stats["by_severity"];
        foreach (var (key, value) in bySeverity.OrderByDescending(kv => kv.Value))
        {
            lines.Add($"  {key}: {value}");
        }

        return string.Join(Environment.NewLine, lines);
    }
}
