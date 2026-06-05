using LTAI.Agent.Vector;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Indexing;

public sealed class KnowledgeQualityScorer
{
    private readonly KgStore _kg;
    private readonly ILogger<KnowledgeQualityScorer> _logger;

    public KnowledgeQualityScorer(KgStore kg, ILogger<KnowledgeQualityScorer> logger)
    {
        _kg = kg;
        _logger = logger;
    }

    public async Task<int> ScoreAllAsync(CancellationToken ct = default)
    {
        var nodes = await _kg.GetNodesByKind("document").ConfigureAwait(false);
        int scored = 0;

        foreach (var node in nodes)
        {
            if (ct.IsCancellationRequested) break;

            var docs = await _kg.GetDocs(node.Id).ConfigureAwait(false);
            if (docs.Count == 0) continue;

            var text = string.Join("\n", docs.Select(d => d.Text));
            var quality = ComputeQuality(text);
            var freshness = ComputeFreshness(node.UpdatedAt);
            var relevance = ComputeRelevance(text);

            await _kg.SetScoresAsync(node.Id, quality, freshness, relevance, 0.8).ConfigureAwait(false);
            scored++;
        }

        _logger.LogInformation("Scored {N} knowledge nodes", scored);
        return scored;
    }

    public async Task ScoreNodeAsync(long nodeId)
    {
        var node = await _kg.GetNode(nodeId).ConfigureAwait(false);
        if (node == null) return;

        var docs = await _kg.GetDocs(nodeId).ConfigureAwait(false);
        var text = string.Join("\n", docs.Select(d => d.Text));
        var quality = ComputeQuality(text);
        var freshness = ComputeFreshness(node.UpdatedAt);

        await _kg.SetScoresAsync(nodeId, quality, freshness, 0.5, 0.8).ConfigureAwait(false);
    }

    private static double ComputeQuality(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;

        var score = 0.5;
        if (text.Length > 200) score += 0.15;
        if (text.Length > 1000) score += 0.1;
        if (text.Contains("```") || text.Contains("---")) score += 0.1;
        if (text.Count(c => c == '。' || c == '.') > 5) score += 0.05;
        if (text.Contains("# ") || text.Contains("## ")) score += 0.1;
        return Math.Min(1.0, score);
    }

    private static double ComputeRelevance(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var score = 0.5;
        if (text.Contains("# ") || text.Contains("## ")) score += 0.15;
        if (text.Contains("```")) score += 0.1;
        if (text.Contains("|") && text.Contains("-|")) score += 0.1;
        if (text.Length > 500) score += 0.15;
        return Math.Min(1.0, score);
    }

    private static double ComputeFreshness(string updatedAtStr)
    {
        if (!DateTime.TryParse(updatedAtStr, out var updatedAt))
            return 0.5;
        var age = DateTime.UtcNow - updatedAt;
        if (age.TotalDays < 7) return 1.0;
        if (age.TotalDays < 30) return 0.8;
        if (age.TotalDays < 90) return 0.6;
        if (age.TotalDays < 365) return 0.3;
        return 0.1;
    }
}
