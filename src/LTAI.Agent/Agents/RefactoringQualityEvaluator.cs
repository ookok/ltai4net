using System.Text.RegularExpressions;

namespace LTAI.Agent.Agents;

public sealed class RefactoringMetrics
{
    public int LinesAdded { get; set; }
    public int LinesRemoved { get; set; }
    public int FilesChanged { get; set; }
    public int InterfacesExtracted { get; set; }
    public int MethodsExtracted { get; set; }
    public int DuplicationsRemoved { get; set; }
    public int DeadCodeRemoved { get; set; }
    public int NewTests { get; set; }
    public int ComplexityReductionScore { get; set; }
    public double BehavioralEquivalenceConfidence { get; set; }
    public List<string> RiskItems { get; set; } = new();
}

public sealed class RefactoringEvaluation
{
    public RefactoringMetrics Metrics { get; init; } = new();
    public double OverallScore { get; init; }
    public string Grade { get; init; } = "";
    public string Summary { get; init; } = "";
    public List<string> Suggestions { get; init; } = new();
}

public static class RefactoringQualityEvaluator
{
    public static RefactoringEvaluation Evaluate(string beforeCode, string afterCode, string? diffOutput = null)
    {
        var metrics = ComputeMetrics(beforeCode, afterCode, diffOutput);
        var score = ComputeScore(metrics);
        var suggestions = GenerateSuggestions(metrics);

        return new RefactoringEvaluation
        {
            Metrics = metrics,
            OverallScore = Math.Clamp(score, 0.0, 1.0),
            Grade = score switch { >= 0.9 => "A", >= 0.75 => "B", >= 0.6 => "C", >= 0.4 => "D", _ => "F" },
            Summary = GenerateSummary(metrics, score),
            Suggestions = suggestions
        };
    }

    private static RefactoringMetrics ComputeMetrics(string before, string after, string? diff)
    {
        var beforeLines = before.Split('\n');
        var afterLines = after.Split('\n');

        var metrics = new RefactoringMetrics
        {
            LinesAdded = Math.Max(0, afterLines.Length - beforeLines.Length),
            LinesRemoved = Math.Max(0, beforeLines.Length - afterLines.Length),
            FilesChanged = 1
        };

        metrics.InterfacesExtracted = CountNew(after, before, @"interface\s+I\w+");
        metrics.MethodsExtracted = CountNew(after, before, @"(?:private|protected)\s+(?:static\s+)?\w+\s+\w+\s*\(");
        metrics.DuplicationsRemoved = Math.Max(0, CountOccurrences(before, @"(.{50,})\n.*\1") - CountOccurrences(after, @"(.{50,})\n.*\1"));
        metrics.DeadCodeRemoved = CountRemoved(before, after, @"\/\/\s*(?:TODO|FIXME|HACK|DEPRECATED)|#region|Console\.WriteLine|Debug\.Write");
        metrics.NewTests = CountNew(after, before, @"\[Fact\]|\[Test\]|def test_|it\(.*=>");
        metrics.ComplexityReductionScore = ComplexityReduction(before, after);

        if (diff != null)
        {
            metrics.LinesAdded = Regex.Matches(diff, @"^\+[^+]").Count;
            metrics.LinesRemoved = Regex.Matches(diff, @"^\-[^-]").Count;
            metrics.FilesChanged = Regex.Matches(diff, @"^diff --git").Count;
        }

        metrics.BehavioralEquivalenceConfidence = EstimateBehavioralEquivalence(before, after);
        metrics.RiskItems = IdentifyRisks(before, after);

        return metrics;
    }

    private static double ComputeScore(RefactoringMetrics m)
    {
        double score = 0.5;

        if (m.InterfacesExtracted > 0) score += 0.1;
        if (m.MethodsExtracted > 1) score += 0.1;
        if (m.DuplicationsRemoved > 0) score += 0.1;
        if (m.DeadCodeRemoved > 0) score += 0.05;
        if (m.NewTests > 0) score += 0.1;
        if (m.ComplexityReductionScore > 0) score += 0.1 * Math.Min(m.ComplexityReductionScore, 2);

        score -= m.RiskItems.Count * 0.1;

        return Math.Clamp(score, 0.0, 1.0);
    }

    private static int CountNew(string after, string before, string pattern)
    {
        var afterCount = Regex.Matches(after, pattern, RegexOptions.IgnoreCase).Count;
        var beforeCount = Regex.Matches(before, pattern, RegexOptions.IgnoreCase).Count;
        return Math.Max(0, afterCount - beforeCount);
    }

    private static int CountRemoved(string before, string after, string pattern)
    {
        var beforeCount = Regex.Matches(before, pattern, RegexOptions.IgnoreCase).Count;
        var afterCount = Regex.Matches(after, pattern, RegexOptions.IgnoreCase).Count;
        return Math.Max(0, beforeCount - afterCount);
    }

    private static int CountOccurrences(string text, string pattern)
    {
        return Regex.Matches(text, pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase).Count;
    }

    private static int ComplexityReduction(string before, string after)
    {
        // Cyclomatic complexity proxy: count branches (if/for/while/switch/case/&&/||)
        int CountBranches(string code) =>
            Regex.Matches(code, @"\bif\b|\bfor\b|\bwhile\b|\bcase\b|\&\&|\|\|", RegexOptions.IgnoreCase).Count;

        var beforeComplexity = CountBranches(before);
        var afterComplexity = CountBranches(after);
        return Math.Max(0, beforeComplexity - afterComplexity);
    }

    private static double EstimateBehavioralEquivalence(string before, string after)
    {
        // Heuristic: public API surface should be similar
        var beforeSignatures = Regex.Matches(before,
            @"public\s+(?:static\s+)?(?:\w+[<>\w, ]*\s+)(\w+)\s*\([^)]*\)", RegexOptions.IgnoreCase);
        var afterSignatures = Regex.Matches(after,
            @"public\s+(?:static\s+)?(?:\w+[<>\w, ]*\s+)(\w+)\s*\([^)]*\)", RegexOptions.IgnoreCase);

        if (beforeSignatures.Count == 0) return 0.5;

        var beforeNames = beforeSignatures.Select(m => m.Groups[1].Value).ToHashSet();
        var afterNames = afterSignatures.Select(m => m.Groups[1].Value).ToHashSet();

        var preserved = beforeNames.Intersect(afterNames).Count();
        return (double)preserved / beforeNames.Count;
    }

    private static List<string> IdentifyRisks(string before, string after)
    {
        var risks = new List<string>();

        var beforePublic = Regex.Matches(before, @"public\s+\w+\s+\w+\s*\(", RegexOptions.IgnoreCase).Count;
        var afterPublic = Regex.Matches(after, @"public\s+\w+\s+\w+\s*\(", RegexOptions.IgnoreCase).Count;
        if (afterPublic < beforePublic - 2)
            risks.Add($"{beforePublic - afterPublic} public methods removed — breaking API change possible");

        if (Regex.IsMatch(after, @"throw\s+new\s+NotImplementedException"))
            risks.Add("NotImplementedException left in code — incomplete refactoring");

        if (before.Contains("internal") && !after.Contains("internal"))
            risks.Add("internal access modifiers removed — may expose implementation details");

        return risks;
    }

    private static string GenerateSummary(RefactoringMetrics m, double score)
    {
        var parts = new List<string>();

        if (m.InterfacesExtracted > 0) parts.Add($"extracted {m.InterfacesExtracted} interface(s)");
        if (m.MethodsExtracted > 1) parts.Add($"extracted {m.MethodsExtracted} method(s)");
        if (m.DuplicationsRemoved > 0) parts.Add("reduced code duplication");
        if (m.DeadCodeRemoved > 0) parts.Add("removed dead code");
        if (m.NewTests > 0) parts.Add($"added {m.NewTests} test(s)");
        if (m.ComplexityReductionScore > 0) parts.Add($"reduced complexity by {m.ComplexityReductionScore} branches");

        if (m.RiskItems.Count > 0) parts.Add($"{m.RiskItems.Count} risk item(s) identified");

        return parts.Count > 0
            ? $"Refactoring score {score:F2}: {string.Join(", ", parts)}."
            : $"Refactoring score {score:F2}: minimal changes detected.";
    }

    private static List<string> GenerateSuggestions(RefactoringMetrics m)
    {
        var suggestions = new List<string>();

        if (m.InterfacesExtracted == 0 && m.LinesAdded + m.LinesRemoved > 50)
            suggestions.Add("Consider extracting interfaces to improve abstraction and testability.");

        if (m.NewTests == 0 && m.LinesAdded + m.LinesRemoved > 30)
            suggestions.Add("Add tests to verify functional equivalence after refactoring.");

        if (m.DuplicationsRemoved == 0 && m.LinesAdded > 100)
            suggestions.Add("Look for opportunities to extract duplicated code into shared methods.");

        if (m.BehavioralEquivalenceConfidence < 0.7)
            suggestions.Add("Low behavioral equivalence confidence — run regression tests before merging.");

        return suggestions;
    }

    public static string FormatReport(RefactoringEvaluation eval)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## Refactoring Quality Report — {eval.OverallScore:F2} ({eval.Grade})");
        sb.AppendLine();
        sb.AppendLine($"### Metrics");
        sb.AppendLine($"- Lines: +{eval.Metrics.LinesAdded} -{eval.Metrics.LinesRemoved}, {eval.Metrics.FilesChanged} file(s)");
        sb.AppendLine($"- Interfaces extracted: {eval.Metrics.InterfacesExtracted}");
        sb.AppendLine($"- Methods extracted: {eval.Metrics.MethodsExtracted}");
        sb.AppendLine($"- Duplications reduced: {eval.Metrics.DuplicationsRemoved}");
        sb.AppendLine($"- Dead code removed: {eval.Metrics.DeadCodeRemoved}");
        sb.AppendLine($"- New tests: {eval.Metrics.NewTests}");
        sb.AppendLine($"- Complexity reduction: {eval.Metrics.ComplexityReductionScore} branches");
        sb.AppendLine($"- Behavioral equivalence: {eval.Metrics.BehavioralEquivalenceConfidence:F2}");
        sb.AppendLine();
        sb.AppendLine($"### Summary: {eval.Summary}");

        if (eval.Metrics.RiskItems.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Risk Items");
            foreach (var r in eval.Metrics.RiskItems)
                sb.AppendLine($"- ⚠️ {r}");
        }

        if (eval.Suggestions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Suggestions");
            foreach (var s in eval.Suggestions)
                sb.AppendLine($"- 💡 {s}");
        }

        return sb.ToString();
    }
}
