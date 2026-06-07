using LTAI.Agent.Vector;

namespace LTAI.Agent.SeedER;

/// <summary>
/// Path exploration engine that walks the knowledge graph along typed edges,
/// preserving full path topology for traceable multi-hop reasoning.
///
/// GoS-inspired enhancements:
/// - Supports "refines" edges (generalization → specialization hierarchy)
/// - Refinement chains are preferred in scoring (higher weight)
/// - Supports backtrack pruning: deletes sub-paths below a given level
/// - Enables FSM-controlled exploration depth
/// </summary>
public sealed class PathExplorer
{
    private readonly KgStore _store;
    private const int MaxBranchesPerNode = 8;

    // Refinement edges get higher traversal priority
    private static readonly HashSet<string> RefinementRelations = new(StringComparer.OrdinalIgnoreCase)
    {
        "refines", "contains", "part_of"
    };

    public PathExplorer(KgStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>
    /// Explore paths from seed nodes through the knowledge graph.
    /// </summary>
    /// <param name="seedIds">Starting node IDs (the "seeds").</param>
    /// <param name="maxDepth">Maximum exploration depth (1-5).</param>
    /// <param name="maxPaths">Maximum number of paths to return.</param>
    /// <param name="includeRelations">If specified, only traverse these relation types.</param>
    /// <param name="excludeRelations">If specified, skip these relation types.</param>
    /// <param name="preferRefinements">If true, refinement edges are explored first and weighted higher.</param>
    /// <param name="backtrackPruneLevel">If set, prune all paths that don't pass through this depth's top node.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<List<ExplorationPath>> ExploreAsync(
        List<long> seedIds,
        int maxDepth = 3,
        int maxPaths = 50,
        HashSet<string>? includeRelations = null,
        HashSet<string>? excludeRelations = null,
        bool preferRefinements = true,
        int? backtrackPruneLevel = null,
        CancellationToken cancellationToken = default)
    {
        maxDepth = Math.Clamp(maxDepth, 1, 5);
        maxPaths = Math.Clamp(maxPaths, 1, 200);

        var seedNodes = new List<NodeRow>();
        foreach (var id in seedIds)
        {
            var node = await _store.GetNode(id).ConfigureAwait(false);
            if (node != null) seedNodes.Add(node);
        }

        if (seedNodes.Count == 0) return [];

        // BFS: maintain frontier as list of partial paths
        var completedPaths = new List<ExplorationPath>();
        var frontier = seedNodes.Select(s => new ExplorationPath(s)).ToList();
        var visited = new HashSet<long>(seedIds);

        for (int depth = 0; depth < maxDepth; depth++)
        {
            if (frontier.Count == 0 || completedPaths.Count >= maxPaths) break;

            var nextFrontier = new List<ExplorationPath>();

            foreach (var path in frontier)
            {
                var currentNodeId = path.Target.Id;
                var edges = await _store.GetEdges(currentNodeId).ConfigureAwait(false);

                // Sort: refinements first if preferRefinements is true
                var sortedEdges = edges
                    .Where(e => ShouldTraverse(e.Relation, includeRelations, excludeRelations))
                    .OrderByDescending(e => preferRefinements && RefinementRelations.Contains(e.Relation) ? 1 : 0)
                    .ThenByDescending(e => e.Weight)
                    .Take(MaxBranchesPerNode)
                    .ToList();

                foreach (var edge in sortedEdges)
                {
                    var neighborId = edge.Src == currentNodeId ? edge.Dst : edge.Src;

                    if (path.ContainsNode(neighborId) || visited.Contains(neighborId))
                        continue;

                    var neighbor = await _store.GetNode(neighborId).ConfigureAwait(false);
                    if (neighbor == null) continue;

                    visited.Add(neighborId);

                    var newPath = new ExplorationPath(path, neighbor, edge);

                    // Apply backtrack prune: keep only paths that pass through the top node at prune level
                    if (backtrackPruneLevel.HasValue && newPath.Length > backtrackPruneLevel.Value)
                    {
                        var pruneStep = newPath.Steps.ElementAtOrDefault(backtrackPruneLevel.Value);
                        if (pruneStep != null && !IsTopAtLevel(pruneStep.Node.Id, completedPaths, depth))
                            continue; // prune this branch
                    }

                    if (depth == maxDepth - 1 || nextFrontier.Count + completedPaths.Count >= maxPaths)
                    {
                        completedPaths.Add(newPath);
                        if (completedPaths.Count >= maxPaths) break;
                    }
                    else
                    {
                        nextFrontier.Add(newPath);
                    }
                }

                if (completedPaths.Count >= maxPaths) break;
            }

            frontier = nextFrontier;
        }

        // Add remaining frontier paths
        if (completedPaths.Count < maxPaths)
        {
            foreach (var path in frontier)
            {
                completedPaths.Add(path);
                if (completedPaths.Count >= maxPaths) break;
            }
        }

        // Score and sort
        foreach (var path in completedPaths)
        {
            path.Score = ComputePathScore(path, preferRefinements);
        }

        return completedPaths.OrderByDescending(p => p.Score).Take(maxPaths).ToList();
    }

    /// <summary>Prune paths below a given depth, keeping only those through the specified node.</summary>
    public static List<ExplorationPath> PruneBelowLevel(
        List<ExplorationPath> paths, int keepDepth, long keepNodeId)
    {
        return paths.Where(p =>
        {
            if (p.Length <= keepDepth) return true;
            var step = p.Steps.ElementAtOrDefault(keepDepth);
            return step != null && step.Node.Id == keepNodeId;
        }).ToList();
    }

    private static bool IsTopAtLevel(long nodeId, List<ExplorationPath> paths, int depth)
    {
        var peersAtDepth = paths
            .Where(p => p.Length > depth)
            .Select(p => p.Steps.ElementAtOrDefault(depth))
            .Where(s => s != null)
            .ToList();

        if (peersAtDepth.Count == 0) return true;

        // Check if this node is among the top-ranked at this depth
        var topScore = peersAtDepth.Max(s => GetStepScore(s!));
        var thisScore = peersAtDepth
            .Where(s => s!.Node.Id == nodeId)
            .Select(s => GetStepScore(s!))
            .FirstOrDefault();

        return thisScore >= topScore * 0.8; // within 80% of top = still viable
    }

    private static double GetStepScore(PathStep step)
    {
        var kindBoost = step.Node.Kind switch
        {
            "method" or "function" => 1.4,
            "class" => 1.3,
            "interface" or "struct" => 1.2,
            _ => 1.0,
        };
        var edgeWeight = step.IncomingEdge?.Weight ?? 1.0;
        return edgeWeight * kindBoost;
    }

    private static bool ShouldTraverse(
        string relation,
        HashSet<string>? includeRelations,
        HashSet<string>? excludeRelations)
    {
        if (excludeRelations?.Contains(relation) == true) return false;
        if (includeRelations != null && !includeRelations.Contains(relation)) return false;
        return true;
    }

    private static double ComputePathScore(ExplorationPath path, bool preferRefinements)
    {
        if (path.Length <= 1) return 1.0;

        double product = 1.0;
        int refinementCount = 0;

        for (int i = 1; i < path.Steps.Count; i++)
        {
            var edge = path.Steps[i].IncomingEdge;
            var node = path.Steps[i].Node;
            var kindBoost = node.Kind switch
            {
                "method" or "function" => 1.4,
                "class" => 1.3,
                "interface" or "struct" => 1.2,
                _ => 1.0,
            };

            var edgeWeight = edge?.Weight ?? 1.0;

            // Boost refinement edges
            if (preferRefinements && edge != null && RefinementRelations.Contains(edge.Relation))
            {
                edgeWeight *= 1.5;
                refinementCount++;
            }

            product *= edgeWeight * kindBoost;
        }

        // Additional boost for paths with refinement chains (GoS-style hierarchy)
        if (preferRefinements && refinementCount > 0)
            product *= 1.0 + (refinementCount * 0.2);

        return product / Math.Sqrt(path.Length);
    }
}
