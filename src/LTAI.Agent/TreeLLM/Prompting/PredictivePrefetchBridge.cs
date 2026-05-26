using System.Collections.Concurrent;
using LTAI.Agent.Intelligence;
using LTAI.Agent.Models;
using LTAI.Knowledge.Core;
using LTAI.Knowledge.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Prompting;

public sealed class PredictivePrefetchBridge
{
    private readonly AnticipatoryCompute _predictor;
    private readonly AgenticRAG _rag;
    private readonly IChatClient _chatClient;
    private readonly ConcurrentDictionary<string, List<KnowledgeSearchResult>> _prefetched = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _prefetchTimestamps = new();
    private readonly ILogger<PredictivePrefetchBridge> _logger;
    private static readonly TimeSpan PrefetchTtl = TimeSpan.FromMinutes(5);
    private const int MaxPrefetched = 100;

    private long _prefetchHits;
    private long _totalPredictions;

    public PredictivePrefetchBridge(
        AgenticRAG rag,
        IChatClient chatClient,
        ILogger<PredictivePrefetchBridge>? logger = null)
    {
        _predictor = AnticipatoryCompute.Instance;
        _rag = rag;
        _chatClient = chatClient;
        _logger = logger ?? NullLogger<PredictivePrefetchBridge>.Instance;
    }

    public List<KnowledgeSearchResult> GetPrefetched(string query)
    {
        var normalized = NormalizeQuery(query);

        foreach (var (key, results) in _prefetched)
        {
            if (normalized.Contains(key, StringComparison.OrdinalIgnoreCase) ||
                key.Contains(normalized, StringComparison.OrdinalIgnoreCase))
            {
                var timestamp = _prefetchTimestamps.GetValueOrDefault(key);
                if (DateTimeOffset.UtcNow - timestamp < PrefetchTtl)
                {
                    Interlocked.Add(ref _prefetchHits, 1);
                    _logger.LogDebug("Prefetch hit for query prefix: {Key}", key);
                    return results;
                }
            }
        }

        return new();
    }

    public async Task<List<KnowledgeSearchResult>> GetOrSearchAsync(
        string query,
        string domain = "general",
        CancellationToken ct = default)
    {
        var prefetched = GetPrefetched(query);
        if (prefetched.Count > 0)
            return prefetched;

        return await _rag.SearchAsync(query, RAGMode.Iterative, domain: domain).ConfigureAwait(false);
    }

    public void LearnAndPredict(string sessionId, string currentQuery, string? nextQuery = null)
    {
        if (nextQuery != null)
        {
            _predictor.Learn(currentQuery, nextQuery);
        }

        var predictions = _predictor.PredictNext(sessionId, currentQuery);
        Interlocked.Increment(ref _totalPredictions);

        if (predictions.Count > 0)
        {
            _ = PrefetchAsync(predictions);
        }
    }

    public async Task<int> PrefetchAsync(List<PredictedQuery> predictions)
    {
        var count = 0;
        foreach (var pred in predictions.Where(p => p.Probability > 0.3).Take(5))
        {
            if (_prefetched.Count >= MaxPrefetched)
                break;

            var key = NormalizeQuery(pred.QueryText);
            if (string.IsNullOrWhiteSpace(key) || key.Length < 5)
                continue;

            try
            {
                var results = await _rag.SearchAsync(pred.QueryText, RAGMode.Iterative, domain: "general").ConfigureAwait(false);
                if (results.Count > 0)
                {
                    _prefetched[key] = results;
                    _prefetchTimestamps[key] = DateTimeOffset.UtcNow;
                    count++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Prefetch failed for: {Query}", pred.QueryText);
            }
        }

        CleanupExpiredPrefetches();
        return count;
    }

    public async Task<string> PrewarmChatAsync(
        string sessionId, string currentQuery, CancellationToken ct = default)
    {
        var predictions = _predictor.PredictNext(sessionId, currentQuery);
        if (predictions.Count == 0 || predictions.Max(p => p.Probability) < 0.5)
            return string.Empty;

        var bestPrediction = predictions.OrderByDescending(p => p.Probability).First();
        var prefetched = await GetOrSearchAsync(bestPrediction.QueryText, "general", ct);

        if (prefetched.Count == 0)
            return string.Empty;

        return string.Join("\n", prefetched.Select(d =>
            $"[PREFETCH: {d.Source ?? "source"} | {d.Score:F2}] {d.Content[..Math.Min(200, d.Content.Length)]}"));
    }

    public Dictionary<string, object> Stats()
    {
        return new()
        {
            ["prefetch_hits"] = Interlocked.Read(ref _prefetchHits),
            ["total_predictions"] = Interlocked.Read(ref _totalPredictions),
            ["prefetched_count"] = _prefetched.Count,
            ["predictor_hit_rate"] = Math.Round(_predictor.HitRate, 3)
        };
    }

    private void CleanupExpiredPrefetches()
    {
        var threshold = DateTimeOffset.UtcNow - PrefetchTtl;
        var expired = _prefetchTimestamps
            .Where(kv => kv.Value < threshold)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in expired)
        {
            _prefetched.TryRemove(key, out _);
            _prefetchTimestamps.TryRemove(key, out _);
        }
    }

    private static string NormalizeQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "";

        return query.Trim().ToLowerInvariant().Length > 60
            ? query.Trim().ToLowerInvariant()[..60]
            : query.Trim().ToLowerInvariant();
    }
}
