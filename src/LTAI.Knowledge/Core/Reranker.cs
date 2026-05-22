using System.Diagnostics;
using LTAI.Knowledge.Core.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.Knowledge.Core;

public class Reranker
{
    private readonly string _method;
    private readonly int _topK;
    private int _rerankCount;
    private readonly ILogger<Reranker> _logger;

    public Reranker(string method = "heuristic", int topK = 5, ILogger<Reranker>? logger = null)
    {
        _method = method;
        _topK = topK;
        _logger = logger ?? NullLogger<Reranker>.Instance;
    }

    public RerankResult Rerank(List<Dictionary<string, object>> candidates, string query, int? topK = null, string? method = null)
    {
        var sw = Stopwatch.StartNew();
        var m = method ?? _method;
        var k = topK ?? _topK;

        var ranked = HeuristicRerank(candidates, query);
        ranked.Sort((a, b) => b.RerankScore.CompareTo(a.RerankScore));
        var top = ranked.Take(k).ToList();

        sw.Stop();
        _rerankCount++;
        return new RerankResult(query, top, m, k, sw.ElapsedMilliseconds, candidates.Count, ranked.Count);
    }

    public List<RankedDocument> HeuristicRerank(List<Dictionary<string, object>> candidates, string query)
    {
        var queryWords = Tokenize(query);
        var results = new List<RankedDocument>();

        for (int i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            var text = c.GetValueOrDefault("text", c.GetValueOrDefault("content", ""))?.ToString() ?? "";
            var docId = c.GetValueOrDefault("id", i.ToString())?.ToString() ?? i.ToString();
            var origScore = c.GetValueOrDefault("score", c.GetValueOrDefault("original_score", 0.0)) is double s ? s : 0.0;

            var docWords = Tokenize(text);
            double jaccard = Jaccard(queryWords, docWords);
            double exactBonus = text.Contains(query, StringComparison.OrdinalIgnoreCase) ? 1.5 : 0.0;
            double positionBonus = 0.1 * (1.0 - (double)i / Math.Max(candidates.Count, 1));
            double lengthScore = LengthScore(text.Length);
            double structBonus = StructBonus(text);

            double score = jaccard * 0.35 + exactBonus * 0.15 + positionBonus * 0.10
                           + lengthScore * 0.20 + structBonus * 0.15 + origScore * 0.05;

            results.Add(new RankedDocument(docId, text, origScore, score,
                c.GetValueOrDefault("source", "")?.ToString() ?? ""));
        }
        return results;
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 && b.Count == 0) return 1.0;
        var intersection = a.Intersect(b).Count();
        var union = a.Union(b).Count();
        return union == 0 ? 0 : (double)intersection / union;
    }

    private static double LengthScore(int len) => len switch
    {
        < 100 => 0.5,
        < 200 => 0.8,
        <= 800 => 1.0,
        _ => Math.Max(0.4, 1.0 - (len - 800) / 2000.0)
    };

    private static double StructBonus(string text)
    {
        double bonus = 0;
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"\d+"))
            bonus += 0.1;
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"[A-Z]{2,}[-\s]?\d"))
            bonus += 0.15;
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"[《「]\D+[》」]"))
            bonus += 0.15;
        return bonus;
    }

    private static HashSet<string> Tokenize(string text)
    {
        return new HashSet<string>(
            System.Text.RegularExpressions.Regex.Matches(text.ToLowerInvariant(), @"\w{2,}")
                .Select(m => m.Value));
    }

    public Dictionary<string, object> GetStats() => new() { ["method"] = _method, ["rerank_count"] = _rerankCount };
}

internal class NullLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => false;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    public static NullLogger<T> Instance { get; } = new();
}
