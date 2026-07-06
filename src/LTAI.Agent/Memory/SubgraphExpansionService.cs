using System.Text.RegularExpressions;
using LTAI.Agent.Vector;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Memory;

public sealed record ExpandedEvidence(
    string Source,
    string Content,
    double Relevance)
{
    public static readonly ExpandedEvidence None = new("", "", 0);
}

public sealed partial class SubgraphExpansionService
{
    private readonly KgStore? _kgStore;
    private readonly ILogger<SubgraphExpansionService>? _logger;
    private const int MaxNodesPerSearch = 3;
    private const int MaxTraversalDepth = 2;
    private const int MaxTraversalNodes = 5;

    public SubgraphExpansionService(KgStore? kgStore = null,
        ILogger<SubgraphExpansionService>? logger = null)
    {
        _kgStore = kgStore;
        _logger = logger;
    }

    public async Task<List<ExpandedEvidence>> ExpandAsync(
        IReadOnlyList<PalaceStore.Drawer> drawers,
        string query,
        CancellationToken ct = default)
    {
        if (_kgStore == null || drawers.Count == 0)
            return [];

        var entities = ExtractEntities(drawers);
        if (entities.Count == 0) return [];

        var evidence = new List<ExpandedEvidence>();

        foreach (var entity in entities.Take(MaxNodesPerSearch))
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var ftsResults = await _kgStore.SearchFts(entity, topN: 2).ConfigureAwait(false);
                if (ftsResults.Count == 0) continue;

                foreach (var (nodeId, text, rank, kind) in ftsResults)
                {
                    var nodes = await _kgStore.TraverseBfs(
                        [nodeId], maxDepth: MaxTraversalDepth, maxNodes: MaxTraversalNodes)
                        .ConfigureAwait(false);

                    var related = string.Join("; ", nodes
                        .Where(n => n.Id != nodeId)
                        .Select(n => $"{n.Kind}:{n.Name}"));

                    var rel = 0.5 * rank;
                    var content = $"[graph:{entity}] {kind}:{text}";
                    if (related.Length > 0)
                        content += $" → {{{related}}}";

                    evidence.Add(new ExpandedEvidence("graph", content, rel));
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "SubgraphExpansion: entity '{Entity}' search failed", entity);
            }
        }

        return evidence;
    }

    private static List<string> ExtractEntities(IReadOnlyList<PalaceStore.Drawer> drawers)
    {
        var entities = new HashSet<string>();
        foreach (var d in drawers)
        {
            if (string.IsNullOrWhiteSpace(d.Content)) continue;
            foreach (Match m in EntityPattern().Matches(d.Content))
            {
                var e = m.Groups[1].Value.Trim();
                if (e.Length >= 3 && e.Length <= 60)
                    entities.Add(e);
            }
        }
        return entities.ToList();
    }

    [GeneratedRegex(@"\b([A-Z][a-z]+(?:\s+[A-Z][a-z]+)+)\b", RegexOptions.Compiled, 500)]
    private static partial Regex EntityPattern();
}
