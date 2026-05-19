using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LTAI.TreeLLM.Intelligence;

public sealed class WarmStartAccel
{
    private static readonly Lazy<WarmStartAccel> _instanceLazy = new(() => new WarmStartAccel());
    public static WarmStartAccel Instance => _instanceLazy.Value;

    private bool _warmed;
    private long _warmupTimeMs;
    private int _providersWarmed;
    private readonly ILogger<WarmStartAccel>? _logger;

    public bool IsWarmed => _warmed;

    public WarmStartAccel(ILogger<WarmStartAccel>? logger = null)
    {
        _logger = logger;
    }

    public async Task<int> Warmup(
        List<(string Provider, double AvgLatencyMs)> providers,
        List<string> freeModels,
        Func<string, string, string, Task<string?>>? chatFn = null,
        int topN = 5)
    {
        if (_warmed) return _providersWarmed;

        var sw = System.Diagnostics.Stopwatch.StartNew();

        var sorted = providers.OrderBy(p => p.AvgLatencyMs).Take(topN).ToList();
        var pingTasks = sorted.Select(async p =>
        {
            try
            {
                if (chatFn != null)
                {
                    var result = await chatFn(p.Provider, "ping", "");
                    return result != null ? p.Provider : null;
                }
                return p.Provider;
            }
            catch
            {
                return null;
            }
        });

        var pinged = (await Task.WhenAll(pingTasks)).Where(p => p != null).Select(p => p!).ToList();
        var warmupCandidates = pinged.Take(3).ToList();

        var warmupTasks = warmupCandidates.Select(async provider =>
        {
            try
            {
                if (chatFn != null)
                {
                    await chatFn(provider, "Hi", "");
                    return true;
                }
                return false;
            }
            catch
            {
                _logger?.LogWarning("Warmup failed for provider {Provider}", provider);
                return false;
            }
        });

        var results = await Task.WhenAll(warmupTasks);
        _providersWarmed = results.Count(r => r);

        sw.Stop();
        _warmupTimeMs = sw.ElapsedMilliseconds;
        _warmed = true;

        _logger?.LogInformation(
            "WarmStart: warmed {Count} providers in {TimeMs}ms", _providersWarmed, _warmupTimeMs);

        return _providersWarmed;
    }
}

public sealed class AnticipatoryCompute
{
    private static readonly Lazy<AnticipatoryCompute> _instanceLazy = new(() => new AnticipatoryCompute());
    public static AnticipatoryCompute Instance => _instanceLazy.Value;

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, int>> _transitions = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, int>> _queryPairs = new();
    private int _hits;
    private int _total;
    private readonly ILogger<AnticipatoryCompute>? _logger;

    public double HitRate => _total == 0 ? 0.0 : (double)_hits / _total;

    public AnticipatoryCompute(ILogger<AnticipatoryCompute>? logger = null)
    {
        _logger = logger;
    }

    public List<Models.PredictedQuery> PredictNext(string sessionId, string currentQuery, string? currentState = null)
    {
        var results = new List<(string Query, double Probability, string Source)>();

        if (currentState != null && _transitions.TryGetValue(currentState, out var stateTransitions))
        {
            var totalTransitions = stateTransitions.Values.Sum();
            if (totalTransitions > 0)
            {
                foreach (var (nextState, count) in stateTransitions.OrderByDescending(kv => kv.Value).Take(3))
                {
                    var prob = (double)count / totalTransitions;
                    results.Add((nextState, prob, "markov"));
                }
            }
        }

        var queryKey = currentQuery.Length > 80 ? currentQuery[..80] : currentQuery;
        if (_queryPairs.TryGetValue(queryKey, out var pairTransitions))
        {
            var totalPairs = pairTransitions.Values.Sum();
            if (totalPairs > 0)
            {
                foreach (var (nextQuery, count) in pairTransitions.OrderByDescending(kv => kv.Value).Take(3))
                {
                    var prob = (double)count / totalPairs;
                    results.Add((nextQuery, prob, "query_pair"));
                }
            }
        }

        if (currentState != null)
        {
            results.Add(($"follow-up to: {currentState}", 0.25, "state_hint"));
        }

        var merged = results
            .GroupBy(r => r.Query)
            .Select(g => new Models.PredictedQuery
            {
                QueryText = g.Key,
                Probability = Math.Min(1.0, g.Sum(r => r.Probability)),
                Source = g.First().Source,
                ExpectedLatencySavingMs = g.First().Probability * 500
            })
            .OrderByDescending(p => p.Probability)
            .Take(3)
            .ToList();

        return merged;
    }

    public async Task<int> Prewarm(
        List<Models.PredictedQuery> predictions,
        Func<string, string, Task<string>>? chatFn = null)
    {
        var count = 0;
        foreach (var pred in predictions)
        {
            if (pred.Probability > 0.5 && chatFn != null)
            {
                try
                {
                    _ = await chatFn(pred.QueryText, "prewarm");
                    count++;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Prewarm failed for query: {Query}", pred.QueryText);
                }
            }
        }
        return count;
    }

    public void Learn(string currentQuery, string nextQuery, string? currentState = null, string? nextState = null)
    {
        if (currentState != null && nextState != null)
        {
            var trans = _transitions.GetOrAdd(currentState, _ => new ConcurrentDictionary<string, int>());
            trans.AddOrUpdate(nextState, 1, (_, v) => v + 1);
        }

        var queryKey = currentQuery.Length > 80 ? currentQuery[..80] : currentQuery;
        var pairs = _queryPairs.GetOrAdd(queryKey, _ => new ConcurrentDictionary<string, int>());
        pairs.AddOrUpdate(nextQuery, 1, (_, v) => v + 1);

        Interlocked.Increment(ref _total);
    }

    public void RecordHit(bool hit)
    {
        Interlocked.Increment(ref _total);
        if (hit) Interlocked.Increment(ref _hits);
    }
}
