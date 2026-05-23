using LTAI.Agent.Workflows;
using LTAI.Planning.HTN;
using LTAI.Planning.Trace;
using LTAI.Knowledge.Memory;
using LTAI.Agent.Federation;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LTAI.Tests;

public class InnovationTests
{
    [Fact]
    public void HTNPlanner_DecomposeSimpleTask_ReturnsPlan()
    {
        var planner = new HTNPlanner(NullLogger<HTNPlanner>.Instance);
        var plan = planner.DecomposeTask("analyze the code quality", "code",
            new List<string> { "code_analyze", "km_search", "shell" });

        Assert.NotNull(plan);
        Assert.Equal("code", plan.Name);
        Assert.True(plan.Children.Count > 0);
    }

    [Fact]
    public void HTNPlanner_StoreAndReuse_Succeeds()
    {
        var planner = new HTNPlanner(NullLogger<HTNPlanner>.Instance);
        var plan = planner.DecomposeTask("build the project", "build",
            new List<string> { "code_build:run", "shell" });

        planner.StorePlan(plan, true);

        var template = planner.FindBestTemplate("build the project", "build");
        Assert.NotNull(template);
        Assert.Equal("build", template!.Domain);
    }

    [Fact]
    public void HTNPlanner_GetStats_ReturnsData()
    {
        var planner = new HTNPlanner(NullLogger<HTNPlanner>.Instance);
        var plan = planner.DecomposeTask("test", "test", new List<string> { "code_test:run" });
        planner.StorePlan(plan, true);

        var stats = planner.GetStats();
        Assert.True((int)stats["total_plans"] > 0);
    }

    [Fact]
    public void TraceCollector_StartAndComplete_CreatesTrace()
    {
        var collector = new TraceCollector();
        var trace = collector.StartTrace("s1", "test query");
        Assert.NotNull(trace.TraceId);

        collector.CompleteTrace(trace.TraceId, "response", 0.9, "PASS", 100);
        var retrieved = collector.GetTrace(trace.TraceId);
        Assert.NotNull(retrieved);
        Assert.Equal("PASS", retrieved!.Verdict);
    }

    [Fact]
    public void TraceCollector_AddMultipleSteps_TracksAll()
    {
        var collector = new TraceCollector();
        var trace = collector.StartTrace("s2", "test");

        collector.RecordIntentRouting(trace.TraceId, "code", "code", 0.8f, "bug,fix", "ProceduralHowTo");
        collector.RecordToolCall(trace.TraceId, "code", "code_analyze", "input", "output", 50, true);
        collector.RecordKnowledgeRetrieval(trace.TraceId, "code", "how to fix bug", "fts5", 3, 20);
        collector.CompleteTrace(trace.TraceId, "done", 0.85, "PASS", 200);

        var retrieved = collector.GetTrace(trace.TraceId);
        Assert.Equal(3, retrieved!.Steps.Count);
    }

    [Fact]
    public void TraceCollector_BuildDecisionTree_ReturnsText()
    {
        var collector = new TraceCollector();
        var trace = collector.StartTrace("s3", "analyze code");
        collector.RecordIntentRouting(trace.TraceId, "code", "code", 0.9f, "analyze", "SemanticConcept");
        collector.CompleteTrace(trace.TraceId, "done", 0.9, "PASS", 50);

        var tree = collector.BuildDecisionTree(trace.TraceId);
        Assert.Contains("Decision Trace", tree);
        Assert.Contains("analyze code", tree);
    }

    [Fact]
    public void TraceCollector_GetRecentTraces_ReturnsOrdered()
    {
        var collector = new TraceCollector();
        for (int i = 0; i < 5; i++)
        {
            var t = collector.StartTrace("s", $"query{i}");
            collector.CompleteTrace(t.TraceId, "ok", 0.8, "PASS", 10);
        }

        var recent = collector.GetRecentTraces(3);
        Assert.Equal(3, recent.Count);
    }

    [Fact]
    public async Task TemporalMemoryFabric_RecordAndQuery_ReturnsResults()
    {
        var memory = new TemporalMemoryFabric(NullLogger<TemporalMemoryFabric>.Instance);
        var evt = new MemoryEvent
        {
            SessionId = "s1", AgentName = "code",
            UserQuery = "analyze the code in Program.cs",
            FilePath = "Program.cs", Importance = 0.8
        };
        memory.RecordEvent(evt);

        var history = memory.GetSessionHistory("s1");
        Assert.Single(history);
    }

    [Fact]
    public void TemporalMemoryFabric_QueryTimeRange_FiltersCorrectly()
    {
        var memory = new TemporalMemoryFabric(NullLogger<TemporalMemoryFabric>.Instance);
        memory.RecordEvent(new MemoryEvent { SessionId = "s1", UserQuery = "old event",
            Timestamp = DateTime.UtcNow.AddDays(-10) });
        memory.RecordEvent(new MemoryEvent { SessionId = "s1", UserQuery = "recent event" });

        var recent = memory.QueryTimeRange(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);
        Assert.Single(recent);
    }

    [Fact]
    public void TemporalMemoryFabric_GetStats_ReturnsData()
    {
        var memory = new TemporalMemoryFabric(NullLogger<TemporalMemoryFabric>.Instance);
        memory.RecordEvent(new MemoryEvent { SessionId = "s1", UserQuery = "test" });

        var stats = memory.GetStats();
        Assert.Equal(1, stats["total_events"]);
    }

    [Fact]
    public void FederationCoordinator_RegisterLocalNode_Succeeds()
    {
        var fed = new FederationCoordinator(NullLogger<FederationCoordinator>.Instance);
        Assert.Equal(1, fed.NodeCount);
    }

    [Fact]
    public void FederationCoordinator_RegisterRemoteNode_Discovers()
    {
        var fed = new FederationCoordinator(NullLogger<FederationCoordinator>.Instance);
        fed.RegisterRemoteNode(new FederationNode
        {
            NodeId = "remote1", Address = "remote:8080",
            Capabilities = new List<NodeCapability> { NodeCapability.CodeGeneration, NodeCapability.GPUInference }
        });

        var nodes = fed.DiscoverNodes();
        Assert.Equal(2, nodes.Count);
    }

    [Fact]
    public async Task FederationCoordinator_DispatchTask_SelectsNode()
    {
        var fed = new FederationCoordinator(NullLogger<FederationCoordinator>.Instance);
        fed.RegisterRemoteNode(new FederationNode
        {
            NodeId = "code-node", Address = "code:8080",
            Capabilities = new List<NodeCapability> { NodeCapability.CodeGeneration }
        });

        var task = await fed.DispatchAsync("write a hello world", NodeCapability.CodeGeneration);
        Assert.Equal("dispatched", task.Status);
        Assert.Equal("code-node", task.TargetNodeId);
    }

    [Fact]
    public async Task FederationCoordinator_NoCapableNode_Fails()
    {
        var fed = new FederationCoordinator(NullLogger<FederationCoordinator>.Instance);
        var task = await fed.DispatchAsync("run gpu inference", NodeCapability.GPUInference);
        Assert.Equal("failed", task.Status);
    }

    [Fact]
    public void FederationCoordinator_CompleteTask_UpdatesStatus()
    {
        var fed = new FederationCoordinator(NullLogger<FederationCoordinator>.Instance);
        fed.RegisterRemoteNode(new FederationNode
        {
            NodeId = "worker", Address = "worker:8080",
            Capabilities = new List<NodeCapability> { NodeCapability.Chat }
        });

        var task = fed.DispatchAsync("hello", NodeCapability.Chat).Result;
        fed.CompleteTask(task.TaskId, "hi there", true);

        var stats = fed.GetStats();
        Assert.Equal(1, stats["completed_tasks"]);
    }

    [Fact]
    public void FederationCoordinator_GetStats_ReturnsComplete()
    {
        var fed = new FederationCoordinator(NullLogger<FederationCoordinator>.Instance);
        fed.RegisterRemoteNode(new FederationNode
        {
            NodeId = "n1", Address = "n1:8080",
            Capabilities = new List<NodeCapability> { NodeCapability.EIA, NodeCapability.Reasoning }
        });

        var stats = fed.GetStats();
        Assert.Equal(2, stats["total_nodes"]);
        Assert.NotNull(stats["capability_coverage"]);
    }
}
