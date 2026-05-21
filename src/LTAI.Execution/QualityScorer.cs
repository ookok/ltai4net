using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using LTAI.Execution.Models;

namespace LTAI.Execution;

public class QualityScorer
{
    private static QualityScorer? _instance;
    private static readonly Lock InstanceLock = new();

    private static readonly Regex HeadingRx = new(
        @"^#{1,6}\s|^\*\*[^*]+\*\*$|^[A-Z\u4e00-\u9fff][^a-z\n]{0,60}$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex CodeBlockRx = new(
        @"```[\s\S]*?```",
        RegexOptions.Compiled);

    private static readonly Regex ListRx = new(
        @"^[\s]*[-*+]\s|^[\s]*\d+[.)]\s",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex TableRx = new(
        @"\|[^|]+\|.*\n\|[-:| ]+\|",
        RegexOptions.Compiled);

    private static readonly Regex NumberRx = new(
        @"\b\d+(?:\.\d+)?\b",
        RegexOptions.Compiled);

    private static readonly Regex StandardRefRx = new(
        @"\b(?:GB|HJ|ISO|ASTM|EN|DIN|JIS|CJJ|CJ|NY|LY|SL|DL|NB|SH|SY|JB|YB|SN|GY)\s*[/-]?\s*\d+(?:\.\d+)?(?:[-:]\d+)?\b",
        RegexOptions.Compiled);

    private static readonly Regex UnitValueRx = new(
        @"\d+\.?\d*\s*(?:吨|mg|dB|km|m/s|kg|g|L|mL|h|min|s|℃|%|ppm|ppb|m|cm|mm|μm|km²|m²|ha|Pa|kPa|MPa|W|kW|MW|J|kJ|MJ|N|kN)",
        RegexOptions.Compiled);

    private static readonly Regex CodeRefRx = new(
        @"`[^`]+`|[\w.]+\(\)|[\w]+\.py\b|[\w]+\.cs\b|[\w]+\.js\b",
        RegexOptions.Compiled);

    private static readonly Regex CitationBracketRx = new(
        @"\[(\d+(?:,\s*\d+)*)\]|\[\d+[-–]\d+\]",
        RegexOptions.Compiled);

    private static readonly Regex StatuteRefRx = new(
        @"第[一二三四五六七八九十百千万\d]+\s*条|《[^》]+》",
        RegexOptions.Compiled);

    private QualityScorer() { }

    public static QualityScorer GetQualityScorer()
    {
        if (_instance is null)
        {
            lock (InstanceLock)
            {
                _instance ??= new QualityScorer();
            }
        }
        return _instance;
    }

    public ScoreResult Evaluate(string output, string systemPrompt = "", string userPrompt = "")
    {
        var prompt = string.IsNullOrEmpty(userPrompt) ? systemPrompt
            : string.IsNullOrEmpty(systemPrompt) ? userPrompt
            : $"{systemPrompt}\n{userPrompt}";

        var alignmentWeight = 0.30f;
        var structuralWeight = 0.30f;
        var specificityWeight = 0.25f;
        var citationWeight = 0.15f;

        if (prompt.Length < 30)
        {
            alignmentWeight = 0.35f;
            structuralWeight = 0.35f;
            specificityWeight = 0.20f;
            citationWeight = 0.10f;
        }

        var alignment = ComputeAlignmentScore(output, prompt);
        var structural = ComputeStructuralScore(output, prompt);
        var specificity = ComputeSpecificityScore(output);
        var citation = ComputeCitationScore(output);

        var overall = alignment * alignmentWeight
                    + structural * structuralWeight
                    + specificity * specificityWeight
                    + citation * citationWeight;

        var segments = SegmentScore(output, prompt);
        var flags = DetectFlags(output, prompt);

        var tokenCount = EstimateTokens(output);

        return new ScoreResult(
            Output: output,
            Prompt: prompt,
            OverallScore: Math.Clamp(overall, 0f, 1f),
            Method: "quality_scorer_v1",
            PerSegment: segments,
            Flags: flags,
            TokenCount: tokenCount);
    }

    public List<ScoreResult> EvaluateBatch(List<Dictionary<string, string>> items)
    {
        var results = new ConcurrentBag<ScoreResult>();

        Parallel.ForEach(items, item =>
        {
            var output = item.GetValueOrDefault("output", "");
            var systemPrompt = item.GetValueOrDefault("system_prompt", "");
            var userPrompt = item.GetValueOrDefault("user_prompt", "");

            results.Add(Evaluate(output, systemPrompt, userPrompt));
        });

        return results.ToList();
    }

    public (int index, ScoreResult result) SelectBest(List<Dictionary<string, string>> items)
    {
        if (items.Count == 0)
            throw new ArgumentException("Items list is empty");

        var scored = EvaluateBatch(items);
        var best = scored
            .Select((s, i) => (index: i, result: s))
            .MaxBy(x => x.result.OverallScore);

        return best;
    }

    public List<(int, ScoreResult)> SortByQuality(List<Dictionary<string, string>> items)
    {
        return EvaluateBatch(items)
            .Select((s, i) => (index: i, result: s))
            .OrderByDescending(x => x.result.OverallScore)
            .ToList();
    }

    private float ComputeAlignmentScore(string output, string prompt)
    {
        if (string.IsNullOrEmpty(prompt))
            return 0.5f;

        var outputNgrams = NGrams(output, 3);
        var promptNgrams = NGrams(prompt, 3);

        float jaccard;
        if (outputNgrams.Count == 0 && promptNgrams.Count == 0)
            jaccard = 1.0f;
        else if (outputNgrams.Count == 0 || promptNgrams.Count == 0)
            jaccard = 0f;
        else
        {
            var intersection = outputNgrams.Intersect(promptNgrams).Count();
            var union = outputNgrams.Union(promptNgrams).Count();
            jaccard = union > 0 ? (float)intersection / union : 0f;
        }

        var outputWords = ExtractContentWords(output);
        var promptWords = ExtractContentWords(prompt);

        float keywordOverlap;
        if (promptWords.Count == 0)
            keywordOverlap = 0.5f;
        else
        {
            var matched = promptWords.Count(pw =>
                outputWords.Any(ow =>
                    ow.Contains(pw, StringComparison.OrdinalIgnoreCase) ||
                    pw.Contains(ow, StringComparison.OrdinalIgnoreCase)));
            keywordOverlap = (float)matched / promptWords.Count;
        }

        return jaccard * 0.6f + keywordOverlap * 0.4f;
    }

    private float ComputeStructuralScore(string output, string prompt)
    {
        var score = 0.3f;

        score += HeadingRx.Matches(output).Count * 0.05f;
        var headingCount = Math.Min(HeadingRx.Matches(output).Count, 6);
        score += headingCount * 0.05f;

        var codeCount = CodeBlockRx.Matches(output).Count;
        score += codeCount * 0.1f;

        var backtickCount = output.Count(c => c == '`');
        if (backtickCount % 2 != 0)
            score -= 0.15f;

        if (ListRx.IsMatch(output))
            score += 0.05f;

        if (TableRx.IsMatch(output))
            score += 0.1f;

        return Math.Clamp(score, 0f, 1f);
    }

    private float ComputeSpecificityScore(string output)
    {
        var score = 0.3f;

        var numbers = NumberRx.Matches(output).Count;
        if (numbers >= 5)
            score += 0.3f;
        else if (numbers >= 2)
            score += 0.15f;

        var standards = StandardRefRx.Matches(output).Count;
        score += Math.Min(standards, 4) * 0.15f;

        if (UnitValueRx.IsMatch(output))
            score += 0.1f;

        if (CodeRefRx.IsMatch(output))
            score += 0.1f;

        return Math.Clamp(score, 0f, 1f);
    }

    private float ComputeCitationScore(string output)
    {
        var score = 0.3f;

        var refMatches = Regex.Matches(output,
            @"\[\d+\]|\[[^\]]*\d{4}[^\]]*\]|\([^)]*\d{4}[^)]*\)");
        var refCount = refMatches
            .Select(m => m.Value)
            .Distinct()
            .Count();
        score += Math.Min(refCount, 4) * 0.15f;

        if (CitationBracketRx.IsMatch(output))
            score += 0.15f;

        if (StatuteRefRx.IsMatch(output))
            score += 0.15f;

        return Math.Clamp(score, 0f, 1f);
    }

    public List<SegmentScore> SegmentScore(string output, string prompt)
    {
        var segments = output.Split("\n\n");
        var results = new List<SegmentScore>();
        var charOffset = 0;

        for (var i = 0; i < segments.Length; i++)
        {
            var seg = segments[i];
            if (string.IsNullOrWhiteSpace(seg))
            {
                charOffset += seg.Length + 2;
                continue;
            }

            var score = ScoreSegment(seg, prompt);

            results.Add(new SegmentScore(
                Text: seg.Length > 200 ? seg[..200] : seg,
                Index: i,
                Score: score,
                StartChar: charOffset,
                EndChar: charOffset + seg.Length,
                Flags: DetectFlags(seg, prompt)));

            charOffset += seg.Length + 2;
        }

        return results;
    }

    public float ScoreSegment(string segment, string prompt)
    {
        var score = ComputeAlignmentScore(segment, prompt);

        if (segment.Length < 30)
            score *= 0.8f;

        if (Regex.IsMatch(segment, @"(?i)\b(?:error|错误|失败|failed|exception|异常)\b"))
            score += 0.1f;
        if (Regex.IsMatch(segment, @"(?i)\b(?:conclusion|总结|结论|therefore|thus|hence|因此|所以)\b"))
            score += 0.1f;

        return Math.Clamp(score, 0f, 1f);
    }

    public static List<string> DetectFlags(string output, string prompt)
    {
        var flags = new List<string>();

        if (string.IsNullOrEmpty(output) || output.Length < 20)
            flags.Add("too_short");

        if (output.Length > 50 && output.Count(c => c == '.' || c == '。' || c == '\n') > 40)
            flags.Add("verbose");

        if (!string.IsNullOrEmpty(prompt))
        {
            var promptWords = ExtractContentWords(prompt);
            var outputWords = ExtractContentWords(output);

            var missingCount = promptWords.Count(pw =>
                !outputWords.Any(ow =>
                    ow.Contains(pw, StringComparison.OrdinalIgnoreCase) ||
                    pw.Contains(ow, StringComparison.OrdinalIgnoreCase)));

            if (missingCount > promptWords.Count * 0.3f)
                flags.Add("missing_entities");
        }

        if (output.Contains("may", StringComparison.OrdinalIgnoreCase)
            && output.Contains("might", StringComparison.OrdinalIgnoreCase)
            || output.Contains("possibly", StringComparison.OrdinalIgnoreCase)
            && output.Contains("could", StringComparison.OrdinalIgnoreCase))
            flags.Add("hedging");

        var backtickCount = output.Count(c => c == '`');
        if (backtickCount > 0 && backtickCount % 2 != 0 && backtickCount % 3 != 0)
            flags.Add("unclosed_code_block");

        return flags;
    }

    public static HashSet<string> NGrams(string text, int n)
    {
        var result = new HashSet<string>();
        var normalized = Regex.Replace(text.Trim(), @"\s+", " ");

        if (normalized.Length < n)
            return result;

        for (var i = 0; i <= normalized.Length - n; i++)
        {
            result.Add(normalized.Substring(i, n));
        }

        return result;
    }

    private static HashSet<string> ExtractContentWords(string text)
    {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in Regex.Matches(text, @"[\u4e00-\u9fff]{2,}|[a-zA-Z]{3,}"))
        {
            if (m.Value.Length >= 2)
                words.Add(m.Value);
        }

        foreach (Match m in Regex.Matches(text, @"[A-Z]{2,}"))
        {
            words.Add(m.Value);
        }

        return words;
    }

    private static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var wordChars = text.Count(c => char.IsLetterOrDigit(c));
        var nonWordChars = text.Length - wordChars;

        return (int)(wordChars / 3.5 + nonWordChars / 2.0);
    }
}
