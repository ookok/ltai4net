using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed record AbTestVariant
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public Dictionary<string, string> Configuration { get; init; } = new();
    public int AssignedUsers { get; init; }
    public int TotalQueries { get; init; }
    public int SuccessfulQueries { get; init; }
    public double AverageLatencyMs { get; init; }
    public double AverageCostYuan { get; init; }
    public float AverageSatisfaction { get; init; }
}

public sealed record AbTestResult
{
    public string TestName { get; init; } = "";
    public string Winner { get; init; } = "";
    public bool StatisticallySignificant { get; init; }
    public Dictionary<string, object> Metrics { get; init; } = new();
    public string Recommendation { get; init; } = "";
}

public sealed class AbTestingFramework
{
    private readonly ConcurrentDictionary<string, AbTestVariant> _variants = new();
    private readonly ConcurrentDictionary<string, string> _userAssignments = new();
    private readonly ConcurrentDictionary<string, List<Dictionary<string, object>>> _results = new();
    private readonly ILogger<AbTestingFramework> _logger;
    private readonly Random _random = new();

    public AbTestingFramework(ILogger<AbTestingFramework>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AbTestingFramework>.Instance;
    }

    public void CreateTest(string testName, List<AbTestVariant> variants)
    {
        foreach (var variant in variants)
        {
            _variants[$"{testName}:{variant.Name}"] = variant;
        }
        _results[testName] = new();
        _logger.LogInformation("A/B test created: {TestName} with {Count} variants", testName, variants.Count);
    }

    public string AssignUser(string userId, string testName)
    {
        var assignmentKey = $"{userId}:{testName}";
        if (_userAssignments.TryGetValue(assignmentKey, out var existing))
            return existing;

        var variantKeys = _variants.Keys.Where(k => k.StartsWith($"{testName}:")).ToList();
        if (variantKeys.Count == 0)
            return "control";

        var assigned = variantKeys[_random.Next(variantKeys.Count)];
        var variantName = assigned.Split(':')[1];

        _userAssignments[assignmentKey] = variantName;

        var variant = _variants[assigned];
        _variants[assigned] = variant with { AssignedUsers = variant.AssignedUsers + 1 };

        return variantName;
    }

    public string GetVariant(string userId, string testName)
    {
        var assignmentKey = $"{userId}:{testName}";
        return _userAssignments.GetValueOrDefault(assignmentKey, "control");
    }

    public void RecordResult(string testName, string variantName, Dictionary<string, object> metrics)
    {
        var key = $"{testName}:{variantName}";
        if (!_variants.ContainsKey(key)) return;

        _results[testName].Add(new Dictionary<string, object>(metrics) { ["variant"] = variantName, ["timestamp"] = DateTime.UtcNow });

        var variant = _variants[key];
        _variants[key] = variant with
        {
            TotalQueries = variant.TotalQueries + 1,
            SuccessfulQueries = variant.SuccessfulQueries + (Convert.ToBoolean(metrics.GetValueOrDefault("success", false)) ? 1 : 0),
            AverageLatencyMs = ComputeRunningAverage(variant.AverageLatencyMs, variant.TotalQueries, Convert.ToDouble(metrics.GetValueOrDefault("latency_ms", 0.0))),
            AverageCostYuan = ComputeRunningAverage(variant.AverageCostYuan, variant.TotalQueries, Convert.ToDouble(metrics.GetValueOrDefault("cost_yuan", 0.0))),
            AverageSatisfaction = ComputeRunningAverageFloat(variant.AverageSatisfaction, variant.TotalQueries, Convert.ToSingle(metrics.GetValueOrDefault("satisfaction", 0.0f)))
        };
    }

    public AbTestResult AnalyzeTest(string testName)
    {
        var variantKeys = _variants.Keys.Where(k => k.StartsWith($"{testName}:")).ToList();
        if (variantKeys.Count < 2)
            return new AbTestResult { TestName = testName, Recommendation = "Not enough variants" };

        var variants = variantKeys.Select(k => _variants[k]).ToList();
        var winner = variants.OrderByDescending(v => v.AverageSatisfaction).First();

        var isSignificant = variants.All(v => v.TotalQueries >= 30);

        var metrics = new Dictionary<string, object>
        {
            ["variants"] = variants.Select(v => new { v.Name, v.TotalQueries, v.AverageSatisfaction, v.AverageLatencyMs, v.AverageCostYuan }).ToList(),
            ["winner"] = winner.Name,
            ["statistically_significant"] = isSignificant
        };

        return new AbTestResult
        {
            TestName = testName,
            Winner = winner.Name,
            StatisticallySignificant = isSignificant,
            Metrics = metrics,
            Recommendation = isSignificant
                ? $"Variant '{winner.Name}' wins with satisfaction={winner.AverageSatisfaction:F2}, latency={winner.AverageLatencyMs:F0}ms, cost={winner.AverageCostYuan:F4}yuan"
                : "Need more data (minimum 30 queries per variant)"
        };
    }

    public Dictionary<string, object> GetTestStats(string testName)
    {
        var variantKeys = _variants.Keys.Where(k => k.StartsWith($"{testName}:")).ToList();
        var variants = variantKeys.Select(k => _variants[k]).ToList();

        return new Dictionary<string, object>
        {
            ["test_name"] = testName,
            ["variant_count"] = variants.Count,
            ["total_queries"] = variants.Sum(v => v.TotalQueries),
            ["variants"] = variants.Select(v => new
            {
                v.Name,
                v.AssignedUsers,
                v.TotalQueries,
                SuccessRate = v.TotalQueries > 0 ? (float)v.SuccessfulQueries / v.TotalQueries : 0f,
                v.AverageSatisfaction,
                v.AverageLatencyMs,
                v.AverageCostYuan
            }).ToList()
        };
    }

    private static double ComputeRunningAverage(double currentAvg, int count, double newValue)
    {
        if (count == 0) return newValue;
        return (currentAvg * count + newValue) / (count + 1);
    }

    private static float ComputeRunningAverageFloat(float currentAvg, int count, float newValue)
    {
        if (count == 0) return newValue;
        return (currentAvg * count + newValue) / (count + 1);
    }
}
