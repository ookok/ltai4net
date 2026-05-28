using System.Collections.Concurrent;

namespace LTAI.Agent.Tools;

/// <summary>
/// Sliding-window rate limiter for tool execution (per tool type).
/// Prevents runaway shell/http/compose calls from overwhelming the system.
/// </summary>
public sealed class ToolRateLimiter
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<DateTime>> _callLogs = new();
    private readonly int _maxCallsPerWindow;
    private readonly TimeSpan _window;

    public ToolRateLimiter(int maxCallsPerWindow = 10, int windowSeconds = 60)
    {
        _maxCallsPerWindow = maxCallsPerWindow;
        _window = TimeSpan.FromSeconds(windowSeconds);
    }

    /// <summary>
    /// Check if a call is allowed for the given tool type.
    /// If allowed, the call is logged. Thread-safe.
    /// </summary>
    public bool AllowCall(string toolType)
    {
        var now = DateTime.UtcNow;
        var log = _callLogs.GetOrAdd(toolType, _ => new ConcurrentQueue<DateTime>());

        // Prune expired entries
        while (log.TryPeek(out var oldest) && (now - oldest) > _window)
        {
            log.TryDequeue(out _);
        }

        // Check limit
        if (log.Count >= _maxCallsPerWindow)
            return false;

        // Log this call
        log.Enqueue(now);
        return true;
    }

    /// <summary>
    /// Get current usage stats for all tool types.
    /// </summary>
    public Dictionary<string, object> GetStats()
    {
        var now = DateTime.UtcNow;
        var stats = new Dictionary<string, object>();

        foreach (var kvp in _callLogs)
        {
            // Prune while reading
            while (kvp.Value.TryPeek(out var oldest) && (now - oldest) > _window)
            {
                kvp.Value.TryDequeue(out _);
            }

            stats[kvp.Key] = new
            {
                current_count = kvp.Value.Count,
                max_allowed = _maxCallsPerWindow,
                window_seconds = _window.TotalSeconds
            };
        }

        return stats;
    }

    /// <summary>
    /// Reset rate limit counters for all tool types.
    /// </summary>
    public void Reset()
    {
        foreach (var kvp in _callLogs)
        {
            while (kvp.Value.TryDequeue(out _)) { }
        }
    }
}
