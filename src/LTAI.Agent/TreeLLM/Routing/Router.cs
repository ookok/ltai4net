using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using LTAI.Agent.Models;

namespace LTAI.Agent.Routing;

/// <summary>
/// Defines the contract for a routing strategy that selects providers and learns from outcomes.
/// </summary>
public interface IRoutingStrategy
{
    /// <summary>Selects the best provider for a given task.</summary>
    RoutingDecision? Select(string task, IReadOnlyList<RoutingCandidate> candidates, string taskType = "general");

    /// <summary>Records the outcome of a routing decision for learning.</summary>
    void Record(RoutingDecision decision, bool success, Dictionary<string, object?>? metadata = null);

    /// <summary>Returns current statistics for monitoring.</summary>
    IReadOnlyDictionary<string, object> Stats();
}

/// <summary>
/// EMA-based routing strategy that maintains weighted scores per task type and provider,
/// applying domain-specific capability bonuses and recency factors.
/// </summary>
public sealed class RouteLearner : IRoutingStrategy, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Capability bonuses mapped by task type to provider-specific modifiers.</summary>
    public static readonly Dictionary<string, Dictionary<string, double>> DOMAIN_CAPABILITY_BONUS = new()
    {
        ["code_engineering"] = new()
        {
            ["tool_call"] = 0.3,
            ["structured_output"] = 0.2
        },
        ["environmental_report"] = new(),
        ["data_analysis"] = new(),
        ["question"] = new()
        {
            ["cost_score"] = 0.4
        }
    };

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, RoutingWeight>> _weights = new();
    private int _sampleCounter;
    private readonly object _saveLock = new();
    private readonly string _persistPath;

    public RouteLearner(string? persistPath = null)
    {
        _persistPath = persistPath ?? Path.Combine("livingtree", "meta", "route_weights.json");
        Load();
    }

    public RoutingDecision? Select(string task, IReadOnlyList<RoutingCandidate> candidates, string taskType = "general")
    {
        if (candidates.Count == 0) return null;

        var typeWeights = _weights.GetOrAdd(taskType, _ => new ConcurrentDictionary<string, RoutingWeight>());
        var scored = new List<(RoutingCandidate Candidate, double Score)>();

        foreach (var c in candidates)
        {
            var w = typeWeights.GetOrAdd(c.Provider, _ => new RoutingWeight
            {
                TaskType = taskType,
                Provider = c.Provider,
                Weight = 1.0,
                SuccessRate = 0.5,
                SampleCount = 0
            });

            var recencyFactor = Math.Min(1.5, 1.0 + w.SampleCount * 0.05);
            var capabilityBonus = GetCapabilityBonus(taskType, c);
            var score = w.Weight * w.SuccessRate * recencyFactor + capabilityBonus;

            scored.Add((c, score));
        }

        var best = scored.OrderByDescending(x => x.Score).First();

        return new RoutingDecision
        {
            Provider = best.Candidate.Provider,
            Model = best.Candidate.Model,
            Strategy = nameof(RouteLearner),
            Score = best.Score,
            Scores = scored.ToDictionary(x => x.Candidate.Provider, x => x.Score),
            Timestamp = DateTime.UtcNow
        };
    }

    public void Record(RoutingDecision decision, bool success, Dictionary<string, object?>? metadata = null)
    {
        var taskType = decision.Metadata.TryGetValue("taskType", out var tt) ? tt?.ToString() ?? "general" : "general";
        var typeWeights = _weights.GetOrAdd(taskType, _ => new ConcurrentDictionary<string, RoutingWeight>());

        typeWeights.AddOrUpdate(decision.Provider,
            _ => new RoutingWeight
            {
                TaskType = taskType,
                Provider = decision.Provider,
                Weight = 1.0,
                SuccessRate = success ? 0.6 : 0.4,
                SampleCount = 1,
                LastUpdated = DateTime.UtcNow
            },
            (_, existing) =>
            {
                var oldRate = existing.SuccessRate;
                var newRate = 0.8 * oldRate + 0.2 * (success ? 1.0 : 0.0);
                existing.SuccessRate = newRate;
                existing.SampleCount++;
                var recency = Math.Min(1.5, 1.0 + existing.SampleCount * 0.05);
                existing.Weight *= newRate * recency;
                existing.LastUpdated = DateTime.UtcNow;
                return existing;
            });

        var counter = Interlocked.Increment(ref _sampleCounter);
        if (counter % 20 == 0)
            Save();
    }

    public IReadOnlyDictionary<string, object> Stats()
    {
        var stats = new Dictionary<string, object>
        {
            ["sampleCount"] = _sampleCounter,
            ["taskTypes"] = _weights.Count,
            ["totalProviders"] = _weights.Values.Sum(d => d.Count),
            ["strategy"] = nameof(RouteLearner)
        };
        return stats;
    }

    private double GetCapabilityBonus(string taskType, RoutingCandidate candidate)
    {
        var bonus = 0.0;
        if (!DOMAIN_CAPABILITY_BONUS.TryGetValue(taskType, out var bonuses))
            return bonus;

        foreach (var (key, value) in bonuses)
        {
            if (candidate.Metrics.TryGetValue(key, out var metric))
                bonus += metric * value;
        }

        return bonus;
    }

    private void Save()
    {
        lock (_saveLock)
        {
            var dir = Path.GetDirectoryName(_persistPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var data = _weights.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToDictionary(x => x.Key, x => x.Value));
            var json = JsonSerializer.Serialize(data, JsonOptions);
            File.WriteAllText(_persistPath, json);
        }
    }

    private void Load()
    {
        if (!File.Exists(_persistPath)) return;

        try
        {
            var json = File.ReadAllText(_persistPath);
            var data = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, RoutingWeight>>>(json, JsonOptions);
            if (data == null) return;

            foreach (var (taskType, providers) in data)
            {
                var dict = _weights.GetOrAdd(taskType, _ => new ConcurrentDictionary<string, RoutingWeight>());
                foreach (var (provider, weight) in providers)
                    dict[provider] = weight;
            }
        }
        catch { /* intentional: cleanup may fail */ }
    }

    public void Dispose() => Save();
}

/// <summary>
/// Bayesian Bandit strategy using three Beta beliefs per arm (quality, latency, cost)
/// with Thompson sampling and an exploration bonus.
/// </summary>
public sealed class ThompsonStrategy : IRoutingStrategy, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record ArmState
    {
        public BetaBelief Quality { get; set; } = new(2.0, 1.0);
        public BetaBelief Latency { get; set; } = new(2.0, 1.0);
        public BetaBelief Cost { get; set; } = new(2.0, 1.0);
        public int Selections { get; set; }
        public DateTime LastSelected { get; set; } = DateTime.UtcNow;
    }

    private const double KL_BUDGET = 0.1;
    private readonly ConcurrentDictionary<string, ArmState> _arms = new();
    private readonly ThreadLocal<Random> _rng = new(() => new Random());
    private readonly string _persistPath;

    public ThompsonStrategy(string? persistPath = null)
    {
        _persistPath = persistPath ?? Path.Combine("livingtree", "meta", "thompson_arms.json");
        Load();
    }

    public RoutingDecision? Select(string task, IReadOnlyList<RoutingCandidate> candidates, string taskType = "general")
    {
        if (candidates.Count == 0) return null;

        var rng = _rng.Value!;
        var now = DateTime.UtcNow;
        var totalSelections = _arms.Values.Sum(a => a.Selections);
        var scored = new List<(RoutingCandidate Candidate, double Score)>();

        foreach (var c in candidates)
        {
            var arm = _arms.GetOrAdd(c.Provider, _ => new ArmState());
            var qualitySample = arm.Quality.Sample(rng);
            var latencySample = arm.Latency.Sample(rng);
            var costSample = arm.Cost.Sample(rng);

            var uncertainty = arm.Quality.Mean * arm.Latency.Mean * arm.Cost.Mean;
            var explorationBonus = KL_BUDGET * uncertainty *
                Math.Sqrt(Math.Log(totalSelections + 1) / Math.Max(arm.Selections, 1));

            var score = qualitySample * 0.5 + latencySample * 0.25 + costSample * 0.25 + explorationBonus;

            scored.Add((c, score));
        }

        var best = scored.OrderByDescending(x => x.Score).First();
        var bestArm = _arms.GetOrAdd(best.Candidate.Provider, _ => new ArmState());
        bestArm.Selections++;
        bestArm.LastSelected = now;

        return new RoutingDecision
        {
            Provider = best.Candidate.Provider,
            Model = best.Candidate.Model,
            Strategy = nameof(ThompsonStrategy),
            Score = best.Score,
            Scores = scored.ToDictionary(x => x.Candidate.Provider, x => x.Score),
            Timestamp = now
        };
    }

    public void Record(RoutingDecision decision, bool success, Dictionary<string, object?>? metadata = null)
    {
        var arm = _arms.GetOrAdd(decision.Provider, _ => new ArmState());
        arm.Quality.Observe(success);

        var latencyNorm = NormalizeMetric(metadata, "latencyMs", 5000.0);
        arm.Latency.Observe(latencyNorm < 0.5);

        var costNorm = NormalizeMetric(metadata, "cost", 0.1);
        arm.Cost.Observe(costNorm < 0.3);

        arm.Quality.Decay();
        arm.Latency.Decay();
        arm.Cost.Decay();
    }

    public IReadOnlyDictionary<string, object> Stats()
    {
        var stats = new Dictionary<string, object>
        {
            ["arms"] = _arms.Count,
            ["totalSelections"] = _arms.Values.Sum(a => a.Selections),
            ["strategy"] = nameof(ThompsonStrategy)
        };

        foreach (var (provider, arm) in _arms)
        {
            stats[$"arm_{provider}_quality"] = arm.Quality.Mean;
            stats[$"arm_{provider}_latency"] = arm.Latency.Mean;
            stats[$"arm_{provider}_cost"] = arm.Cost.Mean;
            stats[$"arm_{provider}_selections"] = arm.Selections;
        }

        return stats;
    }

    private static double NormalizeMetric(Dictionary<string, object?>? metadata, string key, double max)
    {
        if (metadata == null || !metadata.TryGetValue(key, out var value) || value == null)
            return 0.5;

        if (value is double d)
            return Math.Clamp(d / max, 0.0, 1.0);
        if (value is int i)
            return Math.Clamp(i / max, 0.0, 1.0);
        if (value is long l)
            return Math.Clamp(l / max, 0.0, 1.0);

        return 0.5;
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(_persistPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var data = _arms.ToDictionary(
            kvp => kvp.Key,
            kvp => new
            {
                kvp.Value.Quality.Alpha,
                kvp.Value.Quality.Beta,
                LatencyAlpha = kvp.Value.Latency.Alpha,
                LatencyBeta = kvp.Value.Latency.Beta,
                CostAlpha = kvp.Value.Cost.Alpha,
                CostBeta = kvp.Value.Cost.Beta,
                kvp.Value.Selections,
                kvp.Value.LastSelected
            });

        var json = JsonSerializer.Serialize(data, JsonOptions);
        File.WriteAllText(_persistPath, json);
    }

    private void Load()
    {
        if (!File.Exists(_persistPath)) return;

        try
        {
            var json = File.ReadAllText(_persistPath);
            var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, JsonOptions);
            if (data == null) return;

            foreach (var (provider, elem) in data)
            {
                var arm = new ArmState();
                if (elem.TryGetProperty("Alpha", out var alpha))
                    arm.Quality.Alpha = alpha.GetDouble();
                if (elem.TryGetProperty("Beta", out var beta))
                    arm.Quality.Beta = beta.GetDouble();
                if (elem.TryGetProperty("LatencyAlpha", out var lAlpha))
                    arm.Latency.Alpha = lAlpha.GetDouble();
                if (elem.TryGetProperty("LatencyBeta", out var lBeta))
                    arm.Latency.Beta = lBeta.GetDouble();
                if (elem.TryGetProperty("CostAlpha", out var cAlpha))
                    arm.Cost.Alpha = cAlpha.GetDouble();
                if (elem.TryGetProperty("CostBeta", out var cBeta))
                    arm.Cost.Beta = cBeta.GetDouble();
                if (elem.TryGetProperty("Selections", out var sel))
                    arm.Selections = sel.GetInt32();
                if (elem.TryGetProperty("LastSelected", out var ls))
                    arm.LastSelected = ls.GetDateTime();
                _arms[provider] = arm;
            }
        }
        catch { /* intentional: cleanup may fail */ }
    }

    public void Dispose() => Save();
}

/// <summary>
/// Cost-aware routing strategy that tracks daily and monthly spend per provider,
/// degrading or blocking providers that have exhausted their budget.
/// </summary>
public sealed class BudgetStrategy : IRoutingStrategy, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record BudgetState
    {
        public double DailySpend { get; set; }
        public double MonthlySpend { get; set; }
        public double DailyLimit { get; set; } = 10.0;
        public double MonthlyLimit { get; set; } = 200.0;
        public DateTime LastDailyReset { get; set; } = DateTime.UtcNow;
        public bool IsFree { get; set; }
    }

    private readonly ConcurrentDictionary<string, BudgetState> _budgets = new();
    private readonly object _saveLock = new();
    private readonly string _persistPath;

    public BudgetStrategy(string? persistPath = null)
    {
        _persistPath = persistPath ?? Path.Combine("livingtree", "meta", "budget_state.json");
        Load();
    }

    public RoutingDecision? Select(string task, IReadOnlyList<RoutingCandidate> candidates, string taskType = "general")
    {
        if (candidates.Count == 0) return null;

        EnsureDailyReset();

        var scored = new List<(RoutingCandidate Candidate, double Score, double Factor)>();

        foreach (var c in candidates)
        {
            var budget = _budgets.GetOrAdd(c.Provider, _ => CreateBudgetState(c));
            var factor = BudgetFactor(c.Provider, budget);
            if (factor <= 0.0) continue;

            var costMetric = c.Metrics.TryGetValue("cost_score", out var cs) ? cs : 0.5;
            var costScore = factor * (1.0 - Math.Min(costMetric, 1.0));
            scored.Add((c, costScore, factor));
        }

        if (scored.Count == 0)
        {
            return new RoutingDecision
            {
                Provider = candidates[0].Provider,
                Model = candidates[0].Model,
                Strategy = nameof(BudgetStrategy),
                Score = 0.0,
                Scores = candidates.ToDictionary(x => x.Provider, _ => 0.0),
                Timestamp = DateTime.UtcNow,
                Metadata = new Dictionary<string, object?> { ["budgetExhausted"] = true }
            };
        }

        var best = scored.OrderByDescending(x => x.Score).First();

        return new RoutingDecision
        {
            Provider = best.Candidate.Provider,
            Model = best.Candidate.Model,
            Strategy = nameof(BudgetStrategy),
            Score = best.Score,
            Scores = scored.ToDictionary(x => x.Candidate.Provider, x => x.Score),
            Timestamp = DateTime.UtcNow
        };
    }

    public void Record(RoutingDecision decision, bool success, Dictionary<string, object?>? metadata = null)
    {
        var budget = _budgets.GetOrAdd(decision.Provider, _ => new BudgetState());
        var cost = GetCostFromDecision(decision, metadata);
        budget.DailySpend += cost;
        budget.MonthlySpend += cost;
        Save();
    }

    public IReadOnlyDictionary<string, object> Stats()
    {
        EnsureDailyReset();
        var stats = new Dictionary<string, object>
        {
            ["strategy"] = nameof(BudgetStrategy),
            ["providers"] = _budgets.Count
        };

        foreach (var (provider, budget) in _budgets)
        {
            stats[$"{provider}_dailySpend"] = budget.DailySpend;
            stats[$"{provider}_monthlySpend"] = budget.MonthlySpend;
            stats[$"{provider}_dailyLimit"] = budget.DailyLimit;
            stats[$"{provider}_monthlyLimit"] = budget.MonthlyLimit;
            stats[$"{provider}_budgetFactor"] = BudgetFactor(provider, budget);
        }

        return stats;
    }

    private double BudgetFactor(string provider, BudgetState budget)
    {
        if (budget.IsFree) return 1.0;
        if (budget.DailySpend >= budget.DailyLimit || budget.MonthlySpend >= budget.MonthlyLimit)
            return 0.0;

        var utilization = Math.Max(
            budget.DailySpend / Math.Max(budget.DailyLimit, 0.01),
            budget.MonthlySpend / Math.Max(budget.MonthlyLimit, 0.01));

        return utilization switch
        {
            < 0.3 => 1.0,
            < 0.5 => 0.7,
            < 0.7 => 0.5,
            _ => 0.3
        };
    }

    private void EnsureDailyReset()
    {
        var now = DateTime.UtcNow;
        foreach (var (_, budget) in _budgets)
        {
            if (budget.LastDailyReset.Date < now.Date)
            {
                budget.DailySpend = 0.0;
                budget.LastDailyReset = now;
            }
        }
    }

    private static BudgetState CreateBudgetState(RoutingCandidate candidate)
    {
        var state = new BudgetState();
        if (candidate.Metadata.TryGetValue("is_free", out var freeStr) && bool.TryParse(freeStr, out var isFree))
            state.IsFree = isFree;
        if (candidate.Metadata.TryGetValue("daily_limit", out var dl) && double.TryParse(dl, out var dailyLimit))
            state.DailyLimit = dailyLimit;
        if (candidate.Metadata.TryGetValue("monthly_limit", out var ml) && double.TryParse(ml, out var monthlyLimit))
            state.MonthlyLimit = monthlyLimit;
        return state;
    }

    private static double GetCostFromDecision(RoutingDecision decision, Dictionary<string, object?>? metadata)
    {
        if (metadata != null && metadata.TryGetValue("cost", out var costVal) && costVal is double d)
            return d;
        if (decision.Scores.TryGetValue("cost", out var costScore))
            return costScore;
        return 0.001;
    }

    private void Save()
    {
        lock (_saveLock)
        {
            var dir = Path.GetDirectoryName(_persistPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_budgets.ToDictionary(k => k.Key, v => v.Value), JsonOptions);
            File.WriteAllText(_persistPath, json);
        }
    }

    private void Load()
    {
        if (!File.Exists(_persistPath)) return;

        try
        {
            var json = File.ReadAllText(_persistPath);
            var data = JsonSerializer.Deserialize<Dictionary<string, BudgetState>>(json, JsonOptions);
            if (data == null) return;

            foreach (var (provider, state) in data)
                _budgets[provider] = state;
        }
        catch { /* intentional: cleanup may fail */ }
    }

    public void Dispose() => Save();
}

/// <summary>
/// Squeeze-Evolve routing strategy that maintains tier-based statistics
/// (pro/mid/flash/skip) with dynamic threshold adjustment based on success rates.
/// </summary>
public sealed class FitnessStrategy : IRoutingStrategy, IDisposable
{
    private sealed class TierStats
    {
        public int Decisions;
        public int Successes;
        public double SuccessRate => Decisions > 0 ? (double)Successes / Decisions : 0.0;
    }

    private const double THRESHOLD_LOW = 0.4;
    private const double THRESHOLD_HIGH_MAX = 0.85;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ConcurrentDictionary<string, TierStats> _tierStats = new();
    private double _thresholdHigh = 0.65;
    private readonly object _thresholdLock = new();
    private readonly string _persistPath;

    public FitnessStrategy(string? persistPath = null)
    {
        _persistPath = persistPath ?? Path.Combine("livingtree", "meta", "fitness_state.json");
        Load();
    }

    public RoutingDecision? Select(string task, IReadOnlyList<RoutingCandidate> candidates, string taskType = "general")
    {
        if (candidates.Count == 0) return null;

        var now = DateTime.UtcNow;
        double thresholdHigh;
        lock (_thresholdLock) { thresholdHigh = _thresholdHigh; }
        var scored = new List<(RoutingCandidate Candidate, string Tier, double Score)>();

        foreach (var c in candidates)
        {
            var tier = DetermineTier(c, thresholdHigh);
            var stats = _tierStats.GetOrAdd(tier, _ => new TierStats());
            var score = stats.SuccessRate > 0 ? stats.SuccessRate : GetDefaultTierScore(tier);
            scored.Add((c, tier, score));
        }

        var best = scored.OrderByDescending(x => x.Score).First();

        return new RoutingDecision
        {
            Provider = best.Candidate.Provider,
            Model = best.Candidate.Model,
            Strategy = nameof(FitnessStrategy),
            Score = best.Score,
            Scores = scored.ToDictionary(x => $"{x.Candidate.Provider} ({x.Tier})", x => x.Score),
            Metadata = new Dictionary<string, object?>
            {
                ["tier"] = best.Tier,
                ["thresholdHigh"] = thresholdHigh
            },
            Timestamp = now
        };
    }

    public void Record(RoutingDecision decision, bool success, Dictionary<string, object?>? metadata = null)
    {
        var tier = decision.Metadata.TryGetValue("tier", out var t) ? t?.ToString() ?? "mid" : "mid";
        var stats = _tierStats.GetOrAdd(tier, _ => new TierStats());
        Interlocked.Increment(ref stats.Decisions);
        if (success) Interlocked.Increment(ref stats.Successes);

        AdjustThresholds();
    }

    public IReadOnlyDictionary<string, object> Stats()
    {
        var stats = new Dictionary<string, object>
        {
            ["strategy"] = nameof(FitnessStrategy),
            ["thresholdHigh"] = _thresholdHigh,
            ["thresholdLow"] = THRESHOLD_LOW
        };

        foreach (var (tier, tierStat) in _tierStats)
        {
            stats[$"tier_{tier}_decisions"] = tierStat.Decisions;
            stats[$"tier_{tier}_successes"] = tierStat.Successes;
            stats[$"tier_{tier}_successRate"] = tierStat.SuccessRate;
        }

        return stats;
    }

    private static string DetermineTier(RoutingCandidate candidate, double thresholdHigh)
    {
        var capability = candidate.Metrics.TryGetValue("capability_score", out var cs) ? cs : 0.5;
        var cost = candidate.Metrics.TryGetValue("cost_score", out var cst) ? cst : 0.5;

        if (capability >= thresholdHigh) return "pro";
        if (capability >= THRESHOLD_LOW + 0.1) return "mid";
        if (cost < 0.2) return "flash";
        return "skip";
    }

    private static double GetDefaultTierScore(string tier)
    {
        return tier switch
        {
            "pro" => 0.85,
            "mid" => 0.6,
            "flash" => 0.4,
            _ => 0.25
        };
    }

    private void AdjustThresholds()
    {
        var flash = _tierStats.GetOrAdd("flash", _ => new TierStats());
        var totalDecisions = _tierStats.Values.Sum(s => s.Decisions);

        if (totalDecisions > 10 && flash.SuccessRate > 0.7)
        {
            lock (_thresholdLock)
            {
                _thresholdHigh = Math.Min(THRESHOLD_HIGH_MAX, _thresholdHigh + 0.05);
            }
        }
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(_persistPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var data = new Dictionary<string, object>
        {
            ["thresholdHigh"] = _thresholdHigh,
            ["tiers"] = _tierStats.ToDictionary(k => k.Key, v => v.Value)
        };
        var json = JsonSerializer.Serialize(data, JsonOptions);
        File.WriteAllText(_persistPath, json);
    }

    private void Load()
    {
        if (!File.Exists(_persistPath)) return;

        try
        {
            var json = File.ReadAllText(_persistPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("thresholdHigh", out var th))
                _thresholdHigh = th.GetDouble();

            if (doc.RootElement.TryGetProperty("tiers", out var tiers))
            {
                foreach (var tierProp in tiers.EnumerateObject())
                {
                    var tierStat = JsonSerializer.Deserialize<TierStats>(tierProp.Value.GetRawText(), JsonOptions);
                    if (tierStat != null)
                        _tierStats[tierProp.Name] = tierStat;
                }
            }
        }
        catch { /* intentional: cleanup may fail */ }
    }

    public void Dispose() => Save();
}

/// <summary>
/// Historical-pattern routing strategy that tracks hourly and daily provider performance
/// via EMA, combined with latency trends and error rates to predict success.
/// </summary>
public sealed class PredictiveStrategy : IRoutingStrategy, IDisposable
{
    private const double EMA_ALPHA = 0.85;

    private readonly ConcurrentDictionary<string, double[]> _hourly = new();
    private readonly ConcurrentDictionary<string, double[]> _daily = new();
    private readonly ConcurrentDictionary<string, List<double>> _latencyHistory = new();
    private readonly ConcurrentDictionary<string, double> _errorRates = new();
    private readonly ConcurrentDictionary<string, int> _totalCalls = new();
    private readonly ConcurrentDictionary<string, int> _errorCounts = new();
    private readonly object _saveLock = new();
    private readonly string _persistPath;

    public PredictiveStrategy(string? persistPath = null)
    {
        _persistPath = persistPath ?? Path.Combine("livingtree", "meta", "predictive_state.json");
        Load();
    }

    public RoutingDecision? Select(string task, IReadOnlyList<RoutingCandidate> candidates, string taskType = "general")
    {
        if (candidates.Count == 0) return null;

        var now = DateTime.UtcNow;
        var hour = now.Hour;
        var weekday = (int)now.DayOfWeek;
        var scored = new List<(RoutingCandidate Candidate, double Score)>();

        foreach (var c in candidates)
        {
            var hourlyScore = GetHourlyScore(c.Provider, hour);
            var dailyScore = GetDailyScore(c.Provider, weekday);
            var latencyTrend = LatencyTrend(c.Provider);
            var errorRate = _errorRates.GetValueOrDefault(c.Provider, 0.0);

            var score = hourlyScore * 0.35 + dailyScore * 0.25 + latencyTrend * 0.15 + (1.0 - errorRate) * 0.25;
            scored.Add((c, score));
        }

        var best = scored.OrderByDescending(x => x.Score).First();

        return new RoutingDecision
        {
            Provider = best.Candidate.Provider,
            Model = best.Candidate.Model,
            Strategy = nameof(PredictiveStrategy),
            Score = best.Score,
            Scores = scored.ToDictionary(x => x.Candidate.Provider, x => x.Score),
            Timestamp = now
        };
    }

    public void Record(RoutingDecision decision, bool success, Dictionary<string, object?>? metadata = null)
    {
        var now = DateTime.UtcNow;
        var hour = now.Hour;
        var weekday = (int)now.DayOfWeek;
        var provider = decision.Provider;

        _totalCalls.AddOrUpdate(provider, 1, (_, v) => v + 1);
        if (!success)
            _errorCounts.AddOrUpdate(provider, 1, (_, v) => v + 1);

        var total = _totalCalls.GetValueOrDefault(provider, 1);
        var errors = _errorCounts.GetValueOrDefault(provider, 0);
        _errorRates[provider] = (double)errors / total;

        var hourly = _hourly.GetOrAdd(provider, _ => new double[24]);
        hourly[hour] = EMA_ALPHA * hourly[hour] + (1.0 - EMA_ALPHA) * (success ? 1.0 : 0.0);

        var daily = _daily.GetOrAdd(provider, _ => new double[7]);
        daily[weekday] = EMA_ALPHA * daily[weekday] + (1.0 - EMA_ALPHA) * (success ? 1.0 : 0.0);

        var latency = metadata != null && metadata.TryGetValue("latencyMs", out var l) ? l : null;
        if (latency is double dLatency)
        {
            var history = _latencyHistory.GetOrAdd(provider, _ => new List<double>());
            lock (history)
            {
                history.Add(dLatency);
                if (history.Count > 100)
                    history.RemoveRange(0, history.Count - 100);
            }
        }

        Save();
    }

    public IReadOnlyDictionary<string, object> Stats()
    {
        var stats = new Dictionary<string, object>
        {
            ["strategy"] = nameof(PredictiveStrategy),
            ["providers"] = _totalCalls.Count
        };

        foreach (var provider in _totalCalls.Keys)
        {
            stats[$"{provider}_errorRate"] = _errorRates.GetValueOrDefault(provider, 0.0);
            stats[$"{provider}_totalCalls"] = _totalCalls.GetValueOrDefault(provider, 0);
            stats[$"{provider}_latencyTrend"] = LatencyTrend(provider);
        }

        return stats;
    }

    private double GetHourlyScore(string provider, int hour)
    {
        var arr = _hourly.GetOrAdd(provider, _ => new double[24]);
        return arr[hour];
    }

    private double GetDailyScore(string provider, int weekday)
    {
        var arr = _daily.GetOrAdd(provider, _ => new double[7]);
        return arr[weekday];
    }

    private double LatencyTrend(string provider)
    {
        if (!_latencyHistory.TryGetValue(provider, out var history) || history.Count < 6)
            return 0.7;

        lock (history)
        {
            var first3 = history.Take(3).Average();
            var last3 = history.TakeLast(3).Average();
            if (first3 <= 0) return 0.7;
            var ratio = last3 / first3;
            if (ratio < 0.9) return 1.0;
            if (ratio > 1.1) return 0.3;
            return 0.7;
        }
    }

    private void Save()
    {
        lock (_saveLock)
        {
            var dir = Path.GetDirectoryName(_persistPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var data = new PredictiveState
            {
                Hourly = _hourly.ToDictionary(k => k.Key, v => v.Value.ToList()),
                Daily = _daily.ToDictionary(k => k.Key, v => v.Value.ToList()),
                LatencyHistory = _latencyHistory.ToDictionary(k => k.Key, v => v.Value.ToList()),
                ErrorRates = new Dictionary<string, double>(_errorRates),
                TotalCalls = new Dictionary<string, int>(_totalCalls),
                ErrorCounts = new Dictionary<string, int>(_errorCounts)
            };
            var json = JsonSerializer.Serialize(data, JsonOptions);
            File.WriteAllText(_persistPath, json);
        }
    }

    private void Load()
    {
        if (!File.Exists(_persistPath)) return;

        try
        {
            var json = File.ReadAllText(_persistPath);
            var data = JsonSerializer.Deserialize<PredictiveState>(json, JsonOptions);
            if (data == null) return;

            foreach (var (provider, values) in data.Hourly)
                _hourly[provider] = values.ToArray();
            foreach (var (provider, values) in data.Daily)
                _daily[provider] = values.ToArray();
            foreach (var (provider, values) in data.LatencyHistory)
                _latencyHistory[provider] = values.ToList();
            foreach (var (provider, value) in data.ErrorRates)
                _errorRates[provider] = value;
            foreach (var (provider, value) in data.TotalCalls)
                _totalCalls[provider] = value;
            foreach (var (provider, value) in data.ErrorCounts)
                _errorCounts[provider] = value;
        }
        catch
        {
            // Persistence load failure is non-fatal.
        }
    }

    private sealed class PredictiveState
    {
        public Dictionary<string, List<double>> Hourly { get; init; } = new();
        public Dictionary<string, List<double>> Daily { get; init; } = new();
        public Dictionary<string, List<double>> LatencyHistory { get; init; } = new();
        public Dictionary<string, double> ErrorRates { get; init; } = new();
        public Dictionary<string, int> TotalCalls { get; init; } = new();
        public Dictionary<string, int> ErrorCounts { get; init; } = new();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public void Dispose() => Save();
}

/// <summary>
/// TD-learning routing strategy that maintains per-(provider, taskType) scores
/// using TD(0) temporal difference updates with gradient tracking.
/// </summary>
public sealed class ScoreMatchStrategy : IRoutingStrategy, IDisposable
{
    private const double GAMMA = 0.99;
    private const int GRADIENT_WINDOW = 50;

    private readonly ConcurrentDictionary<(string Provider, string TaskType), double> _scores = new();
    private readonly ConcurrentDictionary<string, List<double>> _gradientHistory = new();
    private readonly ConcurrentDictionary<(string Provider, string TaskType), double> _lastScores = new();
    private readonly object _saveLock = new();
    private readonly string _persistPath;

    public ScoreMatchStrategy(string? persistPath = null)
    {
        _persistPath = persistPath ?? Path.Combine("livingtree", "meta", "scorematch_state.json");
        Load();
    }

    public RoutingDecision? Select(string task, IReadOnlyList<RoutingCandidate> candidates, string taskType = "general")
    {
        if (candidates.Count == 0) return null;

        var now = DateTime.UtcNow;
        var scored = new List<(RoutingCandidate Candidate, double Score)>();

        foreach (var c in candidates)
        {
            var key = (c.Provider, taskType);
            var baseScore = _scores.GetOrAdd(key, 0.5);
            var gradient = ProviderGradient(c.Provider);
            var score = Math.Clamp(baseScore * gradient, 0.0, 1.0);

            scored.Add((c, score));
        }

        var best = scored.OrderByDescending(x => x.Score).First();

        return new RoutingDecision
        {
            Provider = best.Candidate.Provider,
            Model = best.Candidate.Model,
            Strategy = nameof(ScoreMatchStrategy),
            Score = best.Score,
            Scores = scored.ToDictionary(x => x.Candidate.Provider, x => x.Score),
            Timestamp = now
        };
    }

    public void Record(RoutingDecision decision, bool success, Dictionary<string, object?>? metadata = null)
    {
        var taskType = decision.Metadata.TryGetValue("taskType", out var tt) ? tt?.ToString() ?? "general" : "general";
        var key = (decision.Provider, taskType);
        var currentScore = _scores.GetOrAdd(key, 0.5);
        var lastScore = _lastScores.GetValueOrDefault(key, currentScore);

        var latencyBonus = ComputeLatencyBonus(metadata);
        var costBonus = ComputeCostBonus(metadata);
        var reward = (success ? 1.0 : -0.3) + latencyBonus + costBonus;

        var targetScore = Math.Clamp(currentScore + reward, 0.0, 1.0);
        var newScore = currentScore + GAMMA * (targetScore - currentScore);
        _scores[key] = Math.Clamp(newScore, 0.0, 1.0);

        var gradient = newScore - lastScore;
        TrackGradient(decision.Provider, gradient);

        _lastScores[key] = newScore;
    }

    public IReadOnlyDictionary<string, object> Stats()
    {
        var stats = new Dictionary<string, object>
        {
            ["strategy"] = nameof(ScoreMatchStrategy),
            ["entries"] = _scores.Count
        };

        foreach (var ((provider, taskType), score) in _scores)
        {
            var gradient = ProviderGradient(provider);
            stats[$"{provider}_{taskType}_score"] = score;
            stats[$"{provider}_gradient"] = gradient;
        }

        return stats;
    }

    private double ProviderGradient(string provider)
    {
        var history = _gradientHistory.GetOrAdd(provider, _ => new List<double>());
        lock (history)
        {
            if (history.Count < 2) return 1.0;
            var ema = history.TakeLast(Math.Min(GRADIENT_WINDOW, history.Count)).Average();
            return Math.Clamp((ema + 1.0) / 2.0, 0.0, 1.0);
        }
    }

    private void TrackGradient(string provider, double gradient)
    {
        var history = _gradientHistory.GetOrAdd(provider, _ => new List<double>());
        lock (history)
        {
            history.Add(gradient);
            if (history.Count > GRADIENT_WINDOW)
                history.RemoveRange(0, history.Count - GRADIENT_WINDOW);
        }
    }

    private static double ComputeLatencyBonus(Dictionary<string, object?>? metadata)
    {
        if (metadata == null || !metadata.TryGetValue("latencyMs", out var val) || val == null)
            return 0.0;

        var latency = val is double d ? d : 500.0;
        return Math.Clamp(0.1 - latency / 10000.0, 0.0, 0.1);
    }

    private static double ComputeCostBonus(Dictionary<string, object?>? metadata)
    {
        if (metadata == null || !metadata.TryGetValue("cost", out var val) || val == null)
            return 0.0;

        var cost = val is double d ? d : 0.001;
        return Math.Clamp(0.1 - cost * 10.0, 0.0, 0.1);
    }

    private void Save()
    {
        lock (_saveLock)
        {
            var dir = Path.GetDirectoryName(_persistPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var data = new ScoreMatchState
            {
                Scores = _scores.ToDictionary(
                    k => $"{k.Key.Provider}|{k.Key.TaskType}",
                    v => v.Value),
                GradientHistory = _gradientHistory.ToDictionary(k => k.Key, v => v.Value.ToList())
            };
            var json = JsonSerializer.Serialize(data, JsonOptions);
            File.WriteAllText(_persistPath, json);
        }
    }

    private void Load()
    {
        if (!File.Exists(_persistPath)) return;

        try
        {
            var json = File.ReadAllText(_persistPath);
            var data = JsonSerializer.Deserialize<ScoreMatchState>(json, JsonOptions);
            if (data == null) return;

            foreach (var (keyStr, score) in data.Scores)
            {
                var parts = keyStr.Split('|');
                if (parts.Length == 2)
                    _scores[(parts[0], parts[1])] = score;
            }

            foreach (var (provider, history) in data.GradientHistory)
                _gradientHistory[provider] = history.ToList();
        }
        catch { /* intentional: cleanup may fail */ }
    }

    private sealed class ScoreMatchState
    {
        public Dictionary<string, double> Scores { get; init; } = new();
        public Dictionary<string, List<double>> GradientHistory { get; init; } = new();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public void Dispose() => Save();
}

/// <summary>
/// Facade that instantiates and manages all six routing strategies via a singleton.
/// Delegates <c>Select</c>, <c>Record</c>, and <c>Stats</c> calls to the chosen strategy.
/// </summary>
public sealed class UnifiedRouter : IDisposable
{
    private static readonly Lazy<UnifiedRouter> _instance = new(() => new UnifiedRouter());
    public static UnifiedRouter Instance => _instance.Value;

    private readonly ThreadLocal<Random> _rng = new(() => new Random());

    public RouteLearner Learner { get; }
    public ThompsonStrategy Thompson { get; }
    public BudgetStrategy Budget { get; }
    public FitnessStrategy Fitness { get; }
    public PredictiveStrategy Predictive { get; }
    public ScoreMatchStrategy ScoreMatch { get; }

    private readonly Dictionary<string, IRoutingStrategy> _strategies;

    private UnifiedRouter()
    {
        var baseDir = Path.Combine("livingtree", "meta");
        Learner = new RouteLearner(Path.Combine(baseDir, "route_weights.json"));
        Thompson = new ThompsonStrategy(Path.Combine(baseDir, "thompson_arms.json"));
        Budget = new BudgetStrategy(Path.Combine(baseDir, "budget_state.json"));
        Fitness = new FitnessStrategy(Path.Combine(baseDir, "fitness_state.json"));
        Predictive = new PredictiveStrategy(Path.Combine(baseDir, "predictive_state.json"));
        ScoreMatch = new ScoreMatchStrategy(Path.Combine(baseDir, "scorematch_state.json"));

        _strategies = new Dictionary<string, IRoutingStrategy>
        {
            [nameof(RouteLearner)] = Learner,
            [nameof(ThompsonStrategy)] = Thompson,
            [nameof(BudgetStrategy)] = Budget,
            [nameof(FitnessStrategy)] = Fitness,
            [nameof(PredictiveStrategy)] = Predictive,
            [nameof(ScoreMatchStrategy)] = ScoreMatch
        };
    }

    /// <summary>Selects a provider using the specified strategy.</summary>
    public RoutingDecision? Select(
        string task,
        IReadOnlyList<RoutingCandidate> candidates,
        string taskType = "general",
        string strategy = nameof(RouteLearner))
    {
        if (!_strategies.TryGetValue(strategy, out var strat))
            strat = Learner;

        var decision = strat.Select(task, candidates, taskType);
        if (decision != null)
            decision.Metadata["strategyName"] = strategy;

        return decision;
    }

    /// <summary>Records the outcome of a routing decision against the strategy that made it.</summary>
    public void Record(RoutingDecision decision, bool success, Dictionary<string, object?>? metadata = null)
    {
        var strategyName = decision.Metadata.TryGetValue("strategyName", out var sn)
            ? sn?.ToString()
            : decision.Strategy;

        if (strategyName != null && _strategies.TryGetValue(strategyName, out var strat))
            strat.Record(decision, success, metadata);
    }

    /// <summary>Returns stats for the specified strategy, or all strategies if none is given.</summary>
    public IReadOnlyDictionary<string, object> Stats(string? strategy = null)
    {
        if (strategy != null && _strategies.TryGetValue(strategy, out var strat))
            return strat.Stats();

        var combined = new Dictionary<string, object>();
        foreach (var (name, s) in _strategies)
        {
            foreach (var (key, value) in s.Stats())
                combined[$"{name}.{key}"] = value;
        }
        return combined;
    }

    /// <summary>Returns the internal random number generator for this thread.</summary>
    public Random GetRandom() => _rng.Value!;

    public void Dispose()
    {
        Learner.Dispose();
        Thompson.Dispose();
        Budget.Dispose();
        Fitness.Dispose();
        Predictive.Dispose();
        ScoreMatch.Dispose();
    }
}
