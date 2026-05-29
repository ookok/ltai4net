using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using LTAI.Knowledge.Core.Models;
using LTAI.Knowledge.Vector.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Knowledge.Core;

public class AgenticRAG
{
    private const int MaxRounds = 5;
    private const int ShortPathMaxLength = 15;
    private const int MaxCacheEntries = 200;

    private readonly DocumentStore _docStore;
    private readonly QueryDecomposer _decomposer;
    private readonly RAGCircuitBreaker _circuitBreaker;
    private readonly HybridRecallEngine? _hybridRecall;
    private readonly ILogger<AgenticRAG> _logger;

    private readonly ConcurrentDictionary<string, (List<KnowledgeSearchResult> Results, DateTime Expiry)> _queryCache = new();

    private static readonly string[] ShortPathPatterns =
    {
        @"^(你好|hi|hello|谢谢|bye|再见)",
        @"^[?？]?[^?？]{1,15}[?？]?$",
        @"^(是|否|对|错|yes|no|true|false)[?？]?$",
        @"^(现在几|今天星期|今天是|日期|时间)"
    };

    public AgenticRAG(DocumentStore docStore,
        QueryDecomposer decomposer, ILogger<AgenticRAG>? logger = null,
        HybridRecallEngine? hybridRecall = null)
    {
        _docStore = docStore;
        _decomposer = decomposer;
        _circuitBreaker = new RAGCircuitBreaker();
        _hybridRecall = hybridRecall;
        _logger = logger ?? new NullLogger<AgenticRAG>();
    }

    public async Task<List<KnowledgeSearchResult>> SearchAsync(string query, RAGMode mode = RAGMode.Iterative,
        int maxRounds = 3, int maxTokens = 50000, string domain = "general")
    {
        var sw = Stopwatch.StartNew();
        _circuitBreaker.Reset();

        // In-memory query cache
        var cacheKey = $"{domain}:{query}";
        if (_queryCache.TryGetValue(cacheKey, out var cached) && cached.Expiry > DateTime.UtcNow)
        {
            _logger.LogDebug("AgenticRAG: Cache hit ({Len} chars, {Count} results)", query.Length, cached.Results.Count);
            return cached.Results;
        }

        List<KnowledgeSearchResult> results;
        if (_hybridRecall != null)
        {
            try
            {
                results = await _hybridRecall.SearchAsync(query, domain, maxRounds * 3).ConfigureAwait(false);
                _logger.LogInformation("AgenticRAG: Hybrid recall returned {Count} results in {Ms}ms",
                    results.Count, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Hybrid recall failed, falling back to FTS5");
                results = IsShortPath(query) ? ShortPathRAG(query, domain) : IterativeRAG(query, domain, maxRounds, maxTokens);
            }
        }
        else
        {
            results = IsShortPath(query) ? ShortPathRAG(query, domain) : IterativeRAG(query, domain, maxRounds, maxTokens);
        }

        if (results.Count > 0 && _queryCache.Count < MaxCacheEntries)
            _queryCache[cacheKey] = (results, DateTime.UtcNow.AddMinutes(15));

        return results;
    }

    private List<KnowledgeSearchResult> IterativeRAG(string query, string domain, int maxRounds, int maxTokens)
    {
        for (int round = 0; round < Math.Min(maxRounds, MaxRounds); round++)
        {
            var results = _docStore.SearchFts(query, domain, 20);
            var candidates = results.Select(r => new Dictionary<string, object>
            {
                ["id"] = r.Id, ["text"] = r.Content, ["score"] = r.Score, ["source"] = r.Source
            }).ToList();

            if (candidates.Count > 0)
            {
                var reranked = candidates
                    .OrderByDescending(c => (double)c["score"])
                    .Take(10)
                    .ToList();
                var rerankedResults = reranked.Select(c => new KnowledgeSearchResult
                {
                    Id = c["id"]?.ToString() ?? "",
                    Title = c["text"]?.ToString()?[..Math.Min((c["text"]?.ToString() ?? "").Length, 100)] ?? "",
                    Content = c["text"]?.ToString() ?? "",
                    Domain = domain,
                    Score = Convert.ToDouble(c["score"]),
                    Source = "rag_round" + round
                }).ToList();

                double confidence = rerankedResults.Count > 0 ? Math.Min(0.9, rerankedResults.Count / 20.0) : 0.0;
                if (_circuitBreaker.Record(confidence)) break;
                if (confidence >= 0.7) return rerankedResults;
            }
            else
            {
                if (_circuitBreaker.RecordFailure()) break;
            }
        }

        return _docStore.SearchFts(query, domain, 10);
    }

    private List<KnowledgeSearchResult> ShortPathRAG(string query, string domain)
    {
        return _docStore.SearchFts(query, domain, 5);
    }

    public static bool IsShortPath(string query)
    {
        if (query.Length <= ShortPathMaxLength) return true;
        return ShortPathPatterns.Any(p => Regex.IsMatch(query, p, RegexOptions.IgnoreCase));
    }
}
