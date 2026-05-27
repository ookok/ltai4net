using LTAI.Agent.Skills;
using LTAI.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Workflows;

public sealed class SubgraphTaskDecomposer
{
    private readonly SkillGraph _graph;
    private readonly ILogger<SubgraphTaskDecomposer> _logger;

    public SubgraphTaskDecomposer(SkillGraph graph, ILogger<SubgraphTaskDecomposer>? logger = null)
    {
        _graph = graph;
        _logger = logger ?? NullLogger<SubgraphTaskDecomposer>.Instance;
    }

    public List<CoordinatorTask> DecomposeFromSubgraph(
        SkillSubgraph subgraph,
        string teamName,
        CancellationToken ct = default)
    {
        var tasks = new List<CoordinatorTask>();

        foreach (var nodeId in subgraph.ExecutionOrder)
        {
            var node = _graph.GetNode(nodeId);
            if (node == null) continue;

            var task = new CoordinatorTask
            {
                Id = $"sg_{teamName}_{nodeId}",
                Goal = node.Description ?? $"Execute skill: {node.Name}",
                Assignee = teamName
            };

            var prerequisiteEdges = subgraph.Edges
                .Where(e => e.Type == SkillEdgeType.Prerequisite && e.TargetId == nodeId);

            foreach (var edge in prerequisiteEdges)
            {
                task.DependsOn.Add($"sg_{teamName}_{edge.SourceId}");
            }

            tasks.Add(task);

            _logger.LogDebug("Subgraph task: {Id} ({Name}) depends on [{Deps}]",
                task.Id, node.Name, string.Join(", ", task.DependsOn));
        }

        return tasks;
    }

    public List<CoordinatorTask> DecomposeByTaskDescription(
        string taskDescription,
        string teamName,
        List<string> tags,
        int maxDepth = 5,
        CancellationToken ct = default)
    {
        var subgraph = _graph.RetrieveByTask(taskDescription, tags, maxDepth, ct);

        if (subgraph.Nodes.Count == 0)
        {
            _logger.LogWarning("No skills found for task: {Description} with tags [{Tags}]",
                taskDescription, string.Join(", ", tags));

            return new List<CoordinatorTask>
            {
                new()
                {
                    Id = $"sg_{teamName}_direct",
                    Goal = taskDescription,
                    Assignee = teamName
                }
            };
        }

        _logger.LogInformation("Decomposed task into {Count} steps from subgraph (entry: {Entry}, confidence: {Conf})",
            subgraph.ExecutionOrder.Count, subgraph.EntryPointId, subgraph.ConfidenceScore);

        return DecomposeFromSubgraph(subgraph, teamName, ct);
    }

    public List<CoordinatorTask> DecomposeFromTagGraph(
        string teamName,
        List<string> tags,
        int maxDepth = 5,
        CancellationToken ct = default)
    {
        var allNodes = _graph.GetAllNodes();
        var matchingNodes = allNodes
            .Where(n => tags.Any(t =>
                n.Tags.Contains(t, StringComparer.OrdinalIgnoreCase) ||
                n.Name.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(n => n.Centrality)
            .ToList();

        if (matchingNodes.Count == 0)
        {
            return new List<CoordinatorTask>();
        }

        var entryNode = matchingNodes[0];
        var subgraph = _graph.RetrieveSubgraph(entryNode.Id, maxDepth, ct: ct);

        foreach (var node in matchingNodes.Skip(1).Take(2))
        {
            if (!subgraph.Nodes.Any(n => n.Id == node.Id))
            {
                var connectedEdges = subgraph.Edges
                    .Where(e => e.SourceId == subgraph.EntryPointId)
                    .ToList();

                subgraph.Nodes.Add(node);
                if (!subgraph.ExecutionOrder.Contains(node.Id))
                    subgraph.ExecutionOrder.Add(node.Id);
            }
        }

        return DecomposeFromSubgraph(subgraph, teamName, ct);
    }
}
