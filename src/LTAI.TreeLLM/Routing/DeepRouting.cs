using System.Collections.Concurrent;
using System.Text.Json;
using LTAI.TreeLLM.Models;
using Microsoft.Extensions.Logging;

using LTAI.Core.System;

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
    private readonly double _alpha = 0.2;
    private int _predictions;

    public void Record(string provider, double latencyMs)
    {
        var profile = _profiles.GetOrAdd(provider, _ => new LatencyProfile());
        profile.EMAMean = profile.EMAMean * (1 - _alpha) + latencyMs * _alpha;
        profile.EMAVariance = profile.EMAVariance * (1 - _alpha) + Math.Pow(latencyMs - profile.EMAMean, 2) * _alpha;
        profile.Samples++;
        profile.LastSample = DateTime.UtcNow;
    }

    public (double predictedMs, bool viable) Predict(string provider, double complexity = 0.5, int hour = -1, double timeoutMs = 120000)
    {
        if (hour < 0) hour = DateTime.UtcNow.Hour;
        if (!_profiles.TryGetValue(provider, out var p)) return (2000, true);

        var baseMs = p.EMAMean;
        var complexityFactor = 0.4 + complexity * 1.6;
        var hourFactor = (hour >= 9 && hour <= 11) || (hour >= 14 && hour <= 17) || (hour >= 20 && hour <= 21) ? 1.35 : 1.0;
        var predicted = baseMs * complexityFactor * hourFactor;
        var viable = predicted < timeoutMs * 0.9;

        Interlocked.Increment(ref _predictions);
        return (predicted, viable);
    }

    public double Predict(string provider) =>
        _profiles.TryGetValue(provider, out var p) ? p.EMAMean : 500;

    public int SmartTimeout(string provider, double complexity = 0.5, int min = 5000, int max = 120000)
    {
        var (predicted, _) = Predict(provider, complexity);
        return (int)Math.Clamp(predicted * 1.5, min, max);
    }

    public bool ShouldRetry(string provider, double elapsedMs)
    {
        if (!_profiles.TryGetValue(provider, out var p)) return true;
        return elapsedMs < p.EMAMean * 0.5;
    }

    public double PredictP95(string provider)
    {
        if (!_profiles.TryGetValue(provider, out var p)) return 1000;
        return p.EMAMean + 1.645 * Math.Sqrt(Math.Max(p.EMAVariance, 1));
    }

    public bool IsLatencyAcceptable(string provider, double thresholdMs = 2000) =>
        PredictP95(provider) <= thresholdMs;

    public IReadOnlyList<string> GetSlowProviders(double thresholdMs = 1500) =>
        _profiles.Where(kvp => kvp.Value.EMAMean > thresholdMs && kvp.Value.Samples > 10)
            .Select(kvp => kvp.Key).ToList().AsReadOnly();

    public Dictionary<string, object> Stats() => new()
    {
        ["predictions"] = _predictions,
        ["providers"] = _profiles.Count,
        ["slow_providers"] = GetSlowProviders()
    };
}

internal sealed class LatencyProfile
{
    public double EMAMean = 2000;
    public double EMAVariance = 100000;
    public int Samples;
    public DateTime LastSample = DateTime.UtcNow;
}

public sealed class CompetitiveEliminator
{
    private readonly ILogger<CompetitiveEliminator> _logger;
    private readonly ConcurrentDictionary<string, ModelRanking> _rankings = new();
    private readonly object _lock = new();
    private const double ELO_INITIAL = 1200;
    private const double ELO_SCALE = 400;
    private const double ELO_K_EARLY = 32;
    private const double ELO_K_STABLE = 16;
    private const int STREAK_THRESHOLD = 5;
    private const int MATCHES_TO_ESTABLISH = 10;
    private const double QUALITY_EMA_ALPHA = 0.3;
    private const double COOLDOWN_HOURS = 48;

    public CompetitiveEliminator(ILogger<CompetitiveEliminator> logger) => _logger = logger;

    public ModelRanking GetOrCreate(string provider)
    {
        return _rankings.GetOrAdd(provider, _ => new ModelRanking
        {
            Provider = provider,
            EloRating = ELO_INITIAL,
            Tier = ModelTierRank.Mid
        });
    }

    public ModelRanking? RecordMatch(string provider, bool success, double latencyMs, double costYuan,
        int tokens = 0, double qualityScore = 0.5, double? safetyScore = null, string? opponent = null)
    {
        var ranking = GetOrCreate(provider);
        if (ranking.IsEliminated)
        {
            if (ranking.CanRequalify)
                ranking.Tier = ModelTierRank.Flash;
            else
                return ranking;
        }

        ranking.Matches++;

        if (success)
        {
            ranking.Wins++;
            ranking.WinStreak++;
            ranking.LoseStreak = Math.Max(0, ranking.LoseStreak - 1);
        }
        else
        {
            ranking.Losses++;
            ranking.LoseStreak++;
            ranking.WinStreak = Math.Max(0, ranking.WinStreak - 1);
        }

        ranking.EmAQuality = ranking.EmAQuality * (1 - QUALITY_EMA_ALPHA) + qualityScore * QUALITY_EMA_ALPHA;
        if (safetyScore.HasValue)
            ranking.EmASafety = ranking.EmASafety * (1 - QUALITY_EMA_ALPHA) + safetyScore.Value * QUALITY_EMA_ALPHA;

        ranking.AvgLatencyMs = ranking.AvgLatencyMs * 0.8 + latencyMs * 0.2;
        ranking.AvgCostYuan = ranking.AvgCostYuan * 0.8 + costYuan * 0.2;
        ranking.LastMatch = DateTime.UtcNow;

        _UpdateElo(ranking, success, opponent);
        _CheckTierChange(ranking);

        return ranking;
    }

    private void _UpdateElo(ModelRanking ranking, bool success, string? opponent)
    {
        double avgOpponentElo = ELO_INITIAL;
        if (opponent != null && _rankings.TryGetValue(opponent, out var opp))
            avgOpponentElo = opp.EloRating;

        var expected = 1.0 / (1.0 + Math.Pow(10, (avgOpponentElo - ranking.EloRating) / ELO_SCALE));
        var actual = success ? 1.0 : 0.0;
        var k = ranking.Matches < MATCHES_TO_ESTABLISH ? ELO_K_EARLY : ELO_K_STABLE;

        ranking.EloRating += k * (actual - expected);
    }

    private void _CheckTierChange(ModelRanking ranking)
    {
        if (ranking.WinStreak >= STREAK_THRESHOLD)
        {
            ranking.Tier = ranking.Tier switch
            {
                ModelTierRank.Eliminated => ModelTierRank.Flash,
                ModelTierRank.Flash => ModelTierRank.Mid,
                ModelTierRank.Mid => ModelTierRank.Pro,
                _ => ranking.Tier
            };
            _logger.LogInformation("{Provider} promoted to {Tier} (streak:{Streak})", ranking.Provider, ranking.Tier, ranking.WinStreak);
            ranking.WinStreak = 0;
        }
        else if (ranking.LoseStreak >= STREAK_THRESHOLD)
        {
            ranking.Tier = ranking.Tier switch
            {
                ModelTierRank.Pro => ModelTierRank.Mid,
                ModelTierRank.Mid => ModelTierRank.Flash,
                ModelTierRank.Flash => ModelTierRank.Eliminated,
                _ => ranking.Tier
            };
            if (ranking.Tier == ModelTierRank.Eliminated)
                ranking.EliminatedAt = DateTime.UtcNow;
            _logger.LogWarning("{Provider} demoted to {Tier} (streak:{Streak})", ranking.Provider, ranking.Tier, ranking.LoseStreak);
            ranking.LoseStreak = 0;
        }

        if (ranking.IsEstablished && ranking.WinStreak == 0 && ranking.LoseStreak == 0)
        {
            if (ranking.EloRating >= 1400 && ranking.Tier != ModelTierRank.Pro)
            {
                ranking.Tier = ModelTierRank.Pro;
                _logger.LogInformation("{Provider} Elo-promoted to Pro ({Elo:F0})", ranking.Provider, ranking.EloRating);
            }
            else if (ranking.EloRating >= 1150 && ranking.Tier == ModelTierRank.Flash)
            {
                ranking.Tier = ModelTierRank.Mid;
                _logger.LogInformation("{Provider} Elo-promoted to Mid ({Elo:F0})", ranking.Provider, ranking.EloRating);
            }
            else if (ranking.EloRating < 900 && ranking.Tier == ModelTierRank.Flash)
            {
                ranking.Tier = ModelTierRank.Eliminated;
                ranking.EliminatedAt = DateTime.UtcNow;
                _logger.LogWarning("{Provider} Elo-eliminated ({Elo:F0})", ranking.Provider, ranking.EloRating);
            }
        }
    }

    public Dictionary<string, double> GetTierModifier(string provider)
    {
        var r = GetOrCreate(provider);
        return r.Tier switch
        {
            ModelTierRank.Pro => new Dictionary<string, double> { ["quality"] = 1.15, ["capability"] = 1.10 },
            ModelTierRank.Flash => new Dictionary<string, double> { ["cost"] = 1.30 },
            ModelTierRank.Eliminated => new Dictionary<string, double> { ["quality"] = 0.0, ["latency"] = 0.0, ["cost"] = 0.0, ["capability"] = 0.0 },
            _ => new Dictionary<string, double>()
        };
    }

    public bool IsViable(string provider)
    {
        var r = GetOrCreate(provider);
        return !r.IsEliminated || r.CanRequalify;
    }

    public List<(string Provider, double Score)> ListwiseRank(IReadOnlyList<string> providers, double temperature = 0.5)
    {
        var eligible = providers.Where(IsViable).Select(p => GetOrCreate(p)).ToList();
        var scores = eligible.Select(r => (
            Provider: r.Provider,
            Score: Math.Exp(r.EloRating / (ELO_SCALE * temperature))
        )).ToList();
        var total = scores.Sum(s => s.Score);
        return scores.Select(s => (
            Provider: s.Provider,
            Score: s.Score / Math.Max(total, 0.001)
        )).OrderByDescending(s => s.Score).ToList();
    }

    public void TransferKnowledge(string winner, string loser, double ratio = 0.3)
    {
        var w = GetOrCreate(winner);
        var l = GetOrCreate(loser);
        var eloGap = Math.Abs(w.EloRating - l.EloRating);
        w.EloRating -= eloGap * ratio * 0.05;
        l.EloRating += eloGap * ratio * 0.05;
        l.EmAQuality += (w.EmAQuality - l.EmAQuality) * ratio;
    }

    public void EvolveCollective(double ratio = 0.25)
    {
        var pro = _rankings.Values.Where(r => r.Tier == ModelTierRank.Pro).OrderByDescending(r => r.EloRating).ToList();
        var flash = _rankings.Values.Where(r => r.Tier == ModelTierRank.Flash || r.Tier == ModelTierRank.Eliminated)
            .OrderBy(r => r.EloRating).ToList();
        var pairs = Math.Min(pro.Count, flash.Count);
        for (int i = 0; i < pairs; i++)
            TransferKnowledge(pro[i].Provider, flash[i].Provider, ratio);
        _logger.LogInformation("Collective evolved: {Count} pairs", pairs);
    }

    public List<Dictionary<string, object>> GetLeaderboard() =>
        _rankings.Values.OrderByDescending(r => r.EloRating).Select(r => new Dictionary<string, object>
        {
            ["provider"] = r.Provider,
            ["elo"] = Math.Round(r.EloRating, 0),
            ["tier"] = r.Tier.ToString(),
            ["matches"] = r.Matches,
            ["win_rate"] = Math.Round(r.WinRate, 3),
            ["streak"] = r.WinStreak > 0 ? $"+{r.WinStreak}" : r.LoseStreak > 0 ? $"-{r.LoseStreak}" : "0",
            ["quality"] = Math.Round(r.EmAQuality, 3),
            ["avg_latency_ms"] = Math.Round(r.AvgLatencyMs, 0)
        }).ToList();

    public void ForcePromote(string provider)
    {
        var r = GetOrCreate(provider);
        r.Tier = r.Tier switch
        {
            ModelTierRank.Eliminated => ModelTierRank.Flash,
            ModelTierRank.Flash => ModelTierRank.Mid,
            ModelTierRank.Mid => ModelTierRank.Pro,
            _ => r.Tier
        };
    }

    public void ForceDemote(string provider)
    {
        var r = GetOrCreate(provider);
        r.Tier = r.Tier switch
        {
            ModelTierRank.Pro => ModelTierRank.Mid,
            ModelTierRank.Mid => ModelTierRank.Flash,
            _ => ModelTierRank.Eliminated
        };
        if (r.Tier == ModelTierRank.Eliminated)
            r.EliminatedAt = DateTime.UtcNow;
    }

    public Dictionary<string, object> Stats() => new()
    {
        ["total"] = _rankings.Count,
        ["pro"] = _rankings.Values.Count(r => r.Tier == ModelTierRank.Pro),
        ["mid"] = _rankings.Values.Count(r => r.Tier == ModelTierRank.Mid),
        ["flash"] = _rankings.Values.Count(r => r.Tier == ModelTierRank.Flash),
        ["eliminated"] = _rankings.Values.Count(r => r.Tier == ModelTierRank.Eliminated)
    };

    public void Save(string path)
    {
        lock (_lock)
        {
            var state = _rankings.Values.Select(r => new
            {
                r.Provider, r.EloRating, Tier = r.Tier.ToString(), r.Matches, r.Wins, r.Losses,
                r.WinStreak, r.LoseStreak, r.EmAQuality, r.EmASafety, r.AvgLatencyMs, r.AvgCostYuan,
                LastMatch = r.LastMatch.ToString("O"), EliminatedAt = r.EliminatedAt?.ToString("O")
            }).ToList();
            File.WriteAllText(path, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public void Load(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            var state = JsonSerializer.Deserialize<List<JsonElement>>(File.ReadAllText(path));
            if (state == null) return;
            lock (_lock)
            {
                foreach (var item in state)
                {
                    var provider = item.GetProperty("Provider").GetString()!;
                    var r = GetOrCreate(provider);
                    r.EloRating = item.GetProperty("EloRating").GetDouble();
                    r.Tier = Enum.TryParse<ModelTierRank>(item.GetProperty("Tier").GetString(), out var t) ? t : ModelTierRank.Mid;
                    r.Matches = item.GetProperty("Matches").GetInt32();
                    r.Wins = item.GetProperty("Wins").GetInt32();
                    r.Losses = item.GetProperty("Losses").GetInt32();
                    r.EmAQuality = item.GetProperty("EmAQuality").GetDouble();
                    if (item.TryGetProperty("EmASafety", out var s)) r.EmASafety = s.GetDouble();
                    r.AvgLatencyMs = item.GetProperty("AvgLatencyMs").GetDouble();
                    r.AvgCostYuan = item.TryGetProperty("AvgCostYuan", out var c) ? c.GetDouble() : 0;
                    r.LastMatch = DateTime.Parse(item.GetProperty("LastMatch").GetString()!);
                    if (item.TryGetProperty("EliminatedAt", out var ea) && ea.ValueKind != JsonValueKind.Null)
                        r.EliminatedAt = DateTime.Parse(ea.GetString()!);
                }
            }
        }
        catch { /* non-fatal */ }
    }
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

    public (string intent, double confidence) Classify(string query)
    {
        if (query.Length < 10) return ("chat", 0.8);
        if (query.Contains("```") || query.Contains("def ") || query.Contains("function ")) return ("code", 0.95);
        if (query.StartsWith("find ") || query.StartsWith("search ")) return ("search", 0.9);

        var intent = ClassificationRegistry.Intent.Classify(query);
        _patternCounts.AddOrUpdate(intent, 1, (_, v) => v + 1);

        return (intent, 0.7);
    }

    public Dictionary<string, int> GetStats() => new(_patternCounts);
}
