using LTAI.Agent.Skills;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Workflows;

public sealed class SkillGraphMaintainer
{
    private readonly SkillGraph _graph;
    private readonly ILogger<SkillGraphMaintainer> _logger;

    private const double MergeSimilarityThreshold = 0.75;
    private const double MinCentralityForKeep = 0.05;
    private const int MinUseCountForSplit = 20;
    private const double SuccessRateDivergenceForSplit = 0.3;

    public SkillGraphMaintainer(SkillGraph graph, ILogger<SkillGraphMaintainer>? logger = null)
    {
        _graph = graph;
        _logger = logger ?? NullLogger<SkillGraphMaintainer>.Instance;
    }

    public async Task<GraphMaintenanceReport> RunMaintenanceAsync(CancellationToken ct = default)
    {
        var report = new GraphMaintenanceReport();

        var splits = await SplitDivergentNodesAsync(ct).ConfigureAwait(false);
        report.SplitsPerformed = splits;

        var merges = await MergeSimilarNodesAsync(ct).ConfigureAwait(false);
        report.MergesPerformed = merges;

        var removes = await RemoveStaleNodesAsync(ct).ConfigureAwait(false);
        report.NodesRemoved = removes;

        _graph.UpdateCentrality();

        report.FinalNodeCount = _graph.NodeCount;
        report.FinalEdgeCount = _graph.EdgeCount;

        _logger.LogInformation("Graph maintenance complete: {Splits} splits, {Merges} merges, {Removes} removes. " +
            "Final: {Nodes} nodes, {Edges} edges",
            report.SplitsPerformed, report.MergesPerformed, report.NodesRemoved,
            report.FinalNodeCount, report.FinalEdgeCount);

        return report;
    }

    private Task<int> SplitDivergentNodesAsync(CancellationToken ct)
    {
        var splitCount = 0;
        var candidates = _graph.GetAllNodes()
            .Where(n => n.UseCount >= MinUseCountForSplit)
            .ToList();

        foreach (var node in candidates)
        {
            var outgoing = _graph.GetOutgoingEdges(node.Id);
            if (outgoing.Count < 2) continue;

            var groups = new Dictionary<string, List<SkillEdge>>();

            foreach (var edge in outgoing)
            {
                var targetNode = _graph.GetNode(edge.TargetId);
                var targetName = targetNode?.Name ?? edge.TargetId;
                if (!groups.ContainsKey(targetName))
                    groups[targetName] = new List<SkillEdge>();
                groups[targetName].Add(edge);
            }

            foreach (var (targetName, edges) in groups)
            {
                if (edges.Count <= 1) continue;

                var avgReliability = edges.Average(e => e.Reliability);
                var similarEdges = outgoing
                    .Where(e => _graph.GetNode(e.TargetId)?.Name != targetName)
                    .Select(e => e.Reliability);

                if (similarEdges.Any() &&
                    Math.Abs(avgReliability - similarEdges.Average()) >= SuccessRateDivergenceForSplit)
                {
                    var newNode = new SkillNode
                    {
                        Id = $"{node.Id}_split_{targetName.ToLower().Replace(' ', '_')}",
                        Name = $"{node.Name} ({targetName})",
                        LayerLevel = node.LayerLevel,
                        Description = $"Specialized variant of {node.Name} for {targetName}",
                        Tags = node.Tags.Concat(new[] { targetName.ToLower() }).ToList(),
                        MarkdownPath = node.MarkdownPath
                    };

                    _graph.AddOrUpdateNode(newNode);

                    foreach (var edge in edges)
                    {
                        _graph.AddOrUpdateEdge(newNode.Id, edge.TargetId,
                            edge.Type, edge.Weight, edge.EvidenceCount);
                        _graph.RemoveEdge(edge.Id);
                    }

                    splitCount++;
                    _logger.LogInformation("Split node {Node} into specialized variant for {Target}: {NewNode}",
                        node.Name, targetName, newNode.Id);
                }
            }
        }

        return Task.FromResult(splitCount);
    }

    private Task<int> MergeSimilarNodesAsync(CancellationToken ct)
    {
        var mergeCount = 0;
        var nodes = _graph.GetAllNodes();
        var merged = new HashSet<string>();

        for (int i = 0; i < nodes.Count; i++)
        {
            if (merged.Contains(nodes[i].Id)) continue;

            for (int j = i + 1; j < nodes.Count; j++)
            {
                if (merged.Contains(nodes[j].Id)) continue;

                var similarity = ComputeNodeSimilarity(nodes[i], nodes[j]);
                if (similarity < MergeSimilarityThreshold) continue;

                var primary = nodes[i].UseCount >= nodes[j].UseCount ? nodes[i] : nodes[j];
                var secondary = nodes[i].UseCount < nodes[j].UseCount ? nodes[i] : nodes[j];

                primary.UseCount += secondary.UseCount;
                primary.SuccessRate = (primary.SuccessRate * primary.UseCount +
                    secondary.SuccessRate * secondary.UseCount) / (primary.UseCount + secondary.UseCount);

                foreach (var edge in _graph.GetOutgoingEdges(secondary.Id))
                {
                    _graph.AddOrUpdateEdge(primary.Id, edge.TargetId, edge.Type, edge.Weight, edge.EvidenceCount);
                }

                foreach (var edge in _graph.GetIncomingEdges(secondary.Id))
                {
                    _graph.AddOrUpdateEdge(edge.SourceId, primary.Id, edge.Type, edge.Weight, edge.EvidenceCount);
                }

                _graph.RemoveNode(secondary.Id);
                merged.Add(secondary.Id);
                mergeCount++;

                _logger.LogInformation("Merged {Secondary} into {Primary} (similarity: {Sim:F2})",
                    secondary.Name, primary.Name, similarity);
            }
        }

        return Task.FromResult(mergeCount);
    }

    private Task<int> RemoveStaleNodesAsync(CancellationToken ct)
    {
        var removeCount = 0;
        var now = DateTime.UtcNow;
        var staleThreshold = TimeSpan.FromDays(60);

        var nodes = _graph.GetAllNodes();

        foreach (var node in nodes)
        {
            var isStale = now - node.LastUsedAt > staleThreshold && node.UseCount < 3;
            var isLowCentrality = node.Centrality < MinCentralityForKeep && node.Centrality > 0;

            if (isStale || (isLowCentrality && node.UseCount < 5))
            {
                _graph.RemoveNode(node.Id);
                removeCount++;
                _logger.LogInformation("Removed stale node {Node} (centrality: {C:F3}, last used: {Last})",
                    node.Name, node.Centrality, node.LastUsedAt);
            }
        }

        return Task.FromResult(removeCount);
    }

    private static double ComputeNodeSimilarity(SkillNode a, SkillNode b)
    {
        if (a.Id == b.Id) return 1.0;

        var tagOverlap = a.Tags.Intersect(b.Tags, StringComparer.OrdinalIgnoreCase).Count();
        var totalTags = a.Tags.Union(b.Tags, StringComparer.OrdinalIgnoreCase).Count();
        var tagSimilarity = totalTags > 0 ? (double)tagOverlap / totalTags : 0;

        var nameSimilarity = ComputeStringSimilarity(a.Name, b.Name);

        var outgoingA = a.Centrality;
        var outgoingB = b.Centrality;
        var structuralSimilarity = 1.0 - Math.Abs(outgoingA - outgoingB);

        return tagSimilarity * 0.4 + nameSimilarity * 0.4 + structuralSimilarity * 0.2;
    }

    private static double ComputeStringSimilarity(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;

        var wordsA = a.ToLower().Split(' ', '_', '-').ToHashSet();
        var wordsB = b.ToLower().Split(' ', '_', '-').ToHashSet();
        var overlap = wordsA.Intersect(wordsB).Count();
        var total = wordsA.Union(wordsB).Count();

        return total > 0 ? (double)overlap / total : 0;
    }
}

public sealed class GraphMaintenanceReport
{
    public int SplitsPerformed { get; set; }
    public int MergesPerformed { get; set; }
    public int NodesRemoved { get; set; }
    public int FinalNodeCount { get; set; }
    public int FinalEdgeCount { get; set; }
}
