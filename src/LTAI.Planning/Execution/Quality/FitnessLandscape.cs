using LTAI.Planning.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Planning.Quality;

public sealed class FitnessLandscape
{
    private static readonly Lazy<FitnessLandscape> _instance = new(() => new FitnessLandscape());
    public static FitnessLandscape Instance => _instance.Value;

    private readonly List<TrajectoryScore> _trajectories = new();
    private readonly Lock _lock = new();
    private readonly Random _rng = new();
    private readonly ILogger<FitnessLandscape> _logger;

    private FitnessLandscape()
    {
        _logger = NullLogger<FitnessLandscape>.Instance;
    }

    internal FitnessLandscape(ILogger<FitnessLandscape> logger)
    {
        _logger = logger ?? NullLogger<FitnessLandscape>.Instance;
    }

    public void Record(string trajectoryId, List<string> toolSequence, int tokens, int ms, bool success, int safetyViolations)
    {
        var reliability = Math.Clamp((success ? 0.8 : 0.2) - safetyViolations * 0.1, 0.0, 1.0);
        var costEfficiency = Math.Clamp(1.0 / (1.0 + tokens / 1000.0), 0.0, 1.0);
        var speed = Math.Clamp(1.0 / (1.0 + ms / 60000.0), 0.0, 1.0);
        var safety = Math.Clamp(1.0 / (1.0 + safetyViolations * 0.5), 0.0, 1.0);

        var score = new TrajectoryScore
        {
            TrajectoryId = trajectoryId,
            ToolSequence = new List<string>(toolSequence),
            TotalTokens = tokens,
            TotalMs = ms,
            Fitness = new FitnessVector
            {
                Reliability = reliability,
                CostEfficiency = costEfficiency,
                Speed = speed,
                Safety = safety
            }
        };

        lock (_lock)
        {
            var insertIndex = _trajectories.BinarySearch(score, Comparer<TrajectoryScore>.Create((a, b) =>
                string.Compare(a.TrajectoryId, b.TrajectoryId, StringComparison.Ordinal)));
            if (insertIndex < 0)
                insertIndex = ~insertIndex;
            _trajectories.Insert(insertIndex, score);
        }

        _logger.LogDebug("Recorded trajectory {Id}: R={Reliability:F2} C={Cost:F2} S={Speed:F2} Sa={Safety:F2}",
            trajectoryId, reliability, costEfficiency, speed, safety);
    }

    public List<TrajectoryScore> GetParetoFront()
    {
        List<TrajectoryScore> snapshot;
        lock (_lock)
        {
            snapshot = new List<TrajectoryScore>(_trajectories);
        }

        foreach (var t in snapshot)
            t.IsParetoOptimal = false;

        var pareto = new List<TrajectoryScore>();
        for (var i = 0; i < snapshot.Count; i++)
        {
            var dominated = false;
            for (var j = 0; j < snapshot.Count; j++)
            {
                if (i == j) continue;
                if (snapshot[j].Fitness.Dominates(snapshot[i].Fitness))
                {
                    dominated = true;
                    break;
                }
            }

            if (!dominated)
            {
                snapshot[i].IsParetoOptimal = true;
                pareto.Add(snapshot[i]);
            }
        }

        return pareto;
    }

    public TrajectoryScore? FindBest(Dictionary<string, double>? weights = null, bool preferPareto = true)
    {
        List<TrajectoryScore> candidates;
        lock (_lock)
        {
            candidates = new List<TrajectoryScore>(_trajectories);
        }

        if (candidates.Count == 0)
            return null;

        if (preferPareto)
        {
            var pareto = GetParetoFront();
            if (pareto.Count > 0)
                candidates = pareto;
        }

        if (weights == null || weights.Count == 0)
        {
            return candidates.MaxBy(t =>
                t.Fitness.Reliability + t.Fitness.CostEfficiency + t.Fitness.Speed + t.Fitness.Safety);
        }

        return candidates.MaxBy(t =>
        {
            var score = 0.0;
            if (weights.TryGetValue("Reliability", out var w)) score += w * t.Fitness.Reliability;
            if (weights.TryGetValue("CostEfficiency", out w)) score += w * t.Fitness.CostEfficiency;
            if (weights.TryGetValue("Speed", out w)) score += w * t.Fitness.Speed;
            if (weights.TryGetValue("Safety", out w)) score += w * t.Fitness.Safety;
            return score;
        });
    }

    public List<TrajectoryScore> RecommendFor(List<string> toolSequence, int k = 3)
    {
        List<TrajectoryScore> snapshot;
        lock (_lock)
        {
            snapshot = new List<TrajectoryScore>(_trajectories);
        }

        var scored = snapshot.Select(t =>
        {
            var jaccard = JaccardSimilarity(t.ToolSequence, toolSequence);
            var prefix = PrefixMatchCount(t.ToolSequence, toolSequence) * 0.2;
            return (trajectory: t, score: jaccard + prefix);
        });

        return scored
            .OrderByDescending(x => x.score)
            .Take(k)
            .Select(x => x.trajectory)
            .ToList();
    }

    public List<TrajectoryScore> MostReliableForTools(List<string> tools, int k = 5)
    {
        List<TrajectoryScore> snapshot;
        lock (_lock)
        {
            snapshot = new List<TrajectoryScore>(_trajectories);
        }

        var toolSet = new HashSet<string>(tools);

        return snapshot
            .Where(t => t.ToolSequence.All(tool => toolSet.Contains(tool)))
            .OrderByDescending(t => t.Fitness.Reliability)
            .Take(k)
            .ToList();
    }

    public Dictionary<string, object?> GetStats()
    {
        List<TrajectoryScore> snapshot;
        lock (_lock)
        {
            snapshot = new List<TrajectoryScore>(_trajectories);
        }

        var pareto = GetParetoFront();

        var avgReliability = snapshot.Count > 0 ? snapshot.Average(t => t.Fitness.Reliability) : 0.0;
        var avgCostEfficiency = snapshot.Count > 0 ? snapshot.Average(t => t.Fitness.CostEfficiency) : 0.0;
        var avgSpeed = snapshot.Count > 0 ? snapshot.Average(t => t.Fitness.Speed) : 0.0;
        var avgSafety = snapshot.Count > 0 ? snapshot.Average(t => t.Fitness.Safety) : 0.0;

        return new Dictionary<string, object?>
        {
            ["ParetoSize"] = pareto.Count,
            ["TotalTrajectories"] = snapshot.Count,
            ["AvgFitness"] = new Dictionary<string, double>
            {
                ["Reliability"] = avgReliability,
                ["CostEfficiency"] = avgCostEfficiency,
                ["Speed"] = avgSpeed,
                ["Safety"] = avgSafety
            }
        };
    }

    internal double JaccardSimilarity(List<string> a, List<string> b)
    {
        var setA = new HashSet<string>(a);
        var setB = new HashSet<string>(b);

        var intersection = setA.Intersect(setB).Count();
        var union = setA.Union(setB).Count();

        return union == 0 ? 0.0 : (double)intersection / union;
    }

    internal int PrefixMatchCount(List<string> a, List<string> b)
    {
        var minLen = Math.Min(a.Count, b.Count);
        var count = 0;
        for (var i = 0; i < minLen; i++)
        {
            if (string.Equals(a[i], b[i], StringComparison.Ordinal))
                count++;
        }
        return count;
    }
}
