namespace LTAI.Agent.Memory;

public sealed class AdaptiveBeamTraverser
{
    private readonly MultiGraphStore _store;
    private const int BeamWidth = 3;
    private const int MaxDepth = 5;

    private static readonly Dictionary<QueryIntent, double[]> EdgeWeights = new()
    {
        [QueryIntent.Why]   = [1.0, 4.0, 1.5, 2.0],  // temp, causal, sem, entity
        [QueryIntent.When]  = [4.0, 1.0, 1.5, 1.0],
        [QueryIntent.Who]   = [1.0, 1.5, 2.0, 5.0],
        [QueryIntent.Where] = [1.5, 1.0, 2.5, 2.0],
        [QueryIntent.How]   = [1.0, 1.5, 2.5, 1.0],
        [QueryIntent.What]  = [1.0, 1.0, 3.0, 2.0],
    };

    public AdaptiveBeamTraverser(MultiGraphStore store)
    {
        _store = store;
    }

    public List<TraversalResult> Traverse(string entryNodeId, QueryIntent intent, int topK = 10)
    {
        if (!EdgeWeights.TryGetValue(intent, out var weights))
            weights = EdgeWeights[QueryIntent.What]; // defensive fallback

        var visited = new HashSet<string> { entryNodeId };
        var beam = new List<(string NodeId, double Score)> { (entryNodeId, 1.0) };
        var results = new List<TraversalResult>();

        for (int depth = 0; depth < MaxDepth && beam.Count > 0 && results.Count < topK; depth++)
        {
            var candidates = new List<(string NodeId, double Score)>();

            foreach (var (current, parentScore) in beam)
            {
                foreach (var (neighbor, sim) in _store.Semantic.GetNeighbors(current, BeamWidth * 2))
                {
                    if (!visited.Add(neighbor)) continue;
                    var score = parentScore * weights[2] * sim;
                    candidates.Add((neighbor, score));
                }

                foreach (var (causeId, score, label) in _store.Causal.GetCauses(current, BeamWidth))
                {
                    if (!visited.Add(causeId)) continue;
                    candidates.Add((causeId, parentScore * weights[1] * score));
                }

                foreach (var (effectId, score, label) in _store.Causal.GetEffects(current, BeamWidth))
                {
                    if (!visited.Add(effectId)) continue;
                    candidates.Add((effectId, parentScore * weights[1] * score));
                }
            }

            beam = candidates
                .OrderByDescending(c => c.Score)
                .Take(BeamWidth)
                .ToList();

            foreach (var (nid, score) in beam)
            {
                var node = _store.GetNode(nid);
                if (node != null)
                    results.Add(new TraversalResult(nid, node.Value.Content, score, depth));
            }
        }

        return results.OrderByDescending(r => r.Score).Take(topK).ToList();
    }
}

public sealed record TraversalResult(string NodeId, string Content, double Score, int Depth);
