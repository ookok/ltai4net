using System.Collections.Concurrent;
using LTAI.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace LTAI.AI;

/// <summary>
/// Circuit breaker and health scoring for LLM providers.
/// Tracks consecutive failures, cooldown periods, and provider health scores.
/// Optionally persists state to SQLite for cross-restart durability.
/// </summary>
public sealed class CircuitBreakerManager
{
    private readonly ConcurrentDictionary<string, int> _providerFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _providerCooldowns = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ProviderStats> _providerStats = new(StringComparer.OrdinalIgnoreCase);
    private readonly CircuitBreakerStore? _breakerStore;
    private readonly ILogger _logger;
    private readonly int _maxFailuresBeforeCooldown;
    private readonly TimeSpan _cooldownDuration;

    public CircuitBreakerManager(
        int maxFailuresBeforeCooldown,
        TimeSpan cooldownDuration,
        CircuitBreakerStore? breakerStore = null,
        ILogger? logger = null)
    {
        _maxFailuresBeforeCooldown = maxFailuresBeforeCooldown;
        _cooldownDuration = cooldownDuration;
        _breakerStore = breakerStore;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    /// <summary>Restore circuit breaker state from persistent store (lazy, fire-and-forget).</summary>
    public Lazy<Task> CreateLoadTask() => new(async () =>
    {
        if (_breakerStore == null) return;
        try
        {
            var all = await _breakerStore.LoadAllAsync().ConfigureAwait(false);
            var now = DateTime.UtcNow;
            foreach (var (provider, (failures, cooldownUntil)) in all)
            {
                if (failures > 0)
                    _providerFailures[provider] = failures;
                if (cooldownUntil.HasValue && cooldownUntil.Value > now)
                    _providerCooldowns[provider] = cooldownUntil.Value;
            }
        }
        catch { /* best-effort; in-memory fallback is still functional */ }
    });

    public void ClearProvider(string provider)
    {
        _providerFailures.TryRemove(provider, out _);
        _providerCooldowns.TryRemove(provider, out _);
    }

    public bool IsInCooldown(string provider)
    {
        return _providerCooldowns.TryGetValue(provider, out var until) && until > DateTime.UtcNow;
    }

    public void SetCooldown(string provider, DateTime until)
    {
        _providerCooldowns[provider] = until;
    }

    public void SetPermanentBan(string provider)
    {
        _providerCooldowns[provider] = DateTime.MaxValue;
    }

    public void RecordFailure(string provider)
    {
        var count = _providerFailures.AddOrUpdate(provider, 1, (_, c) => c + 1);
        var stats = _providerStats.GetOrAdd(provider, static _ => new ProviderStats());
        Interlocked.Increment(ref stats.FailedCalls);
        DateTime? until = null;
        if (count >= _maxFailuresBeforeCooldown)
        {
            until = DateTime.UtcNow + _cooldownDuration;
            _providerCooldowns[provider] = until.Value;
            _logger.LogWarning("Provider '{P}' failed {Count} times — cooling down until {Until}",
                provider, count, until);
        }
        if (_breakerStore != null)
            _ = _breakerStore.SaveAsync(provider, count, until);
    }

    public void RecordSuccess(string provider)
    {
        _providerFailures.TryRemove(provider, out _);
        _providerCooldowns.TryRemove(provider, out _);
        if (_breakerStore != null)
            _ = _breakerStore.ClearAsync(provider);
        var stats = _providerStats.GetOrAdd(provider, static _ => new ProviderStats());
        Interlocked.Increment(ref stats.SuccessfulCalls);
    }

    public double CalcHealthScore(string provider)
    {
        var stats = _providerStats.GetOrAdd(provider, static _ => new ProviderStats());
        var successRate = stats.TotalAttempts > 0
            ? (double)stats.SuccessfulCalls / stats.TotalAttempts
            : 0.8;
        var notInCooldown = IsInCooldown(provider) ? 0.0 : 1.0;
        return successRate * 0.6 + notInCooldown * 0.4;
    }

    internal static long ReadEnvInt(string key, int fallback)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(key), out var v) ? v : fallback;
    }

    private sealed record ProviderStats
    {
        public long SuccessfulCalls;
        public long FailedCalls;
        public long TotalAttempts => SuccessfulCalls + FailedCalls;
    }
}
