using LiteDB;
using LTAI.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Vector;

/// <summary>
/// Two-stage retriever: BM25/Embedding recall → LLM rerank → final ranked results.
/// Uses embedding similarity for initial recall, then LLM for precision re-scoring.
/// </summary>
public sealed class Reranker
{
    private readonly EmbeddingClient _embedder;
    private readonly IChatClient _llm;
    private readonly ILogger<Reranker> _logger;

    public Reranker(EmbeddingClient embedder, IChatClient llm, ILogger<Reranker> logger)
    {
        _embedder = embedder;
        _llm = llm;
        _logger = logger;
    }

    /// <summary>
    /// Retrieve + rerank: get candidates by embedding similarity, then re-score with LLM.
    /// </summary>
    public async Task<List<RankedResult>> RetrieveAndRerankAsync(
        string query,
        List<BsonDocument> candidates,
        string? contextField = null,
        int topK = 5,
        CancellationToken ct = default)
    {
        if (candidates.Count == 0) return [];

        // Phase 1: Embedding similarity scoring
        var queryEmb = await _embedder.GenerateAsync(query, ct);
        var scored = candidates
            .Select(d =>
            {
                var embField = d.ContainsKey("v") ? "v" : null;
                float score = 0;
                if (embField != null)
                {
                    var vec = d[embField].AsArray.Select(x => (float)x.AsDouble).ToArray();
                    score = CosineSim(queryEmb, vec);
                }
                return (doc: d, score);
            })
            .OrderByDescending(x => x.score)
            .Take(topK * 2)  // get more candidates for reranking
            .ToList();

        if (scored.Count == 0) return [];

        // Phase 2: LLM reranking (for top candidates)
        var reranked = await RerankWithLLMAsync(query, scored, contextField, ct);

        return reranked.Take(topK).ToList();
    }

    /// <summary>LLM-based reranking of candidate documents.</summary>
    public async Task<List<RankedResult>> RerankWithLLMAsync(
        string query,
        List<(BsonDocument doc, float score)> candidates,
        string? contextField = null,
        CancellationToken ct = default)
    {
        if (candidates.Count <= 1)
            return candidates.Select((c, i) => new RankedResult(c.doc, c.score, c.score, i + 1)).ToList();

        // Build a prompt that asks the LLM to rank the candidates by relevance
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("You are a relevance ranker. Score each passage on a scale of 0-10 for how relevant it is to the query.");
        sb.AppendLine("Be strict: only give high scores to passages that directly answer or closely relate to the query.");
        sb.AppendLine();
        sb.AppendLine($"Query: {query}");
        sb.AppendLine();

        for (int i = 0; i < candidates.Count; i++)
        {
            var doc = candidates[i].doc;
            var text = GetDocText(doc, contextField);
            if (text.Length > 300) text = text[..300] + "...";
            sb.AppendLine($"--- Passage {i + 1} ---");
            sb.AppendLine(text);
            sb.AppendLine();
        }

        sb.AppendLine("Respond with a JSON array of scores, one per passage, in the same order:");
        sb.AppendLine("Example: [8, 3, 6, 9, 2]");
        sb.AppendLine("Scores only, no explanation.");

        try
        {
            var response = await _llm.GetResponseAsync([
                new ChatMessage(ChatRole.System, "You are a strict relevance ranker. Output only a JSON array of scores."),
                new ChatMessage(ChatRole.User, sb.ToString())
            ], cancellationToken: ct);

            var text = response.Messages?.LastOrDefault()?.Text?.Trim() ?? "";

            // Parse scores from response
            var scores = System.Text.Json.JsonSerializer.Deserialize<List<double>>(text);
            if (scores != null && scores.Count == candidates.Count)
            {
                var results = new List<RankedResult>();
                for (int i = 0; i < candidates.Count; i++)
                {
                    var llmScore = (float)Math.Clamp(scores[i] / 10.0, 0, 1);
                    // Blend embedding score + LLM score (weighted)
                    var blended = candidates[i].score * 0.3f + llmScore * 0.7f;
                    results.Add(new RankedResult(candidates[i].doc, candidates[i].score, llmScore, i + 1));
                }
                return results.OrderByDescending(r => r.BlendedScore).ToList();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM reranking failed, falling back to embedding scores");
        }

        // Fallback: return original embedding order
        return candidates.Select((c, i) => new RankedResult(c.doc, c.score, c.score, i + 1)).ToList();
    }

    private static string GetDocText(BsonDocument doc, string? field)
    {
        if (field != null && doc.ContainsKey(field))
            return doc[field].AsString ?? "";

        // Try common fields
        foreach (var f in new[] { "name", "signature", "content", "summary", "path" })
            if (doc.ContainsKey(f))
                return doc[f].AsString ?? "";

        return doc["_id"].AsString;
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

public sealed record RankedResult(
    LiteDB.BsonDocument Document,
    float EmbeddingScore,
    float LLMScore,
    int OriginalRank)
{
    public float BlendedScore => EmbeddingScore * 0.3f + LLMScore * 0.7f;
}
