using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Execution;

namespace LTAI.Agent.Workflows.GraphIR;

/// <summary>
/// Converts a <see cref="WorkflowGraphIR"/> to a MAF <see cref="Workflow"/>.
/// Maps agent names to <see cref="AIAgent"/> instances and builds sequential,
/// concurrent, or switch-based workflows based on the IR structure.
/// </summary>
public sealed class GraphIRToWorkflow
{
    private readonly IReadOnlyDictionary<string, AIAgent> _agentMap;

    public GraphIRToWorkflow(IEnumerable<AIAgent> agents)
    {
        _agentMap = agents.ToDictionary(a => a.Name ?? a.Id, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Converts the given IR to a MAF <see cref="Workflow"/>.
    /// Linear node chains produce sequential workflows; parallel branches produce concurrent workflows;
    /// Switch nodes produce conditional branching via edge conditions.
    /// </summary>
    public Workflow Convert(WorkflowGraphIR ir)
    {
        if (ir.Nodes.Count == 0)
            throw new ArgumentException("WorkflowGraphIR has no nodes.", nameof(ir));

        var analysis = AnalyzeGraph(ir);

        if (analysis.HasSwitch)
            return BuildSwitchWorkflow(ir, analysis);
        if (analysis.HasParallel)
            return BuildParallelWorkflow(ir, analysis);
        return BuildSequentialWorkflow(ir, analysis);
    }

    internal static GraphAnalysis AnalyzeGraph(WorkflowGraphIR ir)
    {
        var hasSwitch = ir.Nodes.Any(n => n.Type == GraphNodeType.Switch);
        var hasLoop = ir.Nodes.Any(n => n.Type == GraphNodeType.Loop);

        var edgeMap = ir.Edges.GroupBy(e => e.From).ToDictionary(g => g.Key, g => g.ToList());
        var hasParallel = false;
        var branchSources = edgeMap.Where(kvp => kvp.Value.Count > 1).ToList();
        if (branchSources.Count > 0)
        {
            hasParallel = branchSources.Any(bs => bs.Value.Count(e => e.Type == GraphEdgeType.Control) > 1);
        }

        if (!hasParallel)
        {
            var allIds = new HashSet<string>(ir.Nodes.Select(n => n.Id));
            var reachable = new HashSet<string>();
            var startNode = ir.Nodes.FirstOrDefault();
            if (startNode != null)
                WalkGraph(startNode.Id, ir.Edges, reachable);
            var unreachable = allIds.Except(reachable).ToList();
            if (unreachable.Count > 0)
                hasParallel = true;
        }

        return new GraphAnalysis
        {
            HasSwitch = hasSwitch,
            HasLoop = hasLoop,
            HasParallel = hasParallel,
            EdgeMap = edgeMap,
        };
    }

    private static void WalkGraph(string nodeId, List<GraphEdge> edges, HashSet<string> visited)
    {
        if (!visited.Add(nodeId)) return;
        foreach (var edge in edges.Where(e => string.Equals(e.From, nodeId, StringComparison.Ordinal)))
            WalkGraph(edge.To, edges, visited);
    }

    private AIAgent ResolveAgent(string agentName)
    {
        if (!_agentMap.TryGetValue(agentName, out var agent))
            throw new InvalidOperationException($"Agent '{agentName}' not found in the provided agent list.");
        return agent;
    }

    private Workflow BuildSequentialWorkflow(WorkflowGraphIR ir, GraphAnalysis analysis)
    {
        var ordered = TopologicalSort(ir);
        var agents = ordered
            .Where(n => n.Type == GraphNodeType.Agent)
            .Select(n => ResolveAgent(n.AgentName))
            .ToList();

        if (agents.Count == 0)
            throw new InvalidOperationException("No agent nodes in workflow.");

        return AgentWorkflowBuilder.BuildSequential(ir.Name, agents);
    }

    private Workflow BuildParallelWorkflow(WorkflowGraphIR ir, GraphAnalysis analysis)
    {
        var levels = ComputeLevels(ir);

        var allAgents = ir.Nodes
            .Where(n => n.Type == GraphNodeType.Agent)
            .Select(n => ResolveAgent(n.AgentName))
            .ToList();

        if (allAgents.Count == 0)
            throw new InvalidOperationException("No agent nodes in workflow.");

        var parallelGroups = levels.Where(l => l.Count > 1).ToList();
        if (parallelGroups.Count == 0)
            return AgentWorkflowBuilder.BuildSequential(ir.Name, allAgents);

        var startExecutor = new PassThroughExecutor("__start__");
        var builder = new WorkflowBuilder(startExecutor.BindExecutor());

        var bindings = new Dictionary<string, ExecutorBinding>(StringComparer.Ordinal);

        foreach (var node in ir.Nodes)
        {
            if (node.Type == GraphNodeType.Agent)
            {
                var agent = ResolveAgent(node.AgentName);
                bindings[node.Id] = agent.BindAsExecutor(new AIAgentHostOptions
                {
                    ReassignOtherAgentsAsUsers = true,
                    ForwardIncomingMessages = true,
                });
            }
        }

        if (levels.Count > 0)
        {
            var firstLevelBindings = levels[0]
                .Select(id => bindings.GetValueOrDefault(id))
                .Where(b => b != null)
                .ToList()!;

            if (firstLevelBindings.Count == 1 && firstLevelBindings[0] != null)
                builder.AddEdge(startExecutor.BindExecutor(), firstLevelBindings[0]);
            else if (firstLevelBindings.Count > 1)
                builder.AddFanOutEdge(startExecutor.BindExecutor(), firstLevelBindings!);
        }

        foreach (var edge in ir.Edges)
        {
            if (bindings.TryGetValue(edge.From, out var fromB) &&
                bindings.TryGetValue(edge.To, out var toB))
            {
                if (!string.IsNullOrEmpty(edge.Condition))
                    builder.AddEdge<string>(fromB, toB,
                        condition: msg => MatchCondition(msg, edge.Condition),
                        idempotent: true);
                else
                    builder.AddEdge(fromB, toB, idempotent: true);
            }
        }

        var terminalIds = ir.Nodes
            .Where(n => !ir.Edges.Any(e => string.Equals(e.From, n.Id, StringComparison.Ordinal)))
            .Select(n => n.Id)
            .ToList();
        var terminalBindings = terminalIds
            .Select(id => bindings.GetValueOrDefault(id))
            .Where(b => b != null)
            .ToList();
        if (terminalBindings.Count > 0)
            builder.WithOutputFrom(terminalBindings.ToArray()!);
        else if (bindings.Count > 0)
            builder.WithOutputFrom(bindings.Values.ToArray());

        return builder.Build(validateOrphans: false);
    }

    private Workflow BuildSwitchWorkflow(WorkflowGraphIR ir, GraphAnalysis analysis)
    {
        var startExecutor = new PassThroughExecutor("__switch_start__");
        var builder = new WorkflowBuilder(startExecutor.BindExecutor());

        var bindings = new Dictionary<string, ExecutorBinding>(StringComparer.Ordinal);

        foreach (var node in ir.Nodes)
        {
            if (node.Type == GraphNodeType.Agent)
            {
                var agent = ResolveAgent(node.AgentName);
                bindings[node.Id] = agent.BindAsExecutor(new AIAgentHostOptions
                {
                    ReassignOtherAgentsAsUsers = true,
                    ForwardIncomingMessages = true,
                });
            }
            else
            {
                var passThrough = new PassThroughExecutor($"node_{node.Id}");
                bindings[node.Id] = passThrough.BindExecutor();
            }
        }

        var startNodes = ir.Nodes
            .Where(n => !ir.Edges.Any(e => string.Equals(e.To, n.Id, StringComparison.Ordinal)))
            .ToList();
        if (startNodes.Count > 0)
        {
            var firstId = startNodes[0].Id;
            if (bindings.TryGetValue(firstId, out var firstBinding))
                builder.AddEdge(startExecutor.BindExecutor(), firstBinding);
        }

        foreach (var edge in ir.Edges)
        {
            if (bindings.TryGetValue(edge.From, out var fromB) &&
                bindings.TryGetValue(edge.To, out var toB))
            {
                if (!string.IsNullOrEmpty(edge.Condition))
                    builder.AddEdge<string>(fromB, toB,
                        condition: msg => MatchCondition(msg, edge.Condition),
                        label: edge.Condition,
                        idempotent: true);
                else
                    builder.AddEdge(fromB, toB, idempotent: true);
            }
        }

        var terminalIds = ir.Nodes
            .Where(n => !ir.Edges.Any(e => string.Equals(e.From, n.Id, StringComparison.Ordinal)))
            .Select(n => n.Id)
            .ToList();
        var terminalBindings = terminalIds
            .Select(id => bindings.GetValueOrDefault(id))
            .Where(b => b != null)
            .ToList();
        if (terminalBindings.Count > 0)
            builder.WithOutputFrom(terminalBindings.ToArray()!);

        return builder.Build(validateOrphans: false);
    }

    internal static bool MatchCondition(string? msg, string condition)
    {
        if (msg == null) return false;
        if (condition.Contains(".approved"))
            return msg.Contains("approved", StringComparison.OrdinalIgnoreCase);
        if (condition.Contains(".rejected"))
            return msg.Contains("rejected", StringComparison.OrdinalIgnoreCase);
        return msg.Contains(condition, StringComparison.OrdinalIgnoreCase);
    }

    internal static List<List<string>> ComputeLevels(WorkflowGraphIR ir)
    {
        var levels = new List<List<string>>();
        var remaining = new HashSet<string>(ir.Nodes.Select(n => n.Id));

        while (remaining.Count > 0)
        {
            var level = remaining
                .Where(id => !ir.Edges.Any(e =>
                    remaining.Contains(e.From) &&
                    string.Equals(e.To, id, StringComparison.Ordinal)))
                .ToList();

            if (level.Count == 0)
                level.Add(remaining.First());

            levels.Add(level);
            foreach (var id in level)
                remaining.Remove(id);
        }

        return levels;
    }

    internal static List<GraphNode> TopologicalSort(WorkflowGraphIR ir)
    {
        var sorted = new List<GraphNode>();
        var visited = new HashSet<string>();
        var visiting = new HashSet<string>();

        void Dfs(GraphNode node)
        {
            if (visited.Contains(node.Id)) return;
            if (visiting.Contains(node.Id))
            {
                System.Diagnostics.Debug.WriteLine($"GraphIR: cycle detected at node '{node.Id}', skipping");
                return;
            }
            visiting.Add(node.Id);

            foreach (var edge in ir.Edges.Where(e => string.Equals(e.From, node.Id, StringComparison.Ordinal)))
            {
                var next = ir.Nodes.FirstOrDefault(n => string.Equals(n.Id, edge.To, StringComparison.Ordinal));
                if (next != null) Dfs(next);
            }

            visiting.Remove(node.Id);
            visited.Add(node.Id);
            sorted.Add(node);
        }

        var hasInbound = new HashSet<string>(ir.Edges.Select(e => e.To));
        var startNodes = ir.Nodes
            .Where(n => !hasInbound.Contains(n.Id))
            .OrderBy(n => n.Id)
            .ToList();

        if (startNodes.Count == 0 && ir.Nodes.Count > 0)
            startNodes.Add(ir.Nodes[0]);

        foreach (var node in startNodes)
            Dfs(node);

        sorted.Reverse();
        return sorted;
    }

    internal sealed class GraphAnalysis
    {
        public bool HasSwitch { get; init; }
        public bool HasLoop { get; init; }
        public bool HasParallel { get; init; }
        public Dictionary<string, List<GraphEdge>>? EdgeMap { get; init; }
    }
}

/// <summary>
/// A pass-through executor that forwards all input messages without modification.
/// Used as a workflow entry point and for non-agent graph nodes (Switch).
/// </summary>
file sealed class PassThroughExecutor : Executor<object>
{
    public PassThroughExecutor(string id) : base(id) { }

    public override ValueTask HandleAsync(object message, IWorkflowContext context, CancellationToken cancellationToken = default)
        => default;
}