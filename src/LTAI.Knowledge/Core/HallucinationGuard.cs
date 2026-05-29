using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace LTAI.Knowledge.Core;

public sealed record HallucinationVerdict(bool Passed, double Score, string Reason, List<string> SuspiciousSentences);
public sealed record SentenceCheck(int Index, string Text, double NgramScore, double KeywordScore, bool Flagged, string Reason);

public sealed class HallucinationGuard
{
    private static readonly Lazy<HallucinationGuard> _instance = new(() => new HallucinationGuard());
    public static HallucinationGuard Instance => _instance.Value;

    private readonly ConcurrentQueue<HallucinationRecord> _history = new();
    private double _globalRate;
    private int _totalChecks;

    private HallucinationGuard() { }

    public HallucinationVerdict CheckGeneration(string generatedText, string? contextText = null)
    {
        Interlocked.Increment(ref _totalChecks);
        var sentences = SplitSentences(generatedText);
        var checks = new List<SentenceCheck>();
        var flagged = new List<string>();

        for (var i = 0; i < sentences.Count; i++)
        {
            var ngramScore = ComputeNgramCoherence(sentences, i);
            var keywordScore = ComputeKeywordOverlap(sentences[i], contextText);
            var isHanging = sentences[i].Length > 200 && ngramScore < 0.3;
            var isUnsupported = contextText != null && keywordScore < 0.1;

            var flagged_ = isHanging || isUnsupported;
            var reason = isHanging ? "low_ngram_coherence" : isUnsupported ? "unsupported_by_context" : "ok";

            checks.Add(new SentenceCheck(i, sentences[i], Math.Round(ngramScore, 3), Math.Round(keywordScore, 3), flagged_, reason));
            if (flagged_) flagged.Add($"[{i}] {sentences[i][..Math.Min(80, sentences[i].Length)]}");
        }

        var score = checks.Count > 0 ? checks.Average(c => (c.NgramScore + c.KeywordScore) / 2) : 1.0;
        var passed = flagged.Count == 0 || (double)flagged.Count / checks.Count < 0.2;

        _history.Enqueue(new HallucinationRecord(score, flagged.Count, checks.Count));
        while (_history.Count > 50) _history.TryDequeue(out _);

        // Compute global rate safely — guard against DivisionByZero and empty history
        var recent = _history.ToArray();
        _globalRate = recent.Length > 0
            ? recent.Average(r => r.Total > 0 ? (double)r.Flagged / r.Total : 0.0)
            : 0.0;

        return new HallucinationVerdict(passed, Math.Round(score, 3),
            passed ? "passed" : $"{flagged.Count} suspicious sentences",
            flagged);
    }

    public HallucinationVerdict VerifyAgainstKb(string generatedText, List<string> kbFacts)
    {
        var sentences = SplitSentences(generatedText);
        var flagged = new List<string>();

        foreach (var sentence in sentences)
        {
            var matched = kbFacts.Any(f =>
                OverlapScore(sentence.ToLower(), f.ToLower()) > 0.3);
            if (!matched)
                flagged.Add(sentence[..Math.Min(80, sentence.Length)]);
        }

        var score = sentences.Count > 0 ? 1.0 - (double)flagged.Count / sentences.Count : 1.0;
        return new HallucinationVerdict(score > 0.7, Math.Round(score, 3),
            score > 0.7 ? "verified" : $"{flagged.Count} unverified claims", flagged);
    }

    public Dictionary<string, object> GetDashboard() => new()
    {
        ["total_checks"] = _totalChecks,
        ["hallucination_rate"] = Math.Round(_globalRate, 3),
        ["recent"] = _history.ToArray().TakeLast(10).Select(r => new { r.Score, r.Flagged, r.Total }).ToList(),
        ["status"] = _globalRate < 0.1 ? "healthy" : _globalRate < 0.3 ? "warning" : "critical"
    };

    private static double ComputeNgramCoherence(List<string> sentences, int idx)
    {
        if (idx == 0 || idx >= sentences.Count) return 1.0;
        var words = sentences[idx].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var prevWords = sentences[idx - 1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 3 || prevWords.Length < 3) return 0.5;
        var overlap = words.Intersect(prevWords).Count();
        return Math.Min(1.0, (double)overlap / Math.Min(words.Length, prevWords.Length) * 3);
    }

    private static double ComputeKeywordOverlap(string sentence, string? context)
    {
        if (string.IsNullOrEmpty(context)) return 0.5;
        var kw = ExtractKeywords(sentence);
        var ck = context.ToLower();
        return kw.Count > 0 ? (double)kw.Count(k => ck.Contains(k)) / kw.Count : 0.5;
    }

    private static double OverlapScore(string a, string b)
    {
        var wa = new HashSet<string>(a.Split(' '));
        var wb = new HashSet<string>(b.Split(' '));
        var union = new HashSet<string>(wa); union.UnionWith(wb);
        var intersection = wa.Intersect(wb).Count();
        return union.Count > 0 ? (double)intersection / union.Count : 0;
    }

    private static List<string> SplitSentences(string text) =>
        Regex.Split(text, @"(?<=[。！？.!?\n])")
            .Select(s => s.Trim())
            .Where(s => s.Length > 5)
            .ToList();

    private static List<string> ExtractKeywords(string text) =>
        Regex.Matches(text, @"[\u4e00-\u9fff]{2,}|[A-Z][a-z]{2,}")
            .Select(m => m.Value.ToLower())
            .Distinct().Take(10).ToList();

    private sealed record HallucinationRecord(double Score, int Flagged, int Total);
}
