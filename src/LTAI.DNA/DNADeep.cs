using System.Collections.Concurrent;
using LTAI.Core.System;
using Microsoft.Extensions.Logging;

namespace LTAI.DNA;

public sealed class SelfEvolution
{
    private readonly ILogger<SelfEvolution> _logger;
    private readonly ConcurrentDictionary<string, EvolutionRule> _rules = new();
    private readonly List<EvolutionEvent> _history = new();
    private double _mutationRate = 0.05;
    private int _generation;

    public IReadOnlyDictionary<string, EvolutionRule> Rules => _rules;
    public double MutationRate => _mutationRate;

    public SelfEvolution(ILogger<SelfEvolution> logger)
    {
        _logger = logger;
        InitializeBaseRules();
    }

    public async Task<EvolutionReport> EvolveAsync(Dictionary<string, double> feedback, CancellationToken ct = default)
    {
        _generation++;
        var mutations = new List<string>();

        foreach (var (name, signal) in feedback)
        {
            if (_rules.TryGetValue(name, out var rule))
            {
                rule.Strength = Math.Clamp(rule.Strength * 0.95 + signal * 0.05, 0.01, 1.0);
                _history.Add(new EvolutionEvent { Rule = name, OldStrength = rule.Strength, NewStrength = rule.Strength, Trigger = "feedback" });
            }
            else if (signal > 0.5)
            {
                _rules[name] = new EvolutionRule { Name = name, Strength = signal, CreatedAt = DateTime.UtcNow };
                mutations.Add($"+{name}");
            }
        }

        if (_generation % 10 == 0)
        {
            foreach (var (name, rule) in _rules)
            {
                if (rule.Strength < 0.05)
                {
                    _rules.TryRemove(name, out _);
                    mutations.Add($"-{name}");
                }
            }
        }

        if (_generation % 20 == 0)
        {
            _mutationRate = Math.Clamp(_mutationRate * (0.8 + feedback.GetValueOrDefault("exploration", 0.5) * 0.4), 0.01, 0.3);
        }

        if (mutations.Count > 0)
            _logger.LogInformation("Evolution gen{Gen}: {Count} mutations (rate={Rate:F3})", _generation, mutations.Count, _mutationRate);

        return await Task.FromResult(new EvolutionReport
        {
            Generation = _generation, MutationCount = mutations.Count,
            ActiveRules = _rules.Count, MutationRate = _mutationRate,
            RecentMutations = mutations
        }).ConfigureAwait(false);
    }

    private void InitializeBaseRules()
    {
        var baseRules = new[] { "curiosity", "precision", "adaptability", "efficiency", "creativity" };
        foreach (var r in baseRules)
            _rules[r] = new EvolutionRule { Name = r, Strength = 0.7, CreatedAt = DateTime.UtcNow };
    }
}

public sealed class EvolutionRule
{
    public string Name { get; init; } = "";
    public double Strength { get; set; } = 0.5;
    public DateTime CreatedAt { get; init; }
    public DateTime LastModified { get; set; } = DateTime.UtcNow;
}

public sealed class EvolutionEvent
{
    public string Rule { get; init; } = "";
    public double OldStrength { get; init; }
    public double NewStrength { get; init; }
    public string Trigger { get; init; } = "";
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public sealed class EvolutionReport
{
    public int Generation { get; init; }
    public int MutationCount { get; init; }
    public int ActiveRules { get; init; }
    public double MutationRate { get; init; }
    public List<string> RecentMutations { get; init; } = new();
}

public sealed class WorldModel
{
    private readonly ConcurrentDictionary<string, WorldEntity> _entities = new();
    private readonly ConcurrentDictionary<string, CausalRelation> _relations = new();
    private double _modelAccuracy = 0.5;

    public double Accuracy => _modelAccuracy;

    public void Observe(string entity, string attribute, double value)
    {
        var e = _entities.GetOrAdd(entity, _ => new WorldEntity { Name = entity });
        e.Attributes[attribute] = value;
        e.LastObserved = DateTime.UtcNow;
        _modelAccuracy = Math.Min(1.0, _modelAccuracy + 0.01);
    }

    public void LearnRelation(string from, string to, string relation, double strength)
    {
        var key = $"{from}->{to}:{relation}";
        _relations.AddOrUpdate(key,
            _ => new CausalRelation { From = from, To = to, Relation = relation, Strength = strength },
            (_, r) => { r.Strength = r.Strength * 0.9 + strength * 0.1; return r; });
    }

    public double Predict(string entity, string attribute)
    {
        if (!_entities.TryGetValue(entity, out var e)) return 0;
        return e.Attributes.GetValueOrDefault(attribute);

    }

    public List<string> Simulate(string entity, int steps = 3)
    {
        var result = new List<string> { entity };
        var current = entity;
        for (var i = 0; i < steps; i++)
        {
            var next = _relations
                .Where(r => r.Value.From == current && r.Value.Strength > 0.3)
                .OrderByDescending(r => r.Value.Strength)
                .FirstOrDefault();
            if (next.Value == null) break;
            result.Add($"[{next.Value.Relation}]→ {next.Value.To}");
            current = next.Value.To;
        }
        return result;
    }

    public IReadOnlyList<WorldEntity> GetEntities() => _entities.Values.ToList().AsReadOnly();
    public int EntityCount => _entities.Count;
    public int RelationCount => _relations.Count;
}

public sealed class WorldEntity
{
    public string Name { get; init; } = "";
    public Dictionary<string, double> Attributes { get; init; } = new();
    public DateTime LastObserved { get; set; } = DateTime.UtcNow;
}

public sealed class CausalRelation
{
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string Relation { get; init; } = "";
    public double Strength { get; set; }
}

public sealed class PredictiveEngine
{
    private readonly ConcurrentDictionary<string, TimeSeries> _series = new();
    private readonly double _decay = 0.85;

    public void Record(string metric, double value)
    {
        var ts = _series.GetOrAdd(metric, _ => new TimeSeries());
        ts.EMA = ts.EMA * _decay + value * (1.0 - _decay);
        ts.Trend = ts.Trend * _decay + (value - ts.EMA) * (1.0 - _decay);
        ts.LastValue = value;
        ts.Samples++;
    }

    public double Forecast(string metric, int steps = 1)
    {
        if (!_series.TryGetValue(metric, out var ts)) return 0;
        return Math.Clamp(ts.EMA + ts.Trend * steps, 0, 1);
    }

    public bool DetectAnomaly(string metric, double value, double threshold = 2.0)
    {
        if (!_series.TryGetValue(metric, out var ts) || ts.Samples < 10) return false;
        var deviation = Math.Abs(value - ts.EMA);
        var stdDev = Math.Sqrt(ts.Variance > 0 ? ts.Variance : 0.01);
        return deviation / stdDev > threshold;
    }

    public IReadOnlyList<string> GetTrending(int topN = 5) =>
        _series.OrderByDescending(kvp => Math.Abs(kvp.Value.Trend)).Take(topN).Select(kvp => kvp.Key).ToList().AsReadOnly();
}

public sealed class TimeSeries
{
    public double EMA;
    public double Trend;
    public double Variance = 0.01;
    public double LastValue;
    public int Samples;
}

public sealed class MentalTimeTravel
{
    private readonly List<Episode> _episodes = new();
    private readonly WorldModel _world;

    public MentalTimeTravel(WorldModel world) => _world = world;

    public void RecordEpisode(string context, string outcome, double significance)
    {
        _episodes.Add(new Episode
        {
            Context = context, Outcome = outcome,
            Significance = significance, Timestamp = DateTime.UtcNow
        });
        if (_episodes.Count > 200) _episodes.RemoveAt(0);
    }

    public string Recall(string query)
    {
        var relevant = _episodes
            .Where(e => e.Context.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        e.Outcome.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Significance)
            .Take(3).ToList();

        if (relevant.Count == 0) return "No relevant memories found.";

        return string.Join("\n", relevant.Select(e =>
            $"[{e.Timestamp:HH:mm}] {e.Context[..Math.Min(e.Context.Length, 80)]} → {e.Outcome[..Math.Min(e.Outcome.Length, 80)]} (sig:{e.Significance:F2})"));
    }

    public string SimulateFuture(string scenario, int variants = 3)
    {
        var path = _world.Simulate(scenario, variants);
        return $"Future simulation for '{scenario}':\n" + string.Join("\n", path);
    }

    public int EpisodeCount => _episodes.Count;
}

public sealed class Episode
{
    public string Context { get; init; } = "";
    public string Outcome { get; init; } = "";
    public double Significance { get; init; }
    public DateTime Timestamp { get; init; }
}

public sealed class ForesightGovernance
{
    private readonly PredictiveEngine _predictor;
    private readonly ConcurrentDictionary<string, double> _riskScores = new();

    public ForesightGovernance(PredictiveEngine predictor) => _predictor = predictor;

    public (bool proceed, string reason) EvaluateAction(string action, Dictionary<string, double>? context = null)
    {
        var risk = _riskScores.GetValueOrDefault(action);
        var trend = _predictor.Forecast("safety", 3);

        if (risk + (1.0 - trend) > 0.7)
            return (false, $"High risk ({risk:F2}) + low safety trend ({trend:F2})");

        if (context != null)
        {
            foreach (var (k, v) in context)
                _predictor.Record($"action_{action}_{k}", v);
        }

        return (true, $"Risk={risk:F2}, SafetyTrend={trend:F2}");
    }

    public void UpdateRisk(string action, double score) =>
        _riskScores.AddOrUpdate(action, score, (_, v) => v * 0.9 + score * 0.1);

    public Dictionary<string, double> GetRiskProfile() => new(_riskScores);
}

public sealed class EntropyDrive
{
    private readonly Random _rng = new();
    private readonly EntropyScheduler _scheduler;

    public double EntropyLevel => _scheduler.CurrentEntropy;
    public EntropyScheduler Scheduler => _scheduler;

    public EntropyDrive(EntropyScheduler? scheduler = null)
    {
        _scheduler = scheduler ?? new EntropyScheduler(new EntropyScheduleConfig
        {
            Type = EntropyScheduleType.Linear,
            InitialEntropy = 0.8,
            TargetEntropy = 0.15,
            WarmupSteps = 50,
            TotalSteps = 2000
        });
    }

    public string? Explore(List<string> candidates)
    {
        if (candidates.Count == 0) return null;
        _scheduler.StepForward();

        if (_scheduler.ShouldExplore(_rng))
        {
            var pick = candidates[_rng.Next(candidates.Count)];
            return pick;
        }

        return null;
    }

    public void Reset() => _scheduler.Reset();
}

public sealed class FocusDilution
{
    private readonly ConcurrentDictionary<string, double> _attention = new();
    private double _totalAttention;

    public void Focus(string item, double amount)
    {
        _attention.AddOrUpdate(item, amount, (_, v) => Math.Min(1.0, v + amount));
        _totalAttention = _attention.Values.Sum();
        Normalize();
    }

    public void Defocus(string item, double amount)
    {
        _attention.AddOrUpdate(item, 0, (_, v) => Math.Max(0, v - amount));
        _totalAttention = _attention.Values.Sum();
        Normalize();
    }

    public IReadOnlyList<(string item, double focus)> GetFocusDistribution() =>
        _attention.OrderByDescending(kvp => kvp.Value).Take(5)
            .Select(kvp => (kvp.Key, kvp.Value)).ToList().AsReadOnly();

    public double GetFocus(string item) => _attention.GetValueOrDefault(item);

    private void Normalize()
    {
        if (_totalAttention <= 1.0) return;
        var scale = 1.0 / _totalAttention;
        foreach (var key in _attention.Keys)
            _attention[key] *= scale;
        _totalAttention = 1.0;
    }
}

public sealed class GodelianSelf
{
    private readonly List<string> _selfReflections = new();
    private int _depth;

    public int Depth => _depth;
    public int MetaChainDepth => _depth;
    public int GodelianNesting => _selfReflections.Count(r => r.StartsWith("PARADOX"));

    public (double MetaChainDepth, double GodelianNesting) GetDepthMetric()
    {
        return (MetaChainDepth, GodelianNesting);
    }

    public string Reflect(string statement)
    {
        _depth++;
        var paradox = DetectParadox(statement);
        var meta = $"Reflection L{_depth}: Analyzing '{statement[..Math.Min(statement.Length, 60)]}'";

        if (paradox != null)
        {
            _selfReflections.Add($"PARADOX: {paradox}");
            return $"{meta}\n[Godelian] Paradox detected: {paradox}\nThe system contains statements that cannot be proven within itself.";
        }

        _selfReflections.Add(meta);
        if (_depth > 3)
        {
            _depth = 0;
            return $"{meta}\n[Godelian] Recursion depth limit reached. Re-grounding in base axioms.";
        }

        return $"{meta}\n[Godelian] Self-reference valid at depth {_depth}.";
    }

    private static string? DetectParadox(string statement)
    {
        if (statement.Contains("this statement is false") || statement.Contains("I am lying"))
            return "Liar's paradox: self-negating statement";
        if (statement.Contains("cannot") && statement.Contains("itself"))
            return "Incompleteness: system cannot fully describe itself";
        return null;
    }
}
