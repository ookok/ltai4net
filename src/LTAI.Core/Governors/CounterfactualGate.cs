using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.Governors;

public sealed record SemanticBehaviorVector
{
    public Dictionary<string, double> RouteDistribution { get; init; } = new();
    public int TotalSamples { get; init; }
    public DateTime ComputedAt { get; init; } = DateTime.UtcNow;
}

public sealed record CounterfactualResult
{
    public bool Passed { get; init; }
    public string Reason { get; init; } = "";
    public double RegretScore { get; init; }
    public double DistributionShift { get; init; }
    public SemanticBehaviorVector? OriginalBehavior { get; init; }
    public SemanticBehaviorVector? ShadowBehavior { get; init; }
    public TimeSpan Elapsed { get; init; }
    public List<string> Regressions { get; init; } = new();
}

public sealed class CounterfactualGate
{
    private readonly ILogger<CounterfactualGate> _logger;
    private readonly List<(string Query, string ExpectedRoute)> _testBatch = new();
    private readonly double _regretThreshold;
    private readonly double _shiftThreshold;
    private readonly Func<string, float[]>? _embedder;
    private const int MaxTestBatch = 50;

    public CounterfactualGate(
        double regretThreshold = 0.15,
        double shiftThreshold = 0.25,
        Func<string, float[]>? embedder = null,
        ILogger<CounterfactualGate>? logger = null)
    {
        _regretThreshold = regretThreshold;
        _shiftThreshold = shiftThreshold;
        _embedder = embedder;
        _logger = logger ?? NullLogger<CounterfactualGate>.Instance;
    }

    public void SeedTestBatch(IEnumerable<(string Query, string Route)> samples)
    {
        foreach (var sample in samples)
        {
            if (_testBatch.Count >= MaxTestBatch) break;
            _testBatch.Add(sample);
        }
        _logger.LogInformation("Counterfactual gate: seeded with {Count} test samples", _testBatch.Count);
    }

    public bool HasRealEmbeddings => _embedder != null;

    public CounterfactualResult Evaluate(
        ParetoRouter originalRouter,
        ParetoRouter shadowRouter,
        int sampleSize = 20)
    {
        var sw = global::System.Diagnostics.Stopwatch.StartNew();

        var originalDist = new Dictionary<string, double>();
        var shadowDist = new Dictionary<string, double>();
        var regressions = new List<string>();
        var sampleCount = Math.Min(sampleSize, _testBatch.Count);
        double totalConfidenceDrop = 0;
        int disagreeCount = 0;

        for (var i = 0; i < sampleCount; i++)
        {
            var (query, expectedRoute) = _testBatch[i];
            var embedding = _embedder != null
                ? _embedder(query)
                : HashEmbed(query, 768);

            var origDecision = originalRouter.Decide(embedding);
            var shadowDecision = shadowRouter.Decide(embedding);

            originalDist[origDecision.Route] = originalDist.GetValueOrDefault(origDecision.Route) + 1;
            shadowDist[shadowDecision.Route] = shadowDist.GetValueOrDefault(shadowDecision.Route) + 1;

            if (origDecision.Route != shadowDecision.Route)
            {
                disagreeCount++;
                if (origDecision.Route == expectedRoute &&
                    shadowDecision.Route != expectedRoute)
                {
                    regressions.Add(query[..Math.Min(query.Length, 80)]);
                }

                totalConfidenceDrop += Math.Max(0, origDecision.Confidence - shadowDecision.Confidence);
            }
        }

        var originalBehavior = new SemanticBehaviorVector
        {
            RouteDistribution = Normalize(originalDist, sampleCount),
            TotalSamples = sampleCount
        };

        var shadowBehavior = new SemanticBehaviorVector
        {
            RouteDistribution = Normalize(shadowDist, sampleCount),
            TotalSamples = sampleCount
        };

        var shift = ComputeDistributionShift(originalBehavior.RouteDistribution, shadowBehavior.RouteDistribution);
        var avgConfDrop = disagreeCount > 0 ? totalConfidenceDrop / disagreeCount : 0;
        var regret = (shift * 0.6) + (avgConfDrop * 0.3) + ((double)regressions.Count / Math.Max(sampleCount, 1) * 0.1);

        sw.Stop();

        var passed = shift <= _shiftThreshold && regret <= _regretThreshold && regressions.Count < sampleCount * 0.1;

        var result = new CounterfactualResult
        {
            Passed = passed,
            Reason = passed
                ? $"Passed: shift={shift:F3}, regret={regret:F3}"
                : $"BLOCKED: shift={shift:F3}, regret={regret:F3}, regressions={regressions.Count}",
            RegretScore = regret,
            DistributionShift = shift,
            OriginalBehavior = originalBehavior,
            ShadowBehavior = shadowBehavior,
            Elapsed = sw.Elapsed,
            Regressions = regressions
        };

        _logger.LogInformation("Counterfactual evaluation: {Result}", result.Reason);

        return result;
    }

    public bool TrySnapshot(ParetoRouter router, out List<ParetoPoint> snapshot)
    {
        snapshot = new List<ParetoPoint>();
        try
        {
            var front = router.GetFrontier();
            foreach (var p in front)
            {
                snapshot.Add(p with { });
            }
            return snapshot.Count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to snapshot ParetoRouter");
            return false;
        }
    }

    public ParetoRouter CloneRouter(ParetoRouter source)
    {
        var clone = new ParetoRouter(
            embeddingDim: 768,
            metric: ParetoDistanceMetric.Cosine);

        var front = source.GetFrontier();
        foreach (var point in front)
            clone.AddFrontierPoint(point with { });

        return clone;
    }

    private static double ComputeDistributionShift(
        Dictionary<string, double> distA,
        Dictionary<string, double> distB)
    {
        var allKeys = new HashSet<string>(distA.Keys.Concat(distB.Keys));
        double klAB = 0, klBA = 0;

        foreach (var key in allKeys)
        {
            var p = distA.GetValueOrDefault(key, 0.01);
            var q = distB.GetValueOrDefault(key, 0.01);
            klAB += p * Math.Log(p / q);
            klBA += q * Math.Log(q / p);
        }

        var jsDiv = (klAB + klBA) / 2;
        return Math.Max(0, Math.Min(1, jsDiv / Math.Log(2)));
    }

    private static Dictionary<string, double> Normalize(Dictionary<string, double> counts, int total)
    {
        var result = new Dictionary<string, double>();
        foreach (var (key, count) in counts)
            result[key] = count / total;
        return result;
    }

    private static float[] HashEmbed(string text, int dim)
    {
        var emb = new float[dim];
        var bytes = global::System.Text.Encoding.UTF8.GetBytes(text);
        for (var i = 0; i < Math.Min(bytes.Length, dim); i++)
            emb[i] = bytes[i] / 255f;
        return emb;
    }
}
