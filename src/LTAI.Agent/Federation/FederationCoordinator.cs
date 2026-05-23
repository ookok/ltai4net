using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Federation;

public enum NodeCapability { CodeGeneration, CodeReview, EIA, Reasoning, Chat, KnowledgeRetrieval, GPUInference, SandboxExecution }

public sealed record FederationNode
{
    public string NodeId { get; init; } = "";
    public string PeerId { get; init; } = "";
    public string Address { get; init; } = "";
    public List<NodeCapability> Capabilities { get; init; } = new();
    public int MaxConcurrency { get; init; } = 5;
    public int CurrentLoad { get; set; }
    public double LatencyMs { get; set; }
    public double ReliabilityScore { get; init; } = 1.0;
    public DateTime LastHeartbeat { get; set; } = DateTime.UtcNow;
    public Dictionary<string, string> Metadata { get; init; } = new();
}

public sealed record FederationTask
{
    public string TaskId { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public string SourceNodeId { get; init; } = "";
    public string Query { get; init; } = "";
    public NodeCapability RequiredCapability { get; init; } = NodeCapability.Chat;
    public string? TargetNodeId { get; set; }
    public string? Response { get; set; }
    public string Status { get; set; } = "pending";
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

public sealed class FederationCoordinator
{
    private readonly ILogger<FederationCoordinator> _logger;
    private readonly string _localNodeId;
    private readonly ConcurrentDictionary<string, FederationNode> _nodes = new();
    private readonly ConcurrentDictionary<string, FederationTask> _tasks = new();
    private readonly ConcurrentDictionary<string, string> _capabilityCache = new();
    private readonly ConcurrentDictionary<string, int> _nodeLoads = new();

    public string LocalNodeId => _localNodeId;
    public int NodeCount => _nodes.Count;

    public FederationCoordinator(ILogger<FederationCoordinator> logger)
    {
        _logger = logger;
        _localNodeId = Guid.NewGuid().ToString("N")[..8];
        RegisterLocalNode();
    }

    public void RegisterLocalNode()
    {
        var localNode = new FederationNode
        {
            NodeId = _localNodeId,
            PeerId = "local",
            Address = "localhost",
            Capabilities = new List<NodeCapability>
            {
                NodeCapability.Chat, NodeCapability.CodeGeneration,
                NodeCapability.CodeReview, NodeCapability.EIA,
                NodeCapability.Reasoning, NodeCapability.KnowledgeRetrieval,
                NodeCapability.SandboxExecution
            },
            MaxConcurrency = 10
        };

        _nodes[_localNodeId] = localNode;
        _logger.LogInformation("Federation: Registered local node {NodeId}", _localNodeId);
    }

    public void RegisterRemoteNode(FederationNode node)
    {
        _nodes[node.NodeId] = node;
        foreach (var cap in node.Capabilities)
        {
            var key = $"{cap}:{node.NodeId}";
            _capabilityCache[key] = node.NodeId;
        }
        _logger.LogInformation("Federation: Registered remote node {NodeId} with {Count} capabilities",
            node.NodeId, node.Capabilities.Count);
    }

    public void UpdateHeartbeat(string nodeId)
    {
        if (_nodes.TryGetValue(nodeId, out var node))
            node.LastHeartbeat = DateTime.UtcNow;
    }

    public FederationNode? SelectNode(NodeCapability capability, string? excludeNodeId = null)
    {
        var candidates = _nodes.Values
            .Where(n => n.Capabilities.Contains(capability) &&
                        n.NodeId != excludeNodeId &&
                        _nodeLoads.GetValueOrDefault(n.NodeId) < n.MaxConcurrency &&
                        (DateTime.UtcNow - n.LastHeartbeat) < TimeSpan.FromMinutes(5))
            .OrderBy(n => _nodeLoads.GetValueOrDefault(n.NodeId))
            .ThenByDescending(n => n.ReliabilityScore)
            .ToList();

        return candidates.FirstOrDefault();
    }

    public async Task<FederationTask> DispatchAsync(
        string query, NodeCapability capability, string? targetNodeId = null)
    {
        var task = new FederationTask
        {
            SourceNodeId = _localNodeId,
            Query = query,
            RequiredCapability = capability,
            TargetNodeId = targetNodeId
        };

        if (targetNodeId == null)
        {
            var bestNode = SelectNode(capability, _localNodeId);
            if (bestNode == null)
            {
                task.Status = "failed";
                task.Response = $"No node available for capability: {capability}";
                _logger.LogWarning("Federation: No node found for capability {Capability}", capability);
                return task;
            }
            task.TargetNodeId = bestNode.NodeId;
        }

        _tasks[task.TaskId] = task;

        if (_nodes.TryGetValue(task.TargetNodeId!, out var target))
        {
            _nodeLoads.AddOrUpdate(target.NodeId, 1, (_, v) => v + 1);
            task.Status = "dispatched";
            _logger.LogInformation("Federation: Task {TaskId} dispatched to {Node} for {Capability}",
                task.TaskId, target.NodeId, capability);
        }

        return task;
    }

    public void CompleteTask(string taskId, string response, bool success = true)
    {
        if (!_tasks.TryGetValue(taskId, out var task))
            return;

        task.Status = success ? "completed" : "failed";
        task.Response = response;
        task.CompletedAt = DateTime.UtcNow;

        if (task.TargetNodeId != null && _nodes.TryGetValue(task.TargetNodeId, out var node))
            _nodeLoads.AddOrUpdate(node.NodeId, 0, (_, v) => Math.Max(0, v - 1));

        _logger.LogInformation("Federation: Task {TaskId} {Status} by {Node}",
            taskId, task.Status, task.TargetNodeId);
    }

    public List<FederationNode> DiscoverNodes()
    {
        var alive = _nodes.Values
            .Where(n => (DateTime.UtcNow - n.LastHeartbeat) < TimeSpan.FromMinutes(10))
            .OrderBy(n => _nodeLoads.GetValueOrDefault(n.NodeId))
            .ToList();

        return alive;
    }

    public List<FederationTask> GetPendingTasks()
    {
        return _tasks.Values
            .Where(t => t.Status is "pending" or "dispatched")
            .OrderBy(t => t.CreatedAt)
            .ToList();
    }

    public Dictionary<string, object> GetStats()
    {
        return new()
        {
            ["local_node_id"] = _localNodeId,
            ["total_nodes"] = _nodes.Count,
            ["alive_nodes"] = _nodes.Values.Count(n =>
                (DateTime.UtcNow - n.LastHeartbeat) < TimeSpan.FromMinutes(5)),
            ["total_tasks"] = _tasks.Count,
            ["pending_tasks"] = _tasks.Values.Count(t => t.Status is "pending" or "dispatched"),
            ["completed_tasks"] = _tasks.Values.Count(t => t.Status == "completed"),
            ["failed_tasks"] = _tasks.Values.Count(t => t.Status == "failed"),
            ["nodes"] = _nodes.Values.Select(n => new
            {
                    n.NodeId, n.Address,
                        capabilities = n.Capabilities.Select(c => c.ToString()),
                        current_load = _nodeLoads.GetValueOrDefault(n.NodeId), n.MaxConcurrency,
                n.LatencyMs, n.ReliabilityScore,
                alive = (DateTime.UtcNow - n.LastHeartbeat) < TimeSpan.FromMinutes(5)
            }).ToList(),
            ["capability_coverage"] = Enum.GetValues<NodeCapability>()
                .ToDictionary(c => c.ToString(),
                    c => _nodes.Values.Count(n => n.Capabilities.Contains(c)))
        };
    }
}
