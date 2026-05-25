using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using LTAI.Agent.Models;

namespace LTAI.Agent.Routing;

public sealed class ElectionBus
{
    private readonly ILogger<ElectionBus>? _logger;
    private static readonly Lazy<ElectionBus> _instance = new(() => new ElectionBus());

    public static ElectionBus Instance => _instance.Value;

    private readonly ConcurrentDictionary<string, ElectionSnapshot> _snapshots = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private double _ttl = 30.0;
    private readonly double _minTtl = 5.0;
    private readonly double _maxTtl = 60.0;

    private event Action<IReadOnlyList<ProviderScore>>? OnRefreshed;

    public ElectionBus(ILogger<ElectionBus>? logger = null)
    {
        _logger = logger;
    }

    public async Task<List<ProviderScore>> GetScoresAsync(
        IReadOnlyList<string> providers,
        IReadOnlyList<string> freeModels,
        string taskType = "general",
        bool force = false,
        CancellationToken ct = default)
    {
        var cacheKey = BuildCacheKey(providers);

        if (!force && _snapshots.TryGetValue(cacheKey, out var snapshot))
        {
            var elapsed = (DateTime.UtcNow - snapshot.Timestamp).TotalSeconds;
            if (elapsed < _ttl)
            {
                _logger?.LogDebug("Election cache hit for key {Key}, age {Age:F1}s", cacheKey, elapsed);
                return snapshot.Scores;
            }
        }

        await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Double-check cache after acquiring lock
            if (!force && _snapshots.TryGetValue(cacheKey, out snapshot))
            {
                var elapsed = (DateTime.UtcNow - snapshot.Timestamp).TotalSeconds;
                if (elapsed < _ttl)
                    return snapshot.Scores;
            }

            var election = HolisticElection.Instance;
            var scores = await election.ScoreProvidersAsync(providers, freeModels, taskType, force, ct).ConfigureAwait(false);

            var healthRatio = scores.Count > 0
                ? scores.Average(s => s.Scores.GetValueOrDefault("health_factor", 1.0))
                : 1.0;

            var newSnapshot = new ElectionSnapshot
            {
                Scores = scores,
                Timestamp = DateTime.UtcNow,
                CandidatesHash = cacheKey
            };

            _snapshots[cacheKey] = newSnapshot;

            AdaptTtl(healthRatio);

            _logger?.LogInformation("Election refreshed for key {Key}, health ratio {Health:F2}, TTL {Ttl:F1}s",
                cacheKey, healthRatio, _ttl);

            OnRefreshed?.Invoke(scores.AsReadOnly());

            return scores;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public void ForceRefresh()
    {
        _snapshots.Clear();
        _logger?.LogInformation("Election cache force-refreshed");
    }

    public void InvalidateProvider(string provider)
    {
        var keysToRemove = _snapshots.Keys
            .Where(k => k.Split(',', StringSplitOptions.TrimEntries)
                .Contains(provider, StringComparer.OrdinalIgnoreCase))
            .ToList();

        foreach (var key in keysToRemove)
        {
            _snapshots.TryRemove(key, out _);
        }

        _logger?.LogDebug("Invalidated {Count} cache entries for provider {Provider}", keysToRemove.Count, provider);
    }

    public IReadOnlyList<string> GetTop(
        IReadOnlyList<string> providers,
        IReadOnlyList<string> freeModels,
        int n = 3)
    {
        var cacheKey = BuildCacheKey(providers);

        if (!_snapshots.TryGetValue(cacheKey, out var snapshot) || snapshot.Scores.Count == 0)
        {
            // Synchronous fallback: return first N providers
            return providers.Take(n).ToList().AsReadOnly();
        }

        return snapshot.Scores
            .Where(s => s.Alive)
            .OrderByDescending(s => s.Total)
            .Take(n)
            .Select(s => s.Provider)
            .ToList()
            .AsReadOnly();
    }

    public void Subscribe(Action<IReadOnlyList<ProviderScore>> callback)
    {
        OnRefreshed += callback;
    }

    private static string BuildCacheKey(IReadOnlyList<string> providers)
    {
        return string.Join(",", providers.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
    }

    private void AdaptTtl(double healthRatio)
    {
        if (healthRatio > 0.8)
        {
            _ttl = Math.Min(_maxTtl, _ttl * 1.2);
        }
        else if (healthRatio < 0.5)
        {
            _ttl = Math.Max(_minTtl, _ttl * 0.5);
        }
    }
}
