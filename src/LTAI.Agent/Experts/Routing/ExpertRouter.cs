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
        var topList = embeddingTop.ToList();

        if (topList.Count == 0)
            return new ExpertSelectionResult([], "No embedding match.");

        var top1Score = topList[0].Score;
        var margin = topList.Count >= 2 ? topList[0].Score - topList[1].Score : 1f;

        if (top1Score >= EmbeddingConfidentMinScore && margin >= EmbeddingConfidentMargin)
        {
            var selections = topList.Take(MaxExpertsPerQuery)
                .Select(s => new ExpertSelection(s.Expert.ExpertId, s.Score, "Embedding confident"))
                .ToList();
            return new ExpertSelectionResult(selections, $"Embedding routing (score={top1Score:F2}, margin={margin:F2}).");
        }

        // Ambiguous: return wider top-K with adjusted confidence
        _logger?.LogDebug("ExpertRouter: embedding ambiguous (score={Score:F2}, margin={Margin:F2}), returning wide top-K",
            top1Score, margin);
        var wideSelections = topList.Take(Math.Min(topList.Count, MaxExpertsPerQuery + 1))
            .Select(s => new ExpertSelection(s.Expert.ExpertId, s.Score * 0.8f, "Embedding (ambiguous, wide top-K)"))
            .ToList();
        return new ExpertSelectionResult(wideSelections, $"Embedding routing (ambiguous, score={top1Score:F2}).");
    }
}
