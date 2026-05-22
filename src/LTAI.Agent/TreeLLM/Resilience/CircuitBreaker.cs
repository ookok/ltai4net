using System.Collections.Concurrent;
using LTAI.Agent.Models;
using Microsoft.Extensions.DependencyInjection;

namespace LTAI.Agent.Resilience;

public sealed class CircuitBreaker
{
    private readonly ConcurrentDictionary<string, CircuitState> _providers = new();
    private readonly int _failureThreshold;
    private readonly double _cooldownSeconds;
    private readonly int _recoveryThreshold;
    private int _totalBlocked;

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
            var breaker = _providers.GetOrAdd(provider, _ => new CircuitState
            {
                Provider = provider,
                State = BreakerState.Closed
            });

            switch (breaker.State)
            {
                case BreakerState.Closed:
                    return true;

                case BreakerState.Open:
                    if (breaker.TrippedAt.HasValue &&
                        (DateTime.UtcNow - breaker.TrippedAt.Value).TotalSeconds >= _cooldownSeconds)
                    {
                        var halfOpen = new CircuitState
                        {
                            Provider = provider,
                            State = BreakerState.HalfOpen,
                            ConsecutiveFailures = 0,
                            ProbeCount = 0
                        };
                        if (_providers.TryUpdate(provider, halfOpen, breaker))
                            continue;
                    }
                    _totalBlocked++;
                    return false;

                case BreakerState.HalfOpen:
                    breaker.ProbeCount++;
                    if (breaker.ProbeCount <= 2)
                        return true;
                    _totalBlocked++;
                    return false;

                default:
                    return false;
            }
        }
    }

    public void OnSuccess(string provider, double latencyMs = 0)
    {
        _providers.AddOrUpdate(provider,
            _ => new CircuitState
            {
                Provider = provider,
                State = BreakerState.Closed,
                SuccessCount = 1,
                LastSuccessTime = DateTime.UtcNow
            },
            (_, state) =>
            {
                switch (state.State)
                {
                    case BreakerState.HalfOpen:
                    {
                        var newConsecutive = state.ConsecutiveFailures + 1;
                        if (newConsecutive >= _recoveryThreshold)
                        {
                            return new CircuitState
                            {
                                Provider = provider,
                                State = BreakerState.Closed,
                                ConsecutiveFailures = 0,
                                SuccessCount = state.SuccessCount + 1,
                                LastSuccessTime = DateTime.UtcNow
                            };
                        }
                        state.ConsecutiveFailures = newConsecutive;
                        state.SuccessCount++;
                        state.LastSuccessTime = DateTime.UtcNow;
                        return state;
                    }
                    default:
                        state.SuccessCount++;
                        state.ConsecutiveFailures = 0;
                        state.LastSuccessTime = DateTime.UtcNow;
                        return state;
                }
            });
    }

    public void OnFailure(string provider, string error = "")
    {
        _providers.AddOrUpdate(provider,
            _ => new CircuitState
            {
                Provider = provider,
                State = BreakerState.Closed,
                FailureCount = 1,
                ConsecutiveFailures = 1,
                LastFailureTime = DateTime.UtcNow
            },
            (_, state) =>
            {
                switch (state.State)
                {
                    case BreakerState.HalfOpen:
                        return new CircuitState
                        {
                            Provider = provider,
                            State = BreakerState.Open,
                            FailureCount = state.FailureCount + 1,
                            ConsecutiveFailures = 1,
                            LastFailureTime = DateTime.UtcNow,
                            TrippedAt = DateTime.UtcNow,
                            TripCount = state.TripCount + 1
                        };
                    case BreakerState.Closed:
                    {
                        var newConsecutive = state.ConsecutiveFailures + 1;
                        if (newConsecutive >= _failureThreshold)
                        {
                            return new CircuitState
                            {
                                Provider = provider,
                                State = BreakerState.Open,
                                FailureCount = state.FailureCount + 1,
                                ConsecutiveFailures = newConsecutive,
                                LastFailureTime = DateTime.UtcNow,
                                TrippedAt = DateTime.UtcNow,
                                TripCount = state.TripCount + 1
                            };
                        }
                        state.FailureCount++;
                        state.ConsecutiveFailures = newConsecutive;
                        state.LastFailureTime = DateTime.UtcNow;
                        return state;
                    }
                    default:
                        state.FailureCount++;
                        state.ConsecutiveFailures++;
                        state.LastFailureTime = DateTime.UtcNow;
                        return state;
                }
            });
    }

    public bool IsOpen(string provider)
    {
        return _providers.TryGetValue(provider, out var state) && state.State == BreakerState.Open;
    }

    public BreakerStats? GetStats(string provider)
    {
        if (!_providers.TryGetValue(provider, out var state))
            return null;

        return new BreakerStats
        {
            Provider = provider,
            State = state.State,
            FailureCount = state.FailureCount,
            SuccessCount = state.SuccessCount,
            ConsecutiveFailures = state.ConsecutiveFailures,
            TrippedAt = state.TrippedAt,
            LastFailureTime = state.LastFailureTime,
            LastSuccessTime = state.LastSuccessTime,
            TripCount = state.TripCount,
            TotalBlocked = _totalBlocked
        };
    }

    public IReadOnlyDictionary<string, BreakerStats> AllStats()
    {
        return _providers.ToDictionary(
            kvp => kvp.Key,
            kvp => new BreakerStats
            {
                Provider = kvp.Key,
                State = kvp.Value.State,
                FailureCount = kvp.Value.FailureCount,
                SuccessCount = kvp.Value.SuccessCount,
                ConsecutiveFailures = kvp.Value.ConsecutiveFailures,
                TrippedAt = kvp.Value.TrippedAt,
                LastFailureTime = kvp.Value.LastFailureTime,
                LastSuccessTime = kvp.Value.LastSuccessTime,
                TripCount = kvp.Value.TripCount,
                TotalBlocked = _totalBlocked
            });
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
            _ => new CircuitState { Provider = provider, State = BreakerState.Closed },
            (_, state) => new CircuitState
            {
                Provider = provider,
                State = BreakerState.Closed,
                ConsecutiveFailures = 0,
                FailureCount = state.FailureCount,
                SuccessCount = state.SuccessCount
            });
    }

    private sealed class CircuitState
    {
        public string Provider { get; init; } = string.Empty;
        public BreakerState State { get; init; }
        public int FailureCount { get; set; }
        public int SuccessCount { get; set; }
        public int ConsecutiveFailures { get; set; }
        public int ProbeCount { get; set; }
        public DateTime? TrippedAt { get; init; }
        public DateTime? LastFailureTime { get; set; }
        public DateTime? LastSuccessTime { get; set; }
        public int TripCount { get; init; }
    }
}

public static class CircuitBreakerServiceExtensions
{
    public static IServiceCollection AddLTAICircuitBreaker(this IServiceCollection services,
        int failureThreshold = 3, double cooldownSeconds = 30.0, int recoveryThreshold = 2)
    {
        services.AddSingleton(new CircuitBreaker(failureThreshold, cooldownSeconds, recoveryThreshold));
        return services;
    }
}
