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
    /// </summary>
    public async Task<List<RankedResult>> RetrieveAndRerankAsync(
        string query,
        List<NodeRow> candidates,
        int topK = 5,
        CancellationToken ct = default)
    {
        if (candidates.Count == 0) return [];

        // Phase 1: Embedding similarity scoring (parallel)
        var queryEmb = await _embedder.GenerateAsync(query, ct);
        var embTasks = candidates.Select(n =>
        {
            var text = $"{n.Kind} {n.Name} {n.Namespace} {n.Signature}";
            return _embedder.GenerateAsync(text, ct);
        }).ToList();
        var embeddings = await Task.WhenAll(embTasks);

        var scored = candidates
            .Select((n, i) => (node: n, score: CosineSim(queryEmb, embeddings[i])))
            .OrderByDescending(x => x.score)
            .Take(topK * 2)
            .ToList();

        if (scored.Count == 0) return [];

        // Phase 2: LLM reranking
        var reranked = await RerankWithLLMAsync(query, scored, ct);

        return reranked.Take(topK).ToList();
    }

    /// <summary>
    /// LLM-based reranking: send candidates + query to LLM for precision scoring.
    /// </summary>
    public async Task<List<RankedResult>> RerankWithLLMAsync(
        string query,
        List<(NodeRow node, float embeddingScore)> candidates,
        CancellationToken ct = default)
    {
        if (candidates.Count <= 1)
            return candidates.Select((c, i) => new RankedResult(c.node, c.embeddingScore, c.embeddingScore, i + 1)).ToList();

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
            ], cancellationToken: ct);

            var text = response.Text?.Trim() ?? "";
            var scores = JsonSerializer.Deserialize<List<double>>(text);

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
                        i + 1));
                }
                return results.OrderByDescending(r => r.BlendedScore).ToList();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM reranking failed, falling back to embedding scores");
        }

        return candidates.Select((c, i) => new RankedResult(c.node, c.embeddingScore, c.embeddingScore, i + 1)).ToList();
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
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        float dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na > 0 && nb > 0 ? dot / (MathF.Sqrt(na) * MathF.Sqrt(nb)) : 0;
    }
}

/// <summary>
/// Result from the two-stage reranking pipeline.
/// </summary>
public sealed record RankedResult(
    NodeRow Node,
    float EmbeddingScore,
    float LLMScore,
    int OriginalRank)
{
    public float BlendedScore => EmbeddingScore * 0.3f + LLMScore * 0.7f;
}
