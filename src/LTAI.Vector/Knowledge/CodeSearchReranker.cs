using System.Text.Json;
using System.Text.RegularExpressions;

namespace LTAI.Vector.Knowledge;

public sealed class CodeSearchReranker
{
    private static readonly HashSet<string> NoisePatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        "test_", "_test", "test.", ".test.", "__test__",
        "compat_", "compat.", "legacy_", "legacy.",
        "example_", "_example", "sample_", "_sample",
        ".d.ts"
    };

    private static readonly HashSet<string> DefinitionKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "class ", "struct ", "record ", "interface ", "enum ",
        "def ", "func ", "fn ", "function ",
        "public class ", "public struct ", "public record ",
        "async def ", "async fn "
    };

    public static List<Bm25ScoredDoc> Rerank(List<Bm25ScoredDoc> results, string query)
    {
        if (results.Count == 0) return results;

        var queryType = ClassifyQuery(query);
        var reranked = new List<(Bm25ScoredDoc Doc, double Score)>();

        foreach (var doc in results)
        {
            var score = doc.RrfScore > 0 ? doc.RrfScore : doc.Bm25Score + doc.VectorScore;

            score *= DefinitionBoost(doc);
            score += StemMatchBonus(doc.Content, query);
            score *= NoisePenalty(doc.Id);
            score *= QueryTypeWeight(queryType, doc);

            reranked.Add((doc, score));
        }

        reranked = FileCoherenceBoost(reranked);

        return reranked
            .OrderByDescending(r => r.Score)
            .Select(r => r.Doc)
            .ToList();
    }

    private static double DefinitionBoost(Bm25ScoredDoc doc)
    {
        foreach (var keyword in DefinitionKeywords)
        {
            if (doc.Content.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return 1.3;
        }
        return 1.0;
    }

    private static double StemMatchBonus(string content, string query)
    {
        var queryTokens = Tokenize(query);
        var contentTokens = TokenizeIdentifiers(content);
        double bonus = 0;

        foreach (var qt in queryTokens)
        {
            foreach (var ct in contentTokens)
            {
                if (ct.StartsWith(qt, StringComparison.OrdinalIgnoreCase) ||
                    qt.StartsWith(ct, StringComparison.OrdinalIgnoreCase))
                {
                    bonus += 0.15;
                    break;
                }
            }
        }

        return Math.Min(bonus, 0.5);
    }

    private static double NoisePenalty(string docId)
    {
        foreach (var pattern in NoisePatterns)
        {
            if (docId.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return 0.5;
        }
        return 1.0;
    }

    public static string ClassifyQueryType(string query) => ClassifyQuery(query);

    private static string ClassifyQuery(string query)
    {
        if (Regex.IsMatch(query, @"::|\.\w+\(|_\w+\b|^[a-z]\w*[A-Z]"))
            return "symbol";
        if (Regex.IsMatch(query, @"^(def|class|func|function|import|export|const|let|var)\s"))
            return "symbol";
        return "natural";
    }

    private static double QueryTypeWeight(string queryType, Bm25ScoredDoc doc)
    {
        return queryType == "symbol"
            ? 0.3 + doc.VectorScore * 0.3 + doc.Bm25Score * 1.4
            : 0.7 + doc.VectorScore * 0.7 + doc.Bm25Score * 0.6;
    }

    private static List<(Bm25ScoredDoc Doc, double Score)> FileCoherenceBoost(
        List<(Bm25ScoredDoc Doc, double Score)> results)
    {
        var fileCounts = new Dictionary<string, int>();
        foreach (var (doc, _) in results)
        {
            var fileName = ExtractFileName(doc.Id);
            fileCounts.TryGetValue(fileName, out var cnt);
            fileCounts[fileName] = cnt + 1;
        }

        return results.Select(r =>
        {
            var fileName = ExtractFileName(r.Doc.Id);
            var count = fileCounts.GetValueOrDefault(fileName, 1);
            return count > 1 ? (r.Doc, r.Score * 1.15) : r;
        }).ToList();
    }

    private static string ExtractFileName(string id)
    {
        var lastSlash = id.LastIndexOf('/');
        return lastSlash >= 0 ? id[(lastSlash + 1)..] : id;
    }

    private static string[] Tokenize(string text) =>
        Regex.Split(text.ToLowerInvariant(), @"[^a-z0-9_]+")
            .Where(t => t.Length > 1)
            .ToArray();

    private static string[] TokenizeIdentifiers(string content) =>
        Regex.Matches(content, @"[a-zA-Z_][a-zA-Z0-9_]*")
            .Select(m => m.Value.ToLowerInvariant())
            .Distinct()
            .ToArray();
}

public sealed class TokenSavingsTracker
{
    private static readonly string SavingsPath = Path.Combine(".livingtree", "token_savings.jsonl");
    private readonly object _lock = new();
    private long _todaySaved;
    private long _weekSaved;
    private long _totalSaved;
    private int _todayCalls;
    private int _weekCalls;
    private int _totalCalls;
    private DateTime _lastReset = DateTime.UtcNow.Date;

    public TokenSavingsTracker() { Load(); }

    public void Record(string searchQuery, long totalFileBytes, long snippetBytes, int resultCount)
    {
        var saved = (totalFileBytes - snippetBytes) / 4;
        if (saved <= 0) return;

        lock (_lock)
        {
            var today = DateTime.UtcNow.Date;
            if (_lastReset < today) { _todaySaved = 0; _todayCalls = 0; _lastReset = today; }

            _todaySaved += saved;
            _weekSaved += saved;
            _totalSaved += saved;
            _todayCalls++;
            _weekCalls++;
            _totalCalls++;

            File.AppendAllText(SavingsPath, JsonSerializer.Serialize(new
            {
                ts = DateTime.UtcNow.ToString("O"),
                query = searchQuery[..Math.Min(searchQuery.Length, 100)],
                totalFileBytes, snippetBytes, saved, resultCount
            }) + "\n");
        }
    }

    public TokenSavingsStats GetStats()
    {
        lock (_lock) return new TokenSavingsStats
        {
            TodaySaved = _todaySaved, WeekSaved = _weekSaved, TotalSaved = _totalSaved,
            TodayCalls = _todayCalls, WeekCalls = _weekCalls, TotalCalls = _totalCalls,
            AvgSavingRate = _totalCalls > 0 ? (double)_totalSaved / _totalCalls : 0
        };
    }

    private void Load()
    {
        if (!File.Exists(SavingsPath)) return;
        var weekAgo = DateTime.UtcNow - TimeSpan.FromDays(7);
        foreach (var line in File.ReadLines(SavingsPath))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<JsonElement>(line);
                var ts = entry.TryGetProperty("ts", out var t) ? t.GetDateTime() : DateTime.MinValue;
                var saved = entry.TryGetProperty("saved", out var s) ? s.GetInt64() : 0;
                _totalSaved += saved; _totalCalls++;
                if (ts > weekAgo) { _weekSaved += saved; _weekCalls++; }
                if (ts.Date == DateTime.UtcNow.Date) { _todaySaved += saved; _todayCalls++; }
            }
            catch { }
        }
    }
}

public sealed class TokenSavingsStats
{
    public long TodaySaved { get; init; }
    public long WeekSaved { get; init; }
    public long TotalSaved { get; init; }
    public int TodayCalls { get; init; }
    public int WeekCalls { get; init; }
    public int TotalCalls { get; init; }
    public double AvgSavingRate { get; init; }
}
