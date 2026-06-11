// Copyright (c) LTAI. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LTAI.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Vector;

/// <summary>
/// Two-stage reranker: embedding similarity → blend → LLM rescore → final ranking.
/// Works with KgStore.NodeRow instead of LiteDB.BsonDocument.
/// </summary>
public sealed class Reranker
{
    private readonly EmbeddingClient _embedder;
    private readonly IChatClient _llm;
    private readonly ILogger<Reranker> _logger;
    private readonly KgStore? _store;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    /// <param name="embedder">Embedding client for vector similarity scoring.</param>
    /// <param name="llm">LLM for precision reranking.</param>
    /// <param name="store">Optional KgStore for document text lookup.</param>
    /// <param name="logger">Logger.</param>
    public Reranker(EmbeddingClient embedder, IChatClient llm,
        KgStore? store = null, ILogger<Reranker>? logger = null)
    {
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _store = store;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<Reranker>.Instance;
    }

    /// <summary>
    /// Retrieve + rerank: score candidates by embedding similarity, then LLM rescore.
    /// Auto-selects weights based on query characteristics.
    /// </summary>
    public async Task<List<RankedResult>> RetrieveAndRerankAsync(
        string query,
        List<NodeRow> candidates,
        int topK = 5,
        RerankerWeights? weights = null,
        CancellationToken ct = default)
    {
        if (candidates.Count == 0) return [];

        // Auto-select weights if not provided
        var effectiveWeights = weights ?? RerankerWeights.AutoSelect(query);

        // Phase 1: Embedding similarity scoring (batched)
        var queryEmb = await _embedder.GenerateAsync(query, ct).ConfigureAwait(false);
        var texts = candidates.Select(n =>
            $"{n.Kind} {n.Name} {n.Namespace} {n.Signature}").ToArray();
        var embeddingsArray = await _embedder.GenerateBatchAsync(texts, ct).ConfigureAwait(false);
        var embeddings = embeddingsArray as float[][] ?? embeddingsArray.ToArray();

        // Validate dimension consistency — cross-provider switching (ONNX 384d ↔ API 1024d)
        // silently produces meaningless cosine scores. Log and fall back to uniform ranking.
        if (embeddings.Any(e => e.Length != queryEmb.Length))
        {
            _logger.LogWarning("Reranker: embedding dimension mismatch (query={QD}, candidates vary). " +
                "Possible model switch without cache invalidation. Falling back to uniform ranking.",
                queryEmb.Length);
            return candidates.Select((n, i) => new RankedResult(n, 0.5f, 0.5f, i + 1, effectiveWeights)).Take(topK).ToList();
        }

        var scored = candidates
            .Select((n, i) => (node: n, score: CosineSim(queryEmb, embeddings[i])))
            .OrderByDescending(x => x.score)
            .Take(topK * 2)
            .ToList();

        if (scored.Count == 0) return [];

        // Phase 2: LLM reranking
        var reranked = await RerankWithLLMAsync(query, scored, effectiveWeights, ct).ConfigureAwait(false);

        return reranked.Take(topK).ToList();
    }

    /// <summary>
    /// LLM-based reranking: send candidates + query to LLM for precision scoring.
    /// </summary>
    public async Task<List<RankedResult>> RerankWithLLMAsync(
        string query,
        List<(NodeRow node, float embeddingScore)> candidates,
        RerankerWeights? weights = null,
        CancellationToken ct = default)
    {
        var effectiveWeights = weights ?? new RerankerWeights();

        if (candidates.Count <= 1)
            return candidates.Select((c, i) => new RankedResult(c.node, c.embeddingScore, c.embeddingScore, i + 1, effectiveWeights)).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("Score each passage 0-10 for relevance to the query. Be strict.");
        sb.AppendLine();
        sb.AppendLine($"Query: {query}");
        sb.AppendLine();

        for (int i = 0; i < candidates.Count; i++)
        {
            var node = candidates[i].node;
            var text = GetNodeText(node);
            if (text.Length > 300) text = text[..300] + "...";
            sb.AppendLine($"--- Passage {i + 1} ---");
            sb.AppendLine(text);
            sb.AppendLine();
        }

        sb.AppendLine("Respond with a JSON array of scores, one per passage, in order.");
        sb.AppendLine("Example: [8, 3, 6, 9, 2]");

        try
        {
            var response = await _llm.GetResponseAsync([
                new ChatMessage(ChatRole.System, "You are a strict relevance ranker. Output only a JSON array of scores."),
                new ChatMessage(ChatRole.User, sb.ToString())
            ], cancellationToken: ct).ConfigureAwait(false);

            var text = response.Text?.Trim() ?? "";
            List<double>? scores;
            try
            {
                scores = JsonSerializer.Deserialize<List<double>>(text);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Reranker: LLM returned invalid JSON scores");
                scores = null;
            }

            if (scores != null && scores.Count == candidates.Count)
            {
                var results = new List<RankedResult>();
                for (int i = 0; i < candidates.Count; i++)
                {
                    var llmScore = (float)Math.Clamp(scores[i] / 10.0, 0, 1);
                    results.Add(new RankedResult(
                        candidates[i].node,
                        candidates[i].embeddingScore,
                        llmScore,
                        i + 1,
                        effectiveWeights));
                }
                return results.OrderByDescending(r => r.BlendedScore).ToList();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM reranking failed, falling back to embedding scores");
        }

        return candidates.Select((c, i) => new RankedResult(c.node, c.embeddingScore, c.embeddingScore, i + 1, effectiveWeights)).ToList();
    }

    private static string GetNodeText(NodeRow node)
    {
        var text = $"{node.Kind} {node.Name} {node.Namespace} {node.Signature}";
        var props = node.GetProps();
        if (props != null)
        {
            foreach (var (k, v) in props)
                if (v is string s) text += $" {s}";
        }
        return text;
    }

    private static float CosineSim(float[] a, float[] b)
        => LTAI.AI.VectorMath.CosineSimilarity(a.AsSpan(), b.AsSpan());
}

/// <summary>
/// Reranking fusion weights. Configurable per query type.
/// </summary>
public sealed class RerankerWeights
{
    /// <summary>Embedding score weight (default 0.3).</summary>
    public float EmbeddingWeight { get; set; } = 0.3f;

    /// <summary>LLM score weight (default 0.7).</summary>
    public float LLMWeight { get; set; } = 0.7f;

    /// <summary>Get weights optimized for code queries (higher embedding weight).</summary>
    public static RerankerWeights ForCode => new() { EmbeddingWeight = 0.5f, LLMWeight = 0.5f };

    /// <summary>Get weights optimized for knowledge queries (higher LLM weight).</summary>
    public static RerankerWeights ForKnowledge => new() { EmbeddingWeight = 0.3f, LLMWeight = 0.7f };

    /// <summary>Get weights optimized for short queries (higher LLM weight).</summary>
    public static RerankerWeights ForShort => new() { EmbeddingWeight = 0.2f, LLMWeight = 0.8f };

    /// <summary>Get weights optimized for long queries (higher embedding weight).</summary>
    public static RerankerWeights ForLong => new() { EmbeddingWeight = 0.6f, LLMWeight = 0.4f };

    /// <summary>
    /// Auto-select weights based on query characteristics.
    /// </summary>
    public static RerankerWeights AutoSelect(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return ForKnowledge;

        // Code query detection
        if (QueryUtils.ContainsCodePattern(query))
            return ForCode;

        // Short query detection
        var wordCount = query.Split([' ', '，', '。', '、'], StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount <= 3)
            return ForShort;

        // Long query detection
        if (query.Length > 200)
            return ForLong;

        return ForKnowledge;
    }
}

/// <summary>
/// Result from the two-stage reranking pipeline.
/// </summary>
public sealed record RankedResult(
    NodeRow Node,
    float EmbeddingScore,
    float LLMScore,
    int OriginalRank,
    RerankerWeights? Weights = null)
{
    private readonly RerankerWeights _weights = Weights ?? new RerankerWeights();
    public float BlendedScore => EmbeddingScore * _weights.EmbeddingWeight + LLMScore * _weights.LLMWeight;
}
