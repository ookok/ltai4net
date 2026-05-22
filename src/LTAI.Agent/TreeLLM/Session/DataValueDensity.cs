using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using LTAI.Agent.Models;

namespace LTAI.Agent.Session;

public sealed class DataValueDensity
{
    private readonly ILogger<DataValueDensity>? _logger;
    private readonly ConcurrentDictionary<string, int> _seenHashes = new();
    private readonly ConcurrentDictionary<string, int> _successFeatures = new();
    private readonly ConcurrentDictionary<string, int> _failureFeatures = new();

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "is", "are", "was", "were", "be", "been", "being",
        "has", "have", "had", "do", "does", "did", "will", "would", "shall",
        "should", "may", "might", "can", "could", "in", "on", "at", "to",
        "for", "of", "with", "by", "from", "it", "its", "and", "or", "but",
        "this", "that", "these", "those", "的", "了", "在", "是", "我", "有",
        "和", "就", "不", "人", "都", "一", "一个", "上", "也", "很", "到"
    };

    private static readonly string[] CausalMarkers =
    {
        "because", "therefore", "thus", "hence", "since", "as a result",
        "consequently", "due to", "leads to", "causes", "resulting in",
        "if", "then", "so that", "implies", "如果", "所以", "因此", "由于",
        "导致", "引起", "首先", "其次", "然后", "最后", "因为", "于是"
    };

    private static readonly string[] StructuralIndicators =
    {
        "```", "```python", "```csharp", "```js", "1.", "2.", "3.",
        "\n\n", "\n###", "\n---", "| ", "cost:", "price:"
    };

    public DataValueDensity(ILogger<DataValueDensity>? logger = null)
    {
        _logger = logger;
    }

    public DensityReport Assess(string text, Dictionary<string, object>? context = null)
    {
        var infoDensity = ComputeInfoDensity(text);
        var structuralComplex = ComputeStructuralComplexity(text);
        var causalSignals = ComputeCausalSignals(text);
        var novelty = ComputeNovelty(text);
        var utility = context?.TryGetValue("utility", out var u) == true && u is double du ? du : 0.5;
        var completeness = ComputeCompleteness(text);
        var ibMutualInfo = ComputeIbMutualInfo(text, context?.TryGetValue("success", out var s) == true && s is bool sb && sb);

        var totalScore = infoDensity * 0.15 +
                         structuralComplex * 0.20 +
                         causalSignals * 0.15 +
                         novelty * 0.10 +
                         utility * 0.15 +
                         completeness * 0.10 +
                         ibMutualInfo * 0.15;

        var verdict = totalScore switch
        {
            >= 0.7 => "high_value",
            >= 0.4 => "medium_value",
            >= 0.2 => "low_value",
            _ => "noise"
        };

        var suggestions = new List<string>();
        if (infoDensity < 0.3) suggestions.Add("Low information density - consider enrichment");
        if (causalSignals < 0.2) suggestions.Add("Missing causal reasoning markers");
        if (completeness < 0.3) suggestions.Add("Content may be incomplete");

        return new DensityReport
        {
            TotalScore = totalScore,
            InfoDensity = infoDensity,
            StructuralComplexity = structuralComplex,
            CausalSignals = causalSignals,
            Novelty = novelty,
            Utility = utility,
            Completeness = completeness,
            IbMutualInfo = ibMutualInfo,
            Verdict = verdict,
            Suggestions = suggestions,
            SubScores = new Dictionary<string, double>
            {
                ["info_density"] = infoDensity,
                ["structural_complexity"] = structuralComplex,
                ["causal_signals"] = causalSignals,
                ["novelty"] = novelty,
                ["utility"] = utility,
                ["completeness"] = completeness,
                ["ib_mutual_info"] = ibMutualInfo
            }
        };
    }

    private static double ComputeInfoDensity(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var words = Tokenize(text);
        if (words.Length == 0) return 0;

        var contentWords = words.Where(w => !StopWords.Contains(w)).ToList();
        var uniqueRatio = (double)new HashSet<string>(contentWords, StringComparer.OrdinalIgnoreCase).Count /
                          Math.Max(contentWords.Count, 1);

        var codeBonus = text.Contains("```") ? 0.2 : 0;
        var urlPenalty = text.Contains("http") ? Math.Min(0.1, 0.01 * System.Text.RegularExpressions.Regex.Matches(text, "http").Count) : 0;

        return Math.Clamp(uniqueRatio * 0.8 + codeBonus - urlPenalty, 0, 1);
    }

    private static double ComputeStructuralComplexity(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        double score = 0;

        if (text.Contains("```")) score += 0.3;
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^\d+\.", System.Text.RegularExpressions.RegexOptions.Multiline))
            score += 0.2;
        if (text.Contains("\n\n")) score += 0.2;
        if (text.Contains("|")) score += 0.15;
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^(  |\t)", System.Text.RegularExpressions.RegexOptions.Multiline))
            score += 0.15;

        return Math.Min(1.0, score);
    }

    private static double ComputeCausalSignals(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var lower = text.ToLower();
        var matches = CausalMarkers.Count(m => lower.Contains(m, StringComparison.OrdinalIgnoreCase));

        return matches switch
        {
            >= 5 => 0.9,
            >= 3 => 0.7,
            >= 1 => 0.4,
            _ => 0
        };
    }

    private double ComputeNovelty(string text)
    {
        var hash = text.GetHashCode();
        var isNew = _seenHashes.TryAdd(hash.ToString(), 1);

        if (isNew)
            return Math.Min(1.0, 0.3 + _seenHashes.Count * 0.001);

        _seenHashes.AddOrUpdate(hash.ToString(), 1, (_, v) => v + 1);
        var count = _seenHashes[hash.ToString()];
        return Math.Max(0.05, 1.0 / count);
    }

    private static double ComputeCompleteness(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var closingMarkers = new[] { "conclusion", "summary", "in summary", "finally", "综上所述", "总结", "总之", "END", "end" };
        var hasClosing = closingMarkers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));

        var hasQuestion = text.Contains("?") || text.Contains("？");

        return hasClosing ? 0.8 : hasQuestion ? 0.3 : 0.5;
    }

    private double ComputeIbMutualInfo(string text, bool success)
    {
        var features = ExtractIbFeatures(text);
        if (features.Count == 0) return 0.5;

        double totalInfo = 0;
        foreach (var f in features)
        {
            var sCount = _successFeatures.GetValueOrDefault(f, 0) + 1;
            var fCount = _failureFeatures.GetValueOrDefault(f, 0) + 1;
            var total = sCount + fCount;

            var pSuccess = (double)sCount / total;
            var pAll = 0.5;
            totalInfo += Math.Abs(pSuccess - pAll);
        }

        if (success)
            UpdateIbFeatures(features, true);
        else
            UpdateIbFeatures(features, false);

        return Math.Min(1.0, totalInfo);
    }

    private static List<string> ExtractIbFeatures(string text)
    {
        var features = new List<string>();

        if (text.Contains("```")) features.Add("code_blocks");
        if (text.Contains("\n\n")) features.Add("paragraphs");
        if (text.Contains("because") || text.Contains("therefore") || text.Contains("所以"))
            features.Add("reasoning_markers");
        if (text.Contains("conclusion") || text.Contains("summary") || text.Contains("总结"))
            features.Add("conclusive_ending");

        return features;
    }

    private void UpdateIbFeatures(List<string> features, bool success)
    {
        foreach (var f in features)
        {
            if (success)
                _successFeatures.AddOrUpdate(f, 1, (_, v) => v + 1);
            else
                _failureFeatures.AddOrUpdate(f, 1, (_, v) => v + 1);
        }
    }

    public List<(string Text, DensityReport Report)> AssessBatch(List<string> texts,
        Dictionary<string, object>? context = null)
    {
        return texts.Select(t => (t, Assess(t, context))).ToList();
    }

    public List<(string Text, DensityReport Report)> SelectTopK(
        List<string> texts, int topK, Dictionary<string, object>? context = null)
    {
        return AssessBatch(texts, context)
            .OrderByDescending(x => x.Report.TotalScore)
            .Take(topK)
            .ToList();
    }

    public List<(string Text, DensityReport Report)> FilterHighValue(
        List<string> texts, double threshold = 0.6, Dictionary<string, object>? context = null)
    {
        return AssessBatch(texts, context)
            .Where(x => x.Report.TotalScore >= threshold)
            .ToList();
    }

    public List<(string Text, DensityReport Report)> FilterMemories(
        List<(string Text, Dictionary<string, object> Context)> blocks, double threshold = 0.4)
    {
        return blocks
            .Select(b => (b.Text, Assess(b.Text, b.Context)))
            .Where(x => x.Item2.TotalScore >= threshold)
            .ToList();
    }

    private static string[] Tokenize(string text)
    {
        return text.Split(new[] { ' ', '\n', '\r', '\t', '.', ',', ';', ':', '(', ')', '[', ']', '{', '}', '!', '?' },
            StringSplitOptions.RemoveEmptyEntries);
    }

    public Dictionary<string, object> GetStats()
    {
        return new Dictionary<string, object>
        {
            ["seen_hashes"] = _seenHashes.Count,
            ["success_features"] = _successFeatures.Count,
            ["failure_features"] = _failureFeatures.Count
        };
    }
}
