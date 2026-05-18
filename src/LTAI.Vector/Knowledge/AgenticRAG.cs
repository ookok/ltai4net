using System.Diagnostics;
using System.Text.RegularExpressions;
using LTAI.Vector.Knowledge.Models;
using LTAI.Vector.Interfaces;
using Microsoft.Extensions.Logging;

namespace LTAI.Vector.Knowledge;

public class AgenticRAG
{
    private const int MaxRounds = 5;
    private const double MinImprovement = 0.1;
    private const int ShortPathMaxLength = 15;

    private readonly DocumentStore _docStore;
    private readonly Reranker _reranker;
    private readonly QueryDecomposer _decomposer;
    private readonly RAGCircuitBreaker _circuitBreaker;
    private readonly ILogger<AgenticRAG> _logger;

    private static readonly string[] ShortPathPatterns =
    {
        @"^(你好|hi|hello|谢谢|bye|再见)",
        @"^[?？]?[^?？]{1,15}[?？]?$",
        @"^(是|否|对|错|yes|no|true|false)[?？]?$",
        @"^(现在几|今天星期|今天是|日期|时间)"
    };

    public AgenticRAG(DocumentStore docStore, Reranker reranker,
        QueryDecomposer decomposer, ILogger<AgenticRAG>? logger = null)
    {
        _docStore = docStore;
        _reranker = reranker;
        _decomposer = decomposer;
        _circuitBreaker = new RAGCircuitBreaker();
        _logger = logger ?? new NullLogger<AgenticRAG>();
    }

    public List<KnowledgeSearchResult> Search(string query, RAGMode mode = RAGMode.Iterative,
        int maxRounds = 3, int maxTokens = 50000, string domain = "general")
    {
        var sw = Stopwatch.StartNew();
        _circuitBreaker.Reset();

        if (IsShortPath(query))
            return ShortPathRAG(query, domain);

        return IterativeRAG(query, domain, maxRounds, maxTokens);
    }

    private List<KnowledgeSearchResult> IterativeRAG(string query, string domain, int maxRounds, int maxTokens)
    {
        for (int round = 0; round < Math.Min(maxRounds, MaxRounds); round++)
        {
            // FTS5 search
            var results = _docStore.SearchFts(query, domain, 20);

            // Rerank
            var candidates = results.Select(r => new Dictionary<string, object>
            {
                ["id"] = r.Id, ["text"] = r.Content, ["score"] = r.Score, ["source"] = r.Source
            }).ToList();

            if (candidates.Count > 0)
            {
                var reranked = _reranker.Rerank(candidates, query, 10);
                var rerankedResults = reranked.RankedDocs.Select(rd =>
                {
                    var doc = _docStore.GetDocument(rd.DocId);
                    return new KnowledgeSearchResult
                    {
                        Id = rd.DocId, Title = doc?.Title ?? rd.Text[..Math.Min(rd.Text.Length, 100)],
                        Content = rd.Text, Domain = domain,
                        Score = rd.CombinedScore, Source = "rag_round" + round
                    };
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

    public Dictionary<string, object> GetStats() => new()
    {
        ["circuit_breaker_open"] = _circuitBreaker.IsOpen
    };
}
