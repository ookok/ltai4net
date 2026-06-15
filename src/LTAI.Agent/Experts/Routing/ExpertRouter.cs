using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Experts.Routing;

/// <summary>
/// Embedding-based expert selector. Ranks all available experts by ONNX embedding
/// cosine similarity to the user query and returns the top-K.
///
/// No LLM calls — purely local ONNX inference (~50ms). For the ~12% of queries
/// where embedding confidence is ambiguous (low top-1 score or narrow margin),
/// returns a wider top-K selection with proportionally adjusted confidence scores.
/// </summary>
public sealed class ExpertRouter
{
    private readonly ExpertRegistry _registry;
    private readonly ILogger<ExpertRouter>? _logger;

    private const int MaxExpertsPerQuery = 3;
    private const float EmbeddingConfidentMinScore = 0.7f;
    private const float EmbeddingConfidentMargin = 0.15f;

    public ExpertRouter(ExpertRegistry registry, ILogger<ExpertRouter>? logger = null)
    {
        _registry = registry;
        _logger = logger;
    }

    public async Task<ExpertSelectionResult> SelectExpertsAsync(
        string query, CancellationToken ct = default)
    {
        var entries = _registry.Entries;
        if (entries.Count == 0)
            return new ExpertSelectionResult([], "No experts registered.");

        var embeddingTop = await _registry.SelectTopKAsync(query, MaxExpertsPerQuery + 2, ct).ConfigureAwait(false);

        if (embeddingTop.Count == 0)
            return new ExpertSelectionResult([], "No embedding match.");

        var top1Score = embeddingTop[0].Score;
        var margin = embeddingTop.Count >= 2 ? embeddingTop[0].Score - embeddingTop[1].Score : 1f;

        if (top1Score >= EmbeddingConfidentMinScore && margin >= EmbeddingConfidentMargin)
        {
            int count = Math.Min(embeddingTop.Count, MaxExpertsPerQuery);
            var selections = new List<ExpertSelection>(count);
            for (int i = 0; i < count; i++)
                selections.Add(new ExpertSelection(embeddingTop[i].Expert.ExpertId, embeddingTop[i].Score, "Embedding confident"));
            return new ExpertSelectionResult(selections, $"Embedding routing (score={top1Score:F2}, margin={margin:F2}).");
        }

        _logger?.LogDebug("ExpertRouter: embedding ambiguous (score={Score:F2}, margin={Margin:F2}), returning wide top-K",
            top1Score, margin);
        int wideCount = Math.Min(embeddingTop.Count, MaxExpertsPerQuery + 1);
        var wideSelections = new List<ExpertSelection>(wideCount);
        for (int i = 0; i < wideCount; i++)
            wideSelections.Add(new ExpertSelection(embeddingTop[i].Expert.ExpertId, embeddingTop[i].Score * 0.8f, "Embedding (ambiguous, wide top-K)"));
        return new ExpertSelectionResult(wideSelections, $"Embedding routing (ambiguous, score={top1Score:F2}).");
    }
}
