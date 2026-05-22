using System.Text.RegularExpressions;
using LTAI.Core.System;

namespace LTAI.Knowledge.Core;

public sealed record ReasoningStep(string Type, string Content, double Weight);

public sealed class ReasoningReranker
{
    private static readonly Lazy<ReasoningReranker> _instance = new(() => new ReasoningReranker());
    public static ReasoningReranker Instance => _instance.Value;

    private readonly Dictionary<string, CalibrationStats> _calibration = new();
    private int _totalReranks;

    private ReasoningReranker() { }

    public List<(T item, double score)> Rerank<T>(List<T> items, string query,
        Func<T, string> getContent, Func<T, string>? getSource = null)
    {
        Interlocked.Increment(ref _totalReranks);
        var steps = GenerateReasoningSteps(query);

        var scored = items.Select(item =>
        {
            var content = getContent(item);
            var source = getSource?.Invoke(item);
            return (item, score: ComputeMultiSignalScore(content, query, source, steps));
        }).ToList();

        scored.Sort((a, b) => b.score.CompareTo(a.score));

        var calibrated = CalibrateScores(scored.Select(s => s.score).ToList());
        return scored.Zip(calibrated, (s, c) => (s.item, c)).ToList();
    }

    public List<ReasoningStep> GenerateReasoningSteps(string query)
    {
        var steps = new List<ReasoningStep>();

        var temporal = Regex.Match(query, @"\b(\d{4})年|(\d+)月|最近|recent|past|历史");
        if (temporal.Success)
            steps.Add(new ReasoningStep("temporal", temporal.Value, 0.8));

        var causal = Regex.Match(query, @"\b(导致|引起|影响|因为|所以|because|cause|effect|impact)");
        if (causal.Success)
            steps.Add(new ReasoningStep("causal", causal.Value, 0.7));

        var entity = Regex.Match(query, @"[\u4e00-\u9fff]{2,4}|[A-Z][a-z]+");
        if (entity.Success)
            steps.Add(new ReasoningStep("entity", entity.Value, 0.6));

        var spatial = Regex.Match(query, @"\b(位置|区域|附近|坐标|where|location|area)");
        if (spatial.Success)
            steps.Add(new ReasoningStep("spatial", spatial.Value, 0.7));

        return steps;
    }

    private double ComputeMultiSignalScore(string content, string query, string? source, List<ReasoningStep> steps)
    {
        var signals = new List<double> { ComputeSemanticScore(content, query) * 0.4 };
        signals.Add(ComputeTemporalScore(content) * 0.2);
        signals.Add(ComputeSourceCredibility(source) * 0.15);
        signals.Add(ComputeStepAlignment(content, steps) * 0.15);
        signals.Add(ComputeStructureBonus(content) * 0.1);
        return signals.Sum();
    }

    private static double ComputeSemanticScore(string content, string query)
    {
        var qw = new HashSet<string>(query.ToLower().Split(' ').Where(w => w.Length > 2));
        var cw = new HashSet<string>(content.ToLower().Split(' ').Where(w => w.Length > 2));
        var intersect = qw.Intersect(cw).Count();
        return qw.Count > 0 ? (double)intersect / qw.Count : 0;
    }

    private static double ComputeTemporalScore(string content)
    {
        var recentYear = Regex.Match(content, @"\b20(2[3-6])\b");
        return recentYear.Success ? Math.Min(1.0, (int.Parse(recentYear.Groups[1].Value) - 2022) / 4.0) : 0.3;
    }

    private static double ComputeSourceCredibility(string? source)
    {
        if (source == null) return 0.5;
        var credibility = ClassificationRegistry.SourceCredibility.Classify(source);
        return credibility switch
        {
            "high" => 0.9,
            "medium" => 0.6,
            _ => 0.7
        };
    }

    private static double ComputeStepAlignment(string content, List<ReasoningStep> steps)
    {
        if (steps.Count == 0) return 0.5;
        var matches = steps.Count(s => content.Contains(s.Content, StringComparison.OrdinalIgnoreCase));
        return (double)matches / steps.Count;
    }

    private static double ComputeStructureBonus(string content)
    {
        var bonus = 0.0;
        if (Regex.IsMatch(content, @"^#+\s")) bonus += 0.2;
        if (Regex.IsMatch(content, @"\|.*\|.*\|")) bonus += 0.2;
        if (Regex.IsMatch(content, @"\d+\.\s+\w+")) bonus += 0.1;
        return Math.Min(1.0, bonus);
    }

    private List<double> CalibrateScores(List<double> scores)
    {
        if (scores.Count == 0) return scores;
        var min = scores.Min();
        var max = scores.Max();
        var range = max - min;
        return scores.Select(s => range > 0 ? 0.1 + 0.9 * (s - min) / range : 0.5).ToList();
    }

    public Dictionary<string, object> GetStats() => new()
    {
        ["total_reranks"] = _totalReranks,
        ["reasons"] = new[] { "temporal", "causal", "entity", "spatial" },
        ["calibration_scale"] = "Platt-like normalization"
    };

    private sealed record CalibrationStats(double Slope, double Intercept, int Samples);
}
