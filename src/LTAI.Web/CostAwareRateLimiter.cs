using System.Collections.Concurrent;

namespace LTAI.Web;

/// <summary>
/// Generic Cell Rate Algorithm (GCRA) cost-aware rate limiter.
/// From OpenFang's GCRA pattern: tracks per-IP token buckets,
/// cost-aware (models with higher prices consume more tokens),
/// with stale entry cleanup every 5 minutes.
/// </summary>
public sealed class CostAwareRateLimiter
{
    private readonly ConcurrentDictionary<string, GcraBucket> _buckets = new();
    private readonly Timer _cleanupTimer;
    private readonly double _emissionIntervalMs;
    private readonly double _burstTolerance;
    private readonly double _costMultiplier;

    public CostAwareRateLimiter(double requestsPerMinute = 60, double burstTolerance = 1.5, double costMultiplier = 1.0)
    {
        _emissionIntervalMs = 60000.0 / requestsPerMinute;
        _burstTolerance = burstTolerance;
        _costMultiplier = costMultiplier;
        _cleanupTimer = new Timer(CleanupStale, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// Check if request is allowed. Cost is model-dependent (expensive models = higher cost).
    /// Returns (allowed, retryAfterMs).
    /// </summary>
    public (bool Allowed, long RetryAfterMs) IsAllowed(string clientIp, double cost = 1.0)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var effectiveCost = cost * _costMultiplier;

        var bucket = _buckets.GetOrAdd(clientIp, _ => new GcraBucket
        {
            Tat = now - (long)(_emissionIntervalMs * _burstTolerance),
            LastSeen = now
        });

        lock (bucket)
        {
            bucket.LastSeen = now;

            var arrivalTime = Math.Max(now, bucket.Tat);
            var expectedTime = arrivalTime + (long)(_emissionIntervalMs * effectiveCost);

            if (arrivalTime - bucket.Tat > (long)(_emissionIntervalMs * _burstTolerance))
            {
                bucket.Tat = now;
                return (true, 0);
            }

            if (expectedTime - now <= 0)
            {
                bucket.Tat = expectedTime;
                return (true, 0);
            }

            return (false, expectedTime - now);
        }
    }

    private void CleanupStale(object? state)
    {
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 300_000; // 5 min
        foreach (var key in _buckets.Keys)
        {
            if (_buckets.TryGetValue(key, out var bucket) && bucket.LastSeen < cutoff)
                _buckets.TryRemove(key, out _);
        }
    }

    public Dictionary<string, object> GetStats()
    {
        var active = _buckets.Count;
        return new()
        {
            ["active_clients"] = active,
            ["emission_interval_ms"] = _emissionIntervalMs,
            ["burst_tolerance"] = _burstTolerance
        };
    }

    private sealed class GcraBucket
    {
        public long Tat;
        public long LastSeen;
    }
}
