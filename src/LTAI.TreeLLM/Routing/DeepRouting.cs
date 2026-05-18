using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LTAI.TreeLLM.Routing;

public sealed class BudgetRouter
{
    private readonly ILogger<BudgetRouter> _logger;
    private readonly ModelRegistry _registry;
    private double _dailyBudget = 10.00;
    private double _dailySpent;
    private readonly Dictionary<string, double> _allocations = new();
    private DateTime _resetTime = DateTime.UtcNow.Date.AddDays(1);

    public double Remaining => _dailyBudget - _dailySpent;

    public BudgetRouter(ILogger<BudgetRouter> logger, ModelRegistry registry, double dailyBudget = 10.00)
    {
        _logger = logger;
        _registry = registry;
        _dailyBudget = dailyBudget;
    }

    public string? SelectWithinBudget(string taskType, IReadOnlyList<string> candidates)
    {
        if (DateTime.UtcNow >= _resetTime)
        {
            _dailySpent = 0;
            _allocations.Clear();
            _resetTime = DateTime.UtcNow.Date.AddDays(1);
            _logger.LogInformation("Budget reset: ${Budget}/day", _dailyBudget);
        }

        var models = candidates
            .Select(c => _registry.Get(c))
            .Where(m => m != null)
            .Select(m => m!)
            .OrderByDescending(m => m.GetScore(taskType))
            .ToList();

        if (models.Count == 0) return null;

        if (_dailySpent >= _dailyBudget * 0.9)
        {
            var cheapest = models.Where(m => m.Tier == ModelTier.Flash).MinBy(m => m.CostPer1MTokens);
            if (cheapest != null)
            {
                _logger.LogWarning("Budget tight: ${Spent:F2}/${Budget:F2}, switching to flash", _dailySpent, _dailyBudget);
                return cheapest.Name;
            }
        }

        foreach (var model in models)
        {
            var estimatedCost = EstimateCost(model);
            var allocation = _allocations.GetValueOrDefault(model.Name);

            if (allocation + estimatedCost <= _dailyBudget * (model.Tier == ModelTier.Pro ? 0.6 : 0.3))
            {
                _allocations[model.Name] = allocation + estimatedCost;
                return model.Name;
            }
        }

        return models.First().Name;
    }

    public void RecordUsage(string modelName, int inputTokens, int outputTokens)
    {
        var profile = _registry.Get(modelName);
        if (profile == null) return;

        var cost = (inputTokens * profile.CostPer1MTokens + outputTokens * profile.CostPer1MTokens) / 1_000_000.0;
        _dailySpent += cost;
        _registry.RecordTokens(modelName, inputTokens, outputTokens);
    }

    public BudgetStatus GetStatus() => new()
    {
        DailyBudget = _dailyBudget,
        DailySpent = _dailySpent,
        Remaining = Remaining,
        Allocations = new Dictionary<string, double>(_allocations),
        ResetAt = _resetTime
    };

    private static double EstimateCost(ModelProfile model) => model.CostPer1MTokens * 0.005;
}

public sealed class BudgetStatus
{
    public double DailyBudget { get; init; }
    public double DailySpent { get; init; }
    public double Remaining { get; init; }
    public Dictionary<string, double> Allocations { get; init; } = new();
    public DateTime ResetAt { get; init; }
}

public sealed class LatencyOracle
{
    private readonly ConcurrentDictionary<string, LatencyProfile> _profiles = new();
    private readonly double _decay = 0.9;

    public void Record(string provider, double latencyMs)
    {
        var profile = _profiles.GetOrAdd(provider, _ => new LatencyProfile());
        profile.EMAMean = profile.EMAMean * _decay + latencyMs * (1.0 - _decay);
        profile.EMAVariance = profile.EMAVariance * _decay + Math.Pow(latencyMs - profile.EMAMean, 2) * (1.0 - _decay);
        profile.Samples++;
        profile.LastSample = DateTime.UtcNow;
    }

    public double Predict(string provider) =>
        _profiles.TryGetValue(provider, out var p) ? p.EMAMean : 500;

    public double PredictP95(string provider)
    {
        if (!_profiles.TryGetValue(provider, out var p)) return 1000;
        return p.EMAMean + 1.645 * Math.Sqrt(p.EMAVariance);
    }

    public bool IsLatencyAcceptable(string provider, double thresholdMs = 2000) =>
        PredictP95(provider) <= thresholdMs;

    public IReadOnlyList<string> GetSlowProviders(double thresholdMs = 1500) =>
        _profiles.Where(kvp => kvp.Value.EMAMean > thresholdMs && kvp.Value.Samples > 10)
            .Select(kvp => kvp.Key).ToList().AsReadOnly();
}

internal sealed class LatencyProfile
{
    public double EMAMean = 500;
    public double EMAVariance = 25000;
    public int Samples;
    public DateTime LastSample = DateTime.UtcNow;
}

public sealed class CompetitiveEliminator
{
    private readonly ILogger<CompetitiveEliminator> _logger;
    private readonly ConcurrentDictionary<string, TournamentState> _tournaments = new();
    private readonly int _matchesPerRound = 20;
    private readonly double _eliminationThreshold = 0.55;

    public CompetitiveEliminator(ILogger<CompetitiveEliminator> logger) => _logger = logger;

    public (string winner, string loser)? EvaluateRound(string taskType, string modelA, string modelB)
    {
        var pair = string.Compare(modelA, modelB) < 0 ? $"{modelA}_vs_{modelB}" : $"{modelB}_vs_{modelA}";
        var key = $"{taskType}_{pair}";
        var state = _tournaments.GetOrAdd(key, _ => new TournamentState
        {
            ProviderA = modelA,
            ProviderB = modelB,
            TaskType = taskType
        });

        state.Round++;
        if (state.Round >= _matchesPerRound)
        {
            var aRate = state.AWins / (double)Math.Max(1, state.AWins + state.BWins);
            if (aRate > _eliminationThreshold)
            {
                _logger.LogInformation("Elimination: {A} > {B} ({Rate:F2})", modelA, modelB, aRate);
                return (modelA, modelB);
            }
            if (1 - aRate > _eliminationThreshold)
            {
                _logger.LogInformation("Elimination: {B} > {A} ({Rate:F2})", modelB, modelA, 1 - aRate);
                return (modelB, modelA);
            }
            state.Round = 0;
            state.AWins = 0;
            state.BWins = 0;
        }

        return null;
    }

    public void RecordResult(string taskType, string modelA, string modelB, bool aWon)
    {
        var pair = string.Compare(modelA, modelB) < 0 ? $"{modelA}_vs_{modelB}" : $"{modelB}_vs_{modelA}";
        var key = $"{taskType}_{pair}";
        var state = _tournaments.GetOrAdd(key, _ => new TournamentState { ProviderA = modelA, ProviderB = modelB, TaskType = taskType });
        if (aWon) state.AWins++; else state.BWins++;
    }

    public IReadOnlyList<string> GetActiveTournaments() =>
        _tournaments.Where(kvp => kvp.Value.Round > 0).Select(kvp => kvp.Key).ToList().AsReadOnly();
}

internal sealed class TournamentState
{
    public string ProviderA { get; init; } = "";
    public string ProviderB { get; init; } = "";
    public string TaskType { get; init; } = "";
    public int Round;
    public int AWins;
    public int BWins;
}

public sealed class ContinuousBenchmark
{
    private readonly ILogger<ContinuousBenchmark> _logger;
    private readonly ConcurrentDictionary<string, BenchmarkResult> _results = new();
    private int _cycle;

    public ContinuousBenchmark(ILogger<ContinuousBenchmark> logger) => _logger = logger;

    public void Record(string provider, string taskType, bool success, double latencyMs, int tokens)
    {
        var key = $"{provider}_{taskType}";
        _results.AddOrUpdate(key,
            _ => new BenchmarkResult
            {
                Provider = provider, TaskType = taskType, SuccessCount = success ? 1 : 0,
                TotalCount = 1, TotalLatency = latencyMs, TotalTokens = tokens
            },
            (_, r) =>
            {
                if (success) r.SuccessCount++;
                r.TotalCount++;
                r.TotalLatency += latencyMs;
                r.TotalTokens += tokens;
                return r;
            });
    }

    public void Cycle()
    {
        _cycle++;
        if (_cycle % 50 == 0) _logger.LogInformation("Benchmark cycle {Cycle}: {Count} entries", _cycle, _results.Count);
    }

    public IReadOnlyList<BenchmarkResult> GetResults() => _results.Values.ToList().AsReadOnly();

    public BenchmarkSummary GetSummary()
    {
        var results = _results.Values.ToList();
        if (results.Count == 0) return new BenchmarkSummary();

        return new BenchmarkSummary
        {
            TotalCalls = results.Sum(r => r.TotalCount),
            AvgSuccessRate = results.Average(r => r.SuccessCount / (double)Math.Max(1, r.TotalCount)),
            AvgLatencyMs = results.Average(r => r.TotalLatency / Math.Max(1, r.TotalCount)),
            TotalTokens = results.Sum(r => r.TotalTokens),
            ProviderCount = results.Select(r => r.Provider).Distinct().Count()
        };
    }
}

public sealed class BenchmarkResult
{
    public string Provider { get; init; } = "";
    public string TaskType { get; init; } = "";
    public long SuccessCount { get; set; }
    public long TotalCount { get; set; }
    public double TotalLatency { get; set; }
    public long TotalTokens { get; set; }
}

public sealed class BenchmarkSummary
{
    public long TotalCalls { get; init; }
    public double AvgSuccessRate { get; init; }
    public double AvgLatencyMs { get; init; }
    public long TotalTokens { get; init; }
    public int ProviderCount { get; init; }
}

public sealed class QueryClassifier
{
    private readonly ConcurrentDictionary<string, int> _patternCounts = new();
    private static readonly Dictionary<string, string[]> IntentPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["code"] = new[] { "code", "function", "class", "bug", "error", "fix", "implement", "refactor", "test", "compile", "syntax", "import", "package" },
        ["reasoning"] = new[] { "why", "explain", "reason", "analyze", "compare", "contrast", "evaluate", "assess", "prove", "logic", "cause" },
        ["chat"] = new[] { "hello", "hi", "how are you", "thanks", "help", "what is", "tell me", "who", "when" },
        ["search"] = new[] { "find", "search", "lookup", "google", "where is", "locate" },
        ["long_context"] = new[] { "summarize", "summary", "document", "article", "report", "write", "draft", "essay" }
    };

    public (string intent, double confidence) Classify(string query)
    {
        var lower = query.ToLowerInvariant();
        var scores = new Dictionary<string, double>();

        foreach (var (intent, patterns) in IntentPatterns)
        {
            var matches = patterns.Count(p => lower.Contains(p));
            scores[intent] = (double)matches / patterns.Length;
        }

        var best = scores.OrderByDescending(kvp => kvp.Value).First();
        _patternCounts.AddOrUpdate(best.Key, 1, (_, v) => v + 1);

        if (query.Length < 10) return ("chat", 0.8);
        if (query.Contains("```") || query.Contains("def ") || query.Contains("function ")) return ("code", 0.95);
        if (query.StartsWith("find ") || query.StartsWith("search ")) return ("search", 0.9);

        return (best.Key, best.Value);
    }

    public Dictionary<string, int> GetStats() => new(_patternCounts);
}
