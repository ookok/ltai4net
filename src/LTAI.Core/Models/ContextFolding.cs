using System.Text.Json;
using System.Text.RegularExpressions;

namespace LTAI.Core.Models;

public record FoldResult(
    int OriginalLength, int FoldedLength, string Summary,
    List<string>? KeyEntities = null, List<string>? Decisions = null,
    List<string>? ActionItems = null, double Confidence = 0.8)
{
    public double CompressionRatio =>
        Math.Round((double)FoldedLength / Math.Max(OriginalLength, 1), 3);

    public string ToContextBlock()
    {
        var parts = new List<string> { Summary };
        if (KeyEntities is { Count: > 0 })
            parts.Add($"关键信息: {string.Join(", ", KeyEntities.Take(4))}");
        if (Decisions is { Count: > 0 })
            parts.Add($"决策: {string.Join(", ", Decisions.Take(2))}");
        if (ActionItems is { Count: > 0 })
            parts.Add($"待办: {string.Join(", ", ActionItems.Take(2))}");
        return string.Join("\n", parts);
    }
}

public record BudgetSegment(
    string Name, string Content,
    double RelevanceScore = 0.0, int AllocatedChars = 0,
    string FoldedContent = "", double DepthScore = 0.5,
    int PositionIndex = -1, double KvPreservationScore = 0.5)
{
    public int OriginalChars => Content.Length;
}

public record BudgetAllocation(
    List<BudgetSegment> Segments, int TotalBudget = 0,
    int TotalOriginal = 0, int TotalAllocated = 0)
{
    public double CompressionRatio =>
        Math.Round((double)TotalAllocated / Math.Max(TotalOriginal, 1), 3);

    public string BuildContext() =>
        string.Join("\n\n", Segments.Select(s =>
            $"<!-- [{s.Name}] relevance={s.RelevanceScore:F2} budget={s.AllocatedChars}chars -->\n{s.FoldedContent}"));
}

public static class ContextFolding
{
    static readonly string[] KeyPrefixes =
    {
        "结论", "总结", "关键", "重要", "Decision:", "Action:",
        "结果", "错误", "Error:", "Result:", "Summary:", "TL;DR",
        "核心", "决定", "输出", "TLDR"
    };

    public static FoldResult FoldContext(string content, string domain = "general", int maxChars = 500) =>
        HeuristicFold(content, maxChars);

    public static string FoldTextHeuristic(string content, int maxChars = 500) =>
        HeuristicFold(content, maxChars).Summary;

    public static FoldResult HeuristicFold(string content, int maxChars)
    {
        var originalLen = content.Length;
        if (originalLen <= maxChars)
            return new(originalLen, originalLen, content.Trim(), Confidence: 1.0);

        var lines = content.Trim().Split('\n')
            .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        var summaryParts = new List<string>();

        if (lines.Count > 0)
        {
            var firstPara = lines[0];
            if (firstPara.Length > maxChars / 2)
                summaryParts.Add(firstPara[..(maxChars / 2)] + "...");
            else
                summaryParts.Add(firstPara);
        }

        var totalLen = summaryParts.Sum(p => p.Length);
        foreach (var line in lines.Skip(1))
        {
            if (totalLen >= maxChars) break;
            var matched = KeyPrefixes.Any(p => line.StartsWith(p, StringComparison.OrdinalIgnoreCase));
            if (matched || Regex.IsMatch(line, @"^[-*•#]"))
            {
                var part = line.Length > 200 ? line[..200] : line;
                summaryParts.Add(part);
                totalLen += part.Length;
            }
        }

        var summary = string.Join(" ", summaryParts);
        if (summary.Length > maxChars)
            summary = summary[..(maxChars - 3)] + "...";

        var entities = new List<string>();
        // Numeric values with units
        foreach (Match m in Regex.Matches(content, @"\d+\.?\d*\s*(?:吨|mg|dB|km|m³|万元|%)"))
            if (entities.Count < 5) entities.Add(m.Value);
        // Codes like ABC-1234
        foreach (Match m in Regex.Matches(content, @"[A-Z]{2,6}[- ]\d{2,4}"))
            if (entities.Count < 5) entities.Add(m.Value);
        // Chinese titles 《...》
        foreach (Match m in Regex.Matches(content, @"《[^》]+》"))
            if (entities.Count < 5) entities.Add(m.Value);

        return new(originalLen, summary.Length, summary,
            KeyEntities: entities.Distinct().Take(5).ToList(),
            Confidence: 0.3);
    }

    public static double ScoreSegmentRelevance(string segment, string query)
    {
        if (string.IsNullOrEmpty(segment) || string.IsNullOrEmpty(query)) return 0.0;

        var qWords = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var sLower = segment.ToLowerInvariant();
        var termHits = qWords.Count(w => sLower.Contains(w));
        var termDensity = (double)termHits / Math.Max(qWords.Count, 1);

        double structuralBonus = 0;
        if (Regex.IsMatch(segment, @"(?:import|from|class|def|function|using|namespace)\s"))
            structuralBonus += 0.10;
        if (Regex.IsMatch(segment, @"(?:错误|Error|error|异常|Exception|failed|Failed)"))
            structuralBonus += 0.15;
        if (Regex.IsMatch(segment, @"(?:决策|决定|Decision|action|下一步|Action)"))
            structuralBonus += 0.15;
        if (segment.Length < 200)
            structuralBonus += 0.10;

        return Math.Min(termDensity + structuralBonus, 1.0);
    }

    public static double RopeRelativeScore(int posA, int posB, int numScales = 8)
    {
        var relDist = Math.Abs(posA - posB);
        double total = 0;
        for (int k = 0; k < numScales; k++)
        {
            var theta = Math.Pow(10000.0, -2.0 * k / numScales);
            total += Math.Cos(relDist * theta * 0.1);
        }
        return (total / numScales + 1.0) / 2.0;
    }

    public static BudgetAllocation RouteAttentionBudget(
        Dictionary<string, string> segments, string query,
        int totalBudget = 8000,
        Dictionary<string, double>? depthScores = null,
        Dictionary<string, int>? positionIndices = null,
        Dictionary<string, double>? taskCriticalities = null)
    {
        var ds = depthScores ?? new();
        var pi = positionIndices ?? new();
        var tc = taskCriticalities ?? new();
        var names = segments.Keys.ToList();
        int n = names.Count;
        var centerPos = n / 2;

        var scored = new List<(string name, string content, double combinedScore, double depthScore,
            int posIdx, double kvPreservation, double midBoost)>();

        for (int idx = 0; idx < names.Count; idx++)
        {
            var name = names[idx];
            var content = segments[name];
            var baseScore = ScoreSegmentRelevance(content, query);
            var depthBonus = ds.GetValueOrDefault(name, 0.5);
            var posIdx = pi.GetValueOrDefault(name, idx);
            var criticality = tc.GetValueOrDefault(name, 0.3);
            var positionScore = RopeRelativeScore(posIdx, centerPos);

            double midBoost = 1.0;
            if (n > 10 && idx >= n / 3 && idx <= 2 * n / 3 && criticality > 0.6)
                midBoost = 1.3;

            var kvPreservation = Math.Round(
                baseScore * 0.30 + depthBonus * 0.20 + criticality * 0.25 + positionScore * 0.25, 3);

            var combinedScore = Math.Round(
                baseScore * 0.35 + depthBonus * 0.15 + kvPreservation * 0.50, 3);

            scored.Add((name, content, combinedScore, depthBonus, posIdx, kvPreservation, midBoost));
        }

        scored.Sort((a, b) => b.combinedScore.CompareTo(a.combinedScore));
        var totalScore = scored.Sum(s => s.combinedScore);
        if (totalScore <= 0) totalScore = scored.Count;

        var allocation = new BudgetAllocation(new(), totalBudget);
        foreach (var (name, content, combinedScore, depthBonus, posIdx, kvPreservation, midBoost) in scored)
        {
            var rawBudget = (int)(totalBudget * (combinedScore / totalScore));
            var boostedBudget = (int)(rawBudget * midBoost);
            var budget = Math.Min(boostedBudget, totalBudget / 2);

            var seg = new BudgetSegment(
                name, content,
                RelevanceScore: Math.Round(combinedScore, 3),
                AllocatedChars: budget,
                DepthScore: Math.Round(depthBonus, 3),
                PositionIndex: posIdx,
                KvPreservationScore: Math.Round(kvPreservation * midBoost, 3));

            if (seg.AllocatedChars > 0 && content.Length > seg.AllocatedChars)
            {
                seg = seg with { FoldedContent = FoldTextHeuristic(content, seg.AllocatedChars) };
                allocation = allocation with { TotalAllocated = allocation.TotalAllocated + seg.AllocatedChars };
            }
            else if (seg.AllocatedChars > 0)
            {
                seg = seg with { FoldedContent = content };
                allocation = allocation with { TotalAllocated = allocation.TotalAllocated + content.Length };
            }
            else
            {
                seg = seg with { FoldedContent = FoldTextHeuristic(content, 100) };
                allocation = allocation with { TotalAllocated = allocation.TotalAllocated + 100 };
            }

            allocation.Segments.Add(seg);
            allocation = allocation with
            {
                TotalOriginal = allocation.TotalOriginal + content.Length,
                TotalBudget = totalBudget
            };
        }

        return allocation;
    }
}
