using System.Text;

namespace LTAI.TreeLLM.Heima;

public sealed class DecodedStep
{
    public string Entity { get; set; } = "";
    public string Relation { get; set; } = "";
    public string StepType { get; set; } = "";
    public double Confidence { get; set; }
    public string ReconstructedText { get; set; } = "";
    public double ReconstructionScore { get; set; }
}

public sealed class HeimaDecoder
{
    private readonly Dictionary<string, string> _stepTemplates = new()
    {
        ["observation"] = "Looking at the {entity}, we observe that it {relation} certain properties.",
        ["premise"] = "Given the premise that {entity} {relation} the situation, we reason that:",
        ["procedure"] = "Step: we process {entity} by verifying that it {relation} the expected criteria.",
        ["assumption"] = "We assume that {entity} {relation} valid based on contextual evidence.",
        ["example"] = "For example, {entity} demonstrates how {relation} applies in practice.",
        ["conclusion"] = "Therefore, {entity} leads us to conclude that it {relation} the result.",
        ["default"] = "{entity}: {relation}"
    };

    private readonly HeimaConfig _config;

    public HeimaDecoder(HeimaConfig? config = null) => _config = config ?? new();

    public List<DecodedStep> Decode(List<ThinkingToken> tokens)
    {
        var steps = new List<DecodedStep>();
        foreach (var token in tokens.OrderByDescending(t => t.Importance))
        {
            var template = _stepTemplates.GetValueOrDefault(token.StepType, _stepTemplates["default"]);
            var text = template
                .Replace("{entity}", token.KeyEntity)
                .Replace("{relation}", token.Relation);

            steps.Add(new DecodedStep
            {
                Entity = token.KeyEntity,
                Relation = token.Relation,
                StepType = token.StepType,
                Confidence = token.Confidence,
                ReconstructedText = text,
                ReconstructionScore = token.MutualInfo * token.Confidence
            });
        }
        return steps;
    }

    public string DecodeToText(List<ThinkingToken> tokens, bool includeConfidence = false)
    {
        var steps = Decode(tokens);
        var sb = new StringBuilder();

        var grouped = steps.GroupBy(s => s.StepType);
        foreach (var group in grouped.Where(g => g.Key != "observation"))
        {
            foreach (var step in group.OrderByDescending(s => s.ReconstructionScore).Take(3))
            {
                sb.AppendLine(step.ReconstructedText);
                if (includeConfidence)
                    sb.AppendLine($"  [confidence: {step.Confidence:F2}, score: {step.ReconstructionScore:F2}]");
            }
        }

        var observations = steps.Where(s => s.StepType == "observation").ToList();
        if (observations.Count > 0)
        {
            sb.AppendLine("\nObservations:");
            foreach (var obs in observations.OrderByDescending(o => o.ReconstructionScore).Take(5))
                sb.AppendLine($"- {obs.ReconstructedText}");
        }

        return sb.ToString().Trim();
    }

    public string DecodeToCompactText(List<ThinkingToken> tokens)
    {
        var steps = Decode(tokens);
        var conclusions = steps.Where(s => s.StepType == "conclusion").ToList();
        return conclusions.Count > 0
            ? string.Join("; ", conclusions.OrderByDescending(c => c.Confidence).Take(3).Select(c => $"{c.Entity} {c.Relation} (confidence:{c.Confidence:F2})"))
            : string.Join("; ", steps.OrderByDescending(s => s.ReconstructionScore).Take(5).Select(s => $"{s.Entity}:{s.Relation}"));
    }

    public Dictionary<string, double> ComputeReconstructionQuality(List<ThinkingToken> tokens, string? originalText = null)
    {
        var steps = Decode(tokens);
        var completeness = tokens.Count > 0
            ? tokens.Average(t => t.MutualInfo)
            : 0;
        var avgConfidence = steps.Count > 0
            ? steps.Average(s => s.ReconstructionScore)
            : 0;

        var quality = new Dictionary<string, double>
        {
            ["completeness"] = Math.Round(completeness, 3),
            ["avg_confidence"] = Math.Round(avgConfidence, 3),
            ["tokens_decoded"] = steps.Count,
            ["information_retention"] = Math.Round(steps.Count > 0 ? steps.Average(s => s.ReconstructionScore) : 0, 3)
        };

        if (originalText != null)
        {
            var originalEntities = System.Text.RegularExpressions.Regex.Matches(originalText, @"[\u4e00-\u9fff]{2,8}|\b[A-Z][a-z]+\b");
            var decodedEntities = new HashSet<string>(steps.Select(s => s.Entity));
            var overlap = originalEntities.Cast<System.Text.RegularExpressions.Match>().Count(m => decodedEntities.Contains(m.Value));
            var recall = originalEntities.Count > 0 ? (double)overlap / originalEntities.Count : 0;
            quality["entity_recall"] = Math.Round(recall, 3);
        }

        return quality;
    }

    public string GenerateInfoGapReport(List<ThinkingToken> tokens, int originalTokens)
    {
        var compressedTokens = tokens.Sum(t => t.CompressedSize) / 4;
        var ratio = (double)compressedTokens / Math.Max(1, originalTokens) * 100;
        var avgMI = tokens.Count > 0 ? tokens.Average(t => t.MutualInfo) : 0;

        return $"Heima Compression: {originalTokens}→{compressedTokens} tokens ({ratio:F1}%), " +
               $"Avg Mutual Info: {avgMI:F3}, " +
               $"Info Gap: {(1 - avgMI) * 100:F1}%, " +
               $"Verdict: {(avgMI > 0.7 ? "EFFICIENT (gap < 30%)" : avgMI > 0.4 ? "ACCEPTABLE (gap 30-60%)" : "LOSSY (gap > 60%)")}";
    }
}
