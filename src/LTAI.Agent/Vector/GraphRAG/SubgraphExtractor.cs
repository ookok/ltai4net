// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  SubgraphExtractor — BFS + community detection → subgraph
//
//  Phase 4b: starting from linked entities, traverses the KgStore
//  graph to extract a relevant subgraph. Uses edge weights + kind
//  scores for community detection (merging nodes with edge weight
//  > threshold into communities).
//
//  The extracted subgraph is used by GraphContextBuilder to produce
//  the final LLM context.
// ═══════════════════════════════════════════════════════════════

namespace LTAI.Agent.Vector.GraphRAG;

/// <summary>
/// Starting from a set of linked entities (from EntityLinker),
/// traverses the KgStore graph via BFS to extract a relevant subgraph.
/// Performs community detection by merging nodes with edge weights
/// above a configurable threshold.
/// </summary>
public sealed class SubgraphExtractor
{
    private readonly KgStore _store;
    private readonly int _maxDepth;
    private readonly int _maxNodes;

    /// <summary>
    /// A single node in the extracted subgraph, with its neighborhood.
    /// </summary>
    public sealed record SubgraphNode(
        long NodeId,
        string Name,
        string Kind,
        string? Namespace,
        int Depth,
        float Score);

    /// <summary>
    /// An edge in the extracted subgraph.
    /// </summary>
    public sealed record SubgraphEdge(
        long SrcId,
        long DstId,
        string Relation,
        double Weight);

    /// <summary>
    /// A detected community: a group of closely-connected nodes.
    /// </summary>
    public sealed record Community(
        string Id,
        string Label,
        List<SubgraphNode> Members,
        double AverageWeight);

    /// <summary>
    /// The complete extracted subgraph result.
    /// </summary>
    public sealed record SubgraphResult(
        List<SubgraphNode> Nodes,
        List<SubgraphEdge> Edges,
        List<Community> Communities);

    public SubgraphExtractor(
        KgStore store,
        int maxDepth = 3,
        int maxNodes = 30)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _maxDepth = Math.Min(maxDepth, KgStore.MaxTraversalDepth);
        _maxNodes = Math.Min(maxNodes, KgStore.MaxTraversalNodes);
    }

    /// <summary>
    /// Extract a subgraph starting from linked entities.
    /// </summary>
    /// <param name="linkedEntities">Entities from EntityLinker.LinkAsync.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A SubgraphResult with nodes, edges, and communities.</returns>
    public async Task<SubgraphResult> ExtractAsync(
        List<EntityLinker.LinkedEntity> linkedEntities,
        CancellationToken ct = default)
    {
        if (linkedEntities.Count == 0)
            return new SubgraphResult([], [], []);

        var startIds = linkedEntities.Select(e => e.NodeId).Distinct().ToList();

        // BFS traversal from start nodes
        var bfsNodes = await _store.TraverseBfs(
            startIds,
            maxDepth: _maxDepth,
            maxNodes: _maxNodes)
            .ConfigureAwait(false);

        // Build node set (include start nodes even if BFS returned nothing)
        var nodeSet = new HashSet<long>(startIds);
        foreach (var n in bfsNodes) nodeSet.Add(n.Id);

        // Build nodes list
        var nodes = new List<SubgraphNode>();
        foreach (var nid in nodeSet)
        {
            var node = await _store.GetNode(nid).ConfigureAwait(false);
            if (node == null) continue;

            var depth = startIds.Contains(nid) ? 0
                : bfsNodes.FirstOrDefault(n => n.Id == nid) != null ? 1
                : _maxDepth;

            var score = linkedEntities
                .Where(e => e.NodeId == nid)
                .Select(e => e.Score)
                .DefaultIfEmpty(0.3f)
                .Max();

            nodes.Add(new SubgraphNode(
                nid, node.Name, node.Kind,
                node.Namespace, depth, score));
        }

        // Build edges list
        var edges = new List<SubgraphEdge>();
        var seenEdges = new HashSet<string>();

        foreach (var nid in nodeSet)
        {
            var nodeEdges = await _store.GetEdges(nid).ConfigureAwait(false);
            foreach (var edge in nodeEdges)
            {
                // Only include edges between nodes in our subgraph
                var otherId = edge.Src == nid ? edge.Dst : edge.Src;
                if (!nodeSet.Contains(otherId)) continue;

                var edgeKey = $"{Math.Min(nid, otherId)}:{Math.Max(nid, otherId)}:{edge.Relation}";
                if (seenEdges.Add(edgeKey))
                {
                    edges.Add(new SubgraphEdge(
                        edge.Src, edge.Dst,
                        edge.Relation, edge.Weight));
                }
            }
        }

        // Community detection: nodes with edge weight > threshold are merged
        var communities = DetectCommunities(nodes, edges);

        return new SubgraphResult(nodes, edges, communities);
    }

    /// <summary>
    /// Simple community detection: group nodes that are densely connected
    /// (edge weight > community threshold). Uses a union-find approach.
    /// </summary>
    private static List<Community> DetectCommunities(
        List<SubgraphNode> nodes,
        List<SubgraphEdge> edges)
    {
        const double communityThreshold = 0.7;
        var parent = new Dictionary<long, long>();

        long Find(long x)
        {
            if (!parent.ContainsKey(x)) parent[x] = x;
            if (parent[x] != x) parent[x] = Find(parent[x]);
            return parent[x];
        }

        void Union(long a, long b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra != rb) parent[ra] = rb;
        }

        // Union nodes connected by high-weight edges
        foreach (var edge in edges)
        {
            if (edge.Weight >= communityThreshold)
            {
                Union(edge.SrcId, edge.DstId);
            }
        }

        // Group by root
        var communities = new Dictionary<long, (List<SubgraphNode> members, double totalWeight, int edgeCount)>();
        foreach (var node in nodes)
        {
            var root = Find(node.NodeId);
            if (!communities.ContainsKey(root))
                communities[root] = (new List<SubgraphNode>(), 0, 0);
            var (list, _, _) = communities[root];
            list.Add(node);
        }

        // Compute average weight per community
        foreach (var edge in edges)
        {
            var root = Find(edge.SrcId);
            if (communities.TryGetValue(root, out var entry))
            {
                communities[root] = (entry.members, entry.totalWeight + edge.Weight, entry.edgeCount + 1);
            }
        }

        return communities
            .Select(kvp =>
            {
                var (members, totalWeight, edgeCount) = kvp.Value;
                var avgWeight = edgeCount > 0 ? totalWeight / edgeCount : 0;
                var label = members.Count <= 3
                    ? string.Join(", ", members.Select(m => m.Name))
                    : $"{members[0].Name} + {members.Count - 1} more";
                return new Community(
                    $"c_{kvp.Key}",
                    label,
                    members,
                    avgWeight);
            })
            .OrderByDescending(c => c.Members.Count)
            .ToList();
    }
}
