using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Skills;

public sealed class SkillGraph
{
    private readonly ConcurrentDictionary<string, SkillNode> _nodes = new();
    private readonly ConcurrentDictionary<string, SkillEdge> _edges = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _outgoing = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _incoming = new();
    private readonly ILogger<SkillGraph> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public SkillGraph(ILogger<SkillGraph>? logger = null)
    {
        _logger = logger ?? NullLogger<SkillGraph>.Instance;
    }

    public int NodeCount => _nodes.Count;
    public int EdgeCount => _edges.Count;

    public SkillNode AddOrUpdateNode(SkillNode node)
    {
        _nodes[node.Id] = node;
        _outgoing.TryAdd(node.Id, new HashSet<string>());
        _incoming.TryAdd(node.Id, new HashSet<string>());
        return node;
    }

    public SkillNode? GetNode(string id) =>
        _nodes.TryGetValue(id, out var node) ? node : null;

    public List<SkillNode> GetAllNodes() => _nodes.Values.ToList();

    public SkillNode? FindNodeByName(string name)
    {
        return _nodes.Values.FirstOrDefault(n =>
            n.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public List<SkillNode> FindNodesByTag(string tag)
    {
        return _nodes.Values
            .Where(n => n.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    public SkillEdge AddOrUpdateEdge(string sourceId, string targetId, SkillEdgeType type,
        double weight = 1.0, int evidenceCount = 1)
    {
        if (!_nodes.ContainsKey(sourceId) || !_nodes.ContainsKey(targetId))
            return null!;

        var existing = _edges.Values.FirstOrDefault(e =>
            e.SourceId == sourceId && e.TargetId == targetId && e.Type == type);

        if (existing != null)
        {
            existing.Weight = (existing.Weight * existing.EvidenceCount + weight * evidenceCount)
                / (existing.EvidenceCount + evidenceCount);
            existing.EvidenceCount += evidenceCount;
            existing.UpdatedAt = DateTime.UtcNow;
            return existing;
        }

        var edge = new SkillEdge
        {
            SourceId = sourceId,
            TargetId = targetId,
            Type = type,
            Weight = weight,
            EvidenceCount = evidenceCount
        };

        _edges[edge.Id] = edge;
        _outgoing[sourceId].Add(edge.Id);
        _incoming[targetId].Add(edge.Id);

        return edge;
    }

    public SkillEdge? GetEdge(string sourceId, string targetId, SkillEdgeType type)
    {
        return _edges.Values.FirstOrDefault(e =>
            e.SourceId == sourceId && e.TargetId == targetId && e.Type == type);
    }

    public List<SkillEdge> GetOutgoingEdges(string nodeId) =>
        _outgoing.TryGetValue(nodeId, out var edgeIds)
            ? edgeIds.Select(id => _edges[id]).ToList()
            : new List<SkillEdge>();

    public List<SkillEdge> GetIncomingEdges(string nodeId) =>
        _incoming.TryGetValue(nodeId, out var edgeIds)
            ? edgeIds.Select(id => _edges[id]).ToList()
            : new List<SkillEdge>();

    public List<SkillEdge> GetEdgesByType(SkillEdgeType type) =>
        _edges.Values.Where(e => e.Type == type).ToList();

    public List<SkillEdge> GetAllEdges() => _edges.Values.ToList();

    public bool RemoveNode(string nodeId)
    {
        if (!_nodes.TryRemove(nodeId, out _)) return false;

        if (_outgoing.TryRemove(nodeId, out var outEdges))
        {
            foreach (var edgeId in outEdges)
            {
                if (_edges.TryRemove(edgeId, out var edge))
                {
                    if (_incoming.TryGetValue(edge.TargetId, out var inc))
                        inc.Remove(edgeId);
                }
            }
        }

        if (_incoming.TryRemove(nodeId, out var inEdges))
        {
            foreach (var edgeId in inEdges)
            {
                if (_edges.TryRemove(edgeId, out var edge))
                {
                    if (_outgoing.TryGetValue(edge.SourceId, out var outE))
                        outE.Remove(edgeId);
                }
            }
        }

        return true;
    }

    public bool RemoveEdge(string edgeId)
    {
        if (!_edges.TryRemove(edgeId, out var edge)) return false;
        _outgoing.TryGetValue(edge.SourceId, out var outEdges);
        outEdges?.Remove(edgeId);
        _incoming.TryGetValue(edge.TargetId, out var inEdges);
        inEdges?.Remove(edgeId);
        return true;
    }

    public SkillSubgraph RetrieveSubgraph(
        string entryNodeId,
        int maxDepth = 5,
        double minWeight = 0.3,
        CancellationToken ct = default)
    {
        if (!_nodes.ContainsKey(entryNodeId))
        {
            return new SkillSubgraph
            {
                EntryPointId = entryNodeId,
                ConfidenceScore = 0
            };
        }

        var visited = new HashSet<string>();
        var subNodes = new List<SkillNode>();
        var subEdges = new List<SkillEdge>();
        var queue = new Queue<(string nodeId, int depth)>();

        queue.Enqueue((entryNodeId, 0));
        visited.Add(entryNodeId);

        while (queue.Count > 0 && !ct.IsCancellationRequested)
        {
            var (currentId, depth) = queue.Dequeue();
            var node = _nodes[currentId];
            subNodes.Add(node);

            if (depth >= maxDepth) continue;

            foreach (var edgeId in _outgoing.GetValueOrDefault(currentId, new HashSet<string>()))
            {
                var edge = _edges[edgeId];
                if (edge.Weight < minWeight) continue;

                subEdges.Add(edge);

                if (!visited.Contains(edge.TargetId))
                {
                    visited.Add(edge.TargetId);
                    queue.Enqueue((edge.TargetId, depth + 1));
                }
            }
        }

        var executionOrder = TopologicalSort(subNodes, subEdges);

        return new SkillSubgraph
        {
            Nodes = subNodes,
            Edges = subEdges,
            EntryPointId = entryNodeId,
            TotalSteps = executionOrder.Count,
            ExecutionOrder = executionOrder,
            ConfidenceScore = CalculateSubgraphConfidence(subEdges)
        };
    }

    public SkillSubgraph RetrieveByTask(
        string taskDescription,
        List<string> tags,
        int maxDepth = 5,
        CancellationToken ct = default)
    {
        var matchingNodes = _nodes.Values
            .Where(n => tags.Any(t =>
                n.Tags.Contains(t, StringComparer.OrdinalIgnoreCase) ||
                n.Description.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(n => n.UseCount * n.SuccessRate)
            .ToList();

        if (matchingNodes.Count == 0)
        {
            matchingNodes = _nodes.Values
                .OrderByDescending(n => n.Centrality)
                .Take(3)
                .ToList();
        }

        if (matchingNodes.Count == 0)
        {
            return new SkillSubgraph
            {
                EntryPointId = "",
                ConfidenceScore = 0
            };
        }

        var entryNode = matchingNodes[0];
        var sub = RetrieveSubgraph(entryNode.Id, maxDepth, 0.3, ct);

        foreach (var node in matchingNodes.Skip(1))
        {
            if (sub.Nodes.Any(n => n.Id == node.Id)) continue;

            var preEdges = GetIncomingEdges(node.Id)
                .Where(e => e.Type == SkillEdgeType.Prerequisite && sub.Nodes.Any(n => n.Id == e.SourceId))
                .ToList();

            if (preEdges.Count == 0) continue;

            foreach (var edge in preEdges)
            {
                if (!sub.Edges.Any(e => e.Id == edge.Id))
                    sub.Edges.Add(edge);
            }

            if (!sub.Nodes.Any(n => n.Id == node.Id))
            {
                sub.Nodes.Add(node);
                if (!sub.ExecutionOrder.Contains(node.Id))
                    sub.ExecutionOrder.Add(node.Id);
            }
        }

        return sub;
    }

    public void UpdateCentrality()
    {
        foreach (var nodeId in _nodes.Keys)
        {
            var outDegree = _outgoing.GetValueOrDefault(nodeId, new HashSet<string>()).Count;
            var inDegree = _incoming.GetValueOrDefault(nodeId, new HashSet<string>()).Count;
            var totalDegree = outDegree + inDegree;
            var maxDegree = Math.Max(1, _nodes.Keys.Max(id =>
                (_outgoing.GetValueOrDefault(id, new()).Count + _incoming.GetValueOrDefault(id, new()).Count)));

            if (_nodes.TryGetValue(nodeId, out var node))
            {
                node.Centrality = (double)totalDegree / maxDegree;
            }
        }
    }

    private List<string> TopologicalSort(List<SkillNode> nodes, List<SkillEdge> edges)
    {
        var nodeIds = new HashSet<string>(nodes.Select(n => n.Id));
        var inDegree = new Dictionary<string, int>();
        var adjacency = new Dictionary<string, List<string>>();

        foreach (var nodeId in nodeIds)
        {
            inDegree[nodeId] = 0;
            adjacency[nodeId] = new List<string>();
        }

        foreach (var edge in edges.Where(e => e.Type == SkillEdgeType.Prerequisite))
        {
            if (!nodeIds.Contains(edge.SourceId) || !nodeIds.Contains(edge.TargetId))
                continue;

            adjacency[edge.SourceId].Add(edge.TargetId);
            inDegree[edge.TargetId] = inDegree.GetValueOrDefault(edge.TargetId) + 1;
        }

        var sorted = new List<string>();
        var queue = new Queue<string>();

        foreach (var (id, degree) in inDegree)
        {
            if (degree == 0) queue.Enqueue(id);
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            sorted.Add(current);

            foreach (var neighbor in adjacency.GetValueOrDefault(current, new List<string>()))
            {
                inDegree[neighbor]--;
                if (inDegree[neighbor] == 0)
                    queue.Enqueue(neighbor);
            }
        }

        foreach (var nodeId in nodeIds)
        {
            if (!sorted.Contains(nodeId))
                sorted.Add(nodeId);
        }

        return sorted;
    }

    private static double CalculateSubgraphConfidence(List<SkillEdge> edges)
    {
        if (edges.Count == 0) return 0.5;
        return edges.Average(e =>
            e.Weight * (e.Reliability > 0 ? e.Reliability : 0.5));
    }

    public async Task SaveAsync(string filePath, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var data = new
        {
            nodes = _nodes.Values.ToList(),
            edges = _edges.Values.ToList(),
            savedAt = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(data, JsonOpts);
        await File.WriteAllTextAsync(filePath, json, ct).ConfigureAwait(false);
    }

    public static async Task<SkillGraph> LoadAsync(string filePath,
        ILogger<SkillGraph>? logger = null, CancellationToken ct = default)
    {
        var graph = new SkillGraph(logger);

        if (!File.Exists(filePath)) return graph;

        try
        {
            var json = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("nodes", out var nodesEl))
            {
                foreach (var nodeEl in nodesEl.EnumerateArray())
                {
                    var node = JsonSerializer.Deserialize<SkillNode>(nodeEl.GetRawText(), JsonOpts);
                    if (node != null) graph.AddOrUpdateNode(node);
                }
            }

            if (root.TryGetProperty("edges", out var edgesEl))
            {
                foreach (var edgeEl in edgesEl.EnumerateArray())
                {
                    var edge = JsonSerializer.Deserialize<SkillEdge>(edgeEl.GetRawText(), JsonOpts);
                    if (edge != null)
                    {
                        graph._edges[edge.Id] = edge;
                        graph._outgoing.TryAdd(edge.SourceId, new HashSet<string>());
                        graph._incoming.TryAdd(edge.TargetId, new HashSet<string>());
                        graph._outgoing[edge.SourceId].Add(edge.Id);
                        graph._incoming[edge.TargetId].Add(edge.Id);
                    }
                }
            }

            graph.UpdateCentrality();
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to load skill graph from {Path}", filePath);
        }

        return graph;
    }
}
