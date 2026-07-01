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
    /// LLM-based reranking: send candidates + query to LLM for precision scoring
    /// with Verbal-R3 verbal annotations.
    /// </summary>
    public async Task<List<RankedResult>> RerankWithLLMAsync(
        string query,
        List<(NodeRow node, float embeddingScore)> candidates,
        RerankerWeights? weights = null,
        CancellationToken ct = default)
    {
        var (results, _) = await RerankWithVerbalAnnotationsAsync(query, candidates, weights, ct)
            .ConfigureAwait(false);
        return results;
    }

    /// <summary>
    /// Verbal-R3 reranking: returns both ranked results and verbal annotations.
    /// Reference: arXiv:2605.01399 (ACL 2026)
    /// </summary>
    public async Task<(List<RankedResult> Results, VerbalAnnotationSet Annotations)> RerankWithVerbalAnnotationsAsync(
        string query,
        List<(NodeRow node, float embeddingScore)> candidates,
        RerankerWeights? weights = null,
        CancellationToken ct = default)
    {
        var effectiveWeights = weights ?? new RerankerWeights();
        var annotationsList = new List<VerbalAnnotation>();

        if (candidates.Count <= 1)
        {
            var single = candidates.Select((c, i) =>
            {
                var ann = new VerbalAnnotation
                {
                    Score = c.embeddingScore,
                    Rationale = "单一候选，无法对比评分",
                    Confidence = AnnotationConfidence.Medium,
                    SourceId = $"{c.node.Kind}:{c.node.Name}"
                };
                annotationsList.Add(ann);
                return new RankedResult(c.node, c.embeddingScore, c.embeddingScore, i + 1, effectiveWeights) { Annotation = ann };
            }).ToList();
            return (single, MakeSet(query, annotationsList));
        }

        var sb = new StringBuilder();
        sb.AppendLine("For each passage, provide a relevance score (0-10) and a verbal rationale explaining WHY it is (or isn't) relevant to the query. Be strict and analytic.");
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

        sb.AppendLine("Respond with a JSON array of objects, one per passage, in order.");
        sb.AppendLine(@"Example: [{\""score\"": 8, \""rationale\"": \""该段描述了 X 的实现方法，与查询 Y 直接相关\"", \""confidence\"": \""high\"", \""suggestion\"": \""引用为主要证据\""}]");

        try
        {
            var response = await _llm.GetResponseAsync([
                new ChatMessage(ChatRole.System,
                    "You are a strict relevance ranker with verbal reasoning. Output only a JSON array of {score, rationale, confidence, suggestion} objects."),
                new ChatMessage(ChatRole.User, sb.ToString())
            ], cancellationToken: ct).ConfigureAwait(false);

            var text = response.Text?.Trim() ?? "";
            List<AnnotationResponseItem>? items;
            try
            {
                items = JsonSerializer.Deserialize<List<AnnotationResponseItem>>(text);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Reranker: LLM returned invalid JSON annotations, falling back to scores");
                items = null;
            }

            if (items != null && items.Count == candidates.Count)
            {
                var results = new List<RankedResult>();
                for (int i = 0; i < candidates.Count; i++)
                {
                    var llmScore = (float)Math.Clamp(items[i].Score / 10.0, 0, 1);
                    var confidence = ParseConfidence(items[i].Confidence);
                    var ann = new VerbalAnnotation
                    {
                        Score = llmScore,
                        Rationale = items[i].Rationale ?? "",
                        Confidence = confidence,
                        Suggestion = items[i].Suggestion,
                        SourceId = $"{candidates[i].node.Kind}:{candidates[i].node.Name}"
                    };
                    annotationsList.Add(ann);
                    results.Add(new RankedResult(
                        candidates[i].node,
                        candidates[i].embeddingScore,
                        llmScore,
                        i + 1,
                        effectiveWeights) { Annotation = ann });
                }
                var sorted = results.OrderByDescending(r => r.BlendedScore).ToList();
                return (sorted, MakeSet(query, sorted
                    .Select(r => r.Annotation)
                    .Where(a => a != null)
                    .Cast<VerbalAnnotation>()
                    .ToList()));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Verbal-R3 reranking failed, falling back to embedding scores");
        }

        // Fallback: create annotations from embedding scores
        var fallbackResults = candidates.Select((c, i) =>
        {
            var ann = new VerbalAnnotation
            {
                Score = c.embeddingScore,
                Rationale = "LLM reranking不可用，使用嵌入相似度作为代理",
                Confidence = AnnotationConfidence.Low,
                SourceId = $"{c.node.Kind}:{c.node.Name}"
            };
            annotationsList.Add(ann);
            return new RankedResult(c.node, c.embeddingScore, c.embeddingScore, i + 1, effectiveWeights) { Annotation = ann };
        }).ToList();
        return (fallbackResults, MakeSet(query, annotationsList));
    }

    private static VerbalAnnotationSet MakeSet(string query, List<VerbalAnnotation> annotations)
        => new() { Query = query, Annotations = annotations };

    private static AnnotationConfidence ParseConfidence(string? confidence)
        => confidence?.ToLowerInvariant() switch
        {
            "high" => AnnotationConfidence.High,
            "medium" => AnnotationConfidence.Medium,
            "low" => AnnotationConfidence.Low,
            _ => AnnotationConfidence.Medium
        };

    /// <summary>Internal DTO for JSON deserialization of verbal annotation items.</summary>
    private sealed record AnnotationResponseItem(
        double Score,
        string? Rationale,
        string? Confidence,
        string? Suggestion);

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

    /// <summary>Optional Verbal-R3 verbal annotation for this result.</summary>
    public VerbalAnnotation? Annotation { get; init; }
}
