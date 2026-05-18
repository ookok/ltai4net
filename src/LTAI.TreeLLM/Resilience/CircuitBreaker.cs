using System.Collections.Concurrent;
using LTAI.TreeLLM.Models;

namespace LTAI.TreeLLM.Resilience;

public sealed class CircuitBreaker
{
    private static readonly Lazy<CircuitBreaker> _instance = new(() => new CircuitBreaker());
    public static CircuitBreaker Instance => _instance.Value;

    private const int MaxProbes = 2;

    private readonly int _failureThreshold;
    private readonly double _cooldownSeconds;
    private readonly int _recoveryThreshold;

    private readonly ConcurrentDictionary<string, BreakerStats> _providers = new();
    private readonly ConcurrentDictionary<string, int> _probeCounts = new();

    public CircuitBreaker(int failureThreshold = 3, double cooldownSeconds = 30.0, int recoveryThreshold = 2)
    {
        _failureThreshold = failureThreshold;
        _cooldownSeconds = cooldownSeconds;
        _recoveryThreshold = recoveryThreshold;
    }

    public bool BeforeCall(string provider)
    {
        while (true)
        {
            var stats = _providers.GetOrAdd(provider, _ => new BreakerStats { Provider = provider });

            switch (stats.State)
            {
                case BreakerState.Closed:
                    return true;

                case BreakerState.Open:
                    if (stats.TrippedAt.HasValue &&
                        (DateTime.UtcNow - stats.TrippedAt.Value).TotalSeconds >= _cooldownSeconds)
                    {
                        var halfOpen = stats with
                        {
                            State = BreakerState.HalfOpen,
                            ConsecutiveFailures = 0
                        };
                        if (_providers.TryUpdate(provider, halfOpen, stats))
                            _probeCounts[provider] = 0;
                        continue;
                    }
                    _providers.AddOrUpdate(provider,
                        _ => throw new InvalidOperationException(),
                        (_, s) => { s.TotalBlocked++; return s; });
                    return false;

                case BreakerState.HalfOpen:
                    var probeCount = _probeCounts.AddOrUpdate(provider, 1, (_, v) => v + 1);
                    if (probeCount <= MaxProbes)
                        return true;
                    _providers.AddOrUpdate(provider,
                        _ => throw new InvalidOperationException(),
                        (_, s) => { s.TotalBlocked++; return s; });
                    return false;

                default:
                    return false;
            }
        }
    }

    public void OnSuccess(string provider, double latencyMs = 0)
    {
        _providers.AddOrUpdate(provider,
            _ => new BreakerStats { Provider = provider, SuccessCount = 1, LastSuccessTime = DateTime.UtcNow },
            (_, stats) =>
            {
                switch (stats.State)
                {
                    case BreakerState.HalfOpen:
                    {
                        var newConsecutive = stats.ConsecutiveFailures + 1;
                        if (newConsecutive >= _recoveryThreshold)
                        {
                            _probeCounts.TryRemove(provider, out var _removed);
                            return stats with
                            {
                                State = BreakerState.Closed,
                                ConsecutiveFailures = 0,
                                SuccessCount = stats.SuccessCount + 1,
                                LastSuccessTime = DateTime.UtcNow
                            };
                        }
                        return stats with
                        {
                            ConsecutiveFailures = newConsecutive,
                            SuccessCount = stats.SuccessCount + 1,
                            LastSuccessTime = DateTime.UtcNow
                        };
                    }
                    default:
                        stats.SuccessCount++;
                        stats.ConsecutiveFailures = 0;
                        stats.LastSuccessTime = DateTime.UtcNow;
                        return stats;
                }
            });
    }

    public void OnFailure(string provider, string error = "")
    {
        _providers.AddOrUpdate(provider,
            _ => new BreakerStats
            {
                Provider = provider,
                FailureCount = 1,
                ConsecutiveFailures = 1,
                LastFailureTime = DateTime.UtcNow
            },
            (_, stats) =>
            {
                switch (stats.State)
                {
                    case BreakerState.HalfOpen:
                        _probeCounts.TryRemove(provider, out var _removed);
                        return stats with
                        {
                            State = BreakerState.Open,
                            FailureCount = stats.FailureCount + 1,
                            ConsecutiveFailures = 1,
                            LastFailureTime = DateTime.UtcNow,
                            TrippedAt = DateTime.UtcNow,
                            TripCount = stats.TripCount + 1
                        };
                    case BreakerState.Closed:
                    {
                        var newConsecutive = stats.ConsecutiveFailures + 1;
                        if (newConsecutive >= _failureThreshold)
                        {
                            return stats with
                            {
                                State = BreakerState.Open,
                                FailureCount = stats.FailureCount + 1,
                                ConsecutiveFailures = newConsecutive,
                                LastFailureTime = DateTime.UtcNow,
                                TrippedAt = DateTime.UtcNow,
                                TripCount = stats.TripCount + 1
                            };
                        }
                        stats.FailureCount++;
                        stats.ConsecutiveFailures = newConsecutive;
                        stats.LastFailureTime = DateTime.UtcNow;
                        return stats;
                    }
                    default:
                        stats.FailureCount++;
                        stats.ConsecutiveFailures++;
                        stats.LastFailureTime = DateTime.UtcNow;
                        return stats;
                }
            });
    }

    public bool IsOpen(string provider)
    {
        return _providers.TryGetValue(provider, out var stats) && stats.State == BreakerState.Open;
    }

    public BreakerStats? GetStats(string provider)
    {
        return _providers.TryGetValue(provider, out var stats) ? stats : null;
    }

    public IReadOnlyDictionary<string, BreakerStats> AllStats()
    {
        return new Dictionary<string, BreakerStats>(_providers);
    }

    public IReadOnlyList<string> BlockedProviders()
    {
        return _providers
            .Where(kvp => kvp.Value.State == BreakerState.Open)
            .Select(kvp => kvp.Key)
            .ToList();
    }

    public IReadOnlyList<string> HealthyProviders(IEnumerable<string> providers)
    {
        return providers
            .Where(p => !IsOpen(p))
            .ToList();
    }

    public void ForceClose(string provider)
    {
        _providers.AddOrUpdate(provider,
            _ => new BreakerStats { Provider = provider, State = BreakerState.Closed },
            (_, stats) =>
            {
                _probeCounts.TryRemove(provider, out var _removed);
                return stats with
                {
                    State = BreakerState.Closed,
                    ConsecutiveFailures = 0
                };
            });
    }
}
