using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Core.Providers;

public sealed class TextRetrievalBooster
{
    private readonly IChatClient _llm;
    private readonly ILogger<TextRetrievalBooster> _logger;
    private readonly ConcurrentDictionary<string, string[]> _keywordCache = new();
    private readonly ConcurrentDictionary<string, string[]> _synonymCache = new();

    public TextRetrievalBooster(IChatClient llm, ILogger<TextRetrievalBooster>? logger = null)
    {
        _llm = llm;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TextRetrievalBooster>.Instance;
    }

    public async Task<string[]> ExtractKeywordsAsync(string query)
    {
        if (_keywordCache.TryGetValue(query, out var cached))
            return cached;

        try
        {
            var prompt = $"Extract 3-5 key concept words or short phrases from this query. Return ONLY comma-separated words, no explanation: \"{query[..Math.Min(query.Length, 200)]}\"";
            var response = await _llm.GetResponseAsync(prompt, new ChatOptions { Temperature = 0, MaxOutputTokens = 80 });
            var text = (response.Text ?? "").Trim();
            var keywords = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(k => k.Trim('"', '\''))
                .Where(k => k.Length > 1)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (keywords.Length > 0)
            {
                _keywordCache[query] = keywords;
                _logger.LogDebug("TextBooster: extracted {Count} keywords for query", keywords.Length);
            }
            return keywords;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public async Task<string[]> ExpandSynonymsAsync(string word)
    {
        if (_synonymCache.TryGetValue(word, out var cached))
            return cached;

        try
        {
            var prompt = $"List 3-5 synonyms or closely related terms for: \"{word}\". Return ONLY comma-separated words, no explanation.";
            var response = await _llm.GetResponseAsync(prompt, new ChatOptions { Temperature = 0, MaxOutputTokens = 60 });
            var text = (response.Text ?? "").Trim();
            var synonyms = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.Trim('"', '\''))
                .Where(s => s.Length > 1 && !s.Equals(word, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (synonyms.Length > 0)
            {
                _synonymCache[word] = synonyms;
                _logger.LogDebug("TextBooster: expanded {Word} → {Count} synonyms", word, synonyms.Length);
            }
            return synonyms;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public async Task<double> EnhancedTextSimilarityAsync(string query, string document)
    {
        var queryWords = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var docWords = document.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

        var intersect = queryWords.Intersect(docWords).Count();
        var union = queryWords.Union(docWords).Count();
        var jaccard = union > 0 ? (double)intersect / union : 0.0;

        var keywords = await ExtractKeywordsAsync(query);
        if (keywords.Length > 0)
        {
            var keywordHits = keywords.Count(k => document.Contains(k, StringComparison.OrdinalIgnoreCase));
            jaccard += keywordHits * 0.15;
        }

        var topWords = queryWords.OrderByDescending(w => w.Length).Take(3);
        foreach (var word in topWords)
        {
            var synonyms = await ExpandSynonymsAsync(word);
            var synHits = synonyms.Count(s => document.Contains(s, StringComparison.OrdinalIgnoreCase));
            jaccard += synHits * 0.08;
        }

        return Math.Min(1.0, jaccard);
    }

    public async Task<List<(T Item, double Score)>> ReRankAsync<T>(
        string query, List<(T Item, string Document, double TextScore)> candidates, int topK = 5)
    {
        if (candidates.Count <= topK)
            return candidates.Select(c => (Item: c.Item, Score: c.TextScore)).ToList();

        try
        {
            var prompt = new global::System.Text.StringBuilder();
            prompt.AppendLine("Rate each candidate's relevance to the query on a scale of 0.0-1.0. Return ONLY comma-separated scores, one per candidate:");
            prompt.AppendLine($"Query: {query[..Math.Min(query.Length, 100)]}");
            prompt.AppendLine("Candidates:");
            for (int i = 0; i < Math.Min(candidates.Count, 10); i++)
            {
                var doc = candidates[i].Document[..Math.Min(candidates[i].Document.Length, 60)];
                prompt.AppendLine($"[{i}] {doc}");
            }

            var response = await _llm.GetResponseAsync(prompt.ToString(), new ChatOptions { Temperature = 0, MaxOutputTokens = 80 });
            var text = (response.Text ?? "").Trim();
            var scores = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => double.TryParse(s.Trim(), out var v) ? v : 0.5)
                .ToArray();

            var results = new List<(T Item, double Score)>();
            for (int i = 0; i < Math.Min(candidates.Count, scores.Length); i++)
            {
                var combinedScore = candidates[i].TextScore * 0.4 + (scores.Length > i ? scores[i] : 0.5) * 0.6;
                results.Add((candidates[i].Item, combinedScore));
            }

            return results.OrderByDescending(r => r.Score).Take(topK).ToList();
        }
        catch
        {
            return candidates.Select(c => (Item: c.Item, Score: c.TextScore)).Take(topK).ToList();
        }
    }
}
