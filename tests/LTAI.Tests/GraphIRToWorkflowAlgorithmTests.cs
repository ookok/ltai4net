using LTAI.Agent.Workflows.GraphIR;
using Xunit;

namespace LTAI.Tests;

public sealed class GraphIRToWorkflowAlgorithmTests
{
    // ═══════════════════════════════════════════
    //  MatchCondition
    // ═══════════════════════════════════════════

    [Fact]
    public void MatchCondition_NullMsg_ReturnsFalse()
    {
        Assert.False(GraphIRToWorkflow.MatchCondition(null, "anything"));
    }

    [Fact]
    public void MatchCondition_ApprovedCondition_Match()
    {
        Assert.True(GraphIRToWorkflow.MatchCondition("The review is approved", "review.approved"));
    }

    [Fact]
    public void MatchCondition_ApprovedCondition_NoMatch()
    {
        Assert.False(GraphIRToWorkflow.MatchCondition("changes were rejected", "review.approved"));
    }

    [Fact]
    public void MatchCondition_RejectedCondition_Match()
    {
        Assert.True(GraphIRToWorkflow.MatchCondition("changes rejected", "result.rejected"));
    }

    [Fact]
    public void MatchCondition_RejectedCondition_NoMatch()
    {
        Assert.False(GraphIRToWorkflow.MatchCondition("everything looks good", "result.rejected"));
    }

    [Fact]
    public void MatchCondition_GenericCondition_Match()
    {
        Assert.True(GraphIRToWorkflow.MatchCondition("has custom_flag set", "custom_flag"));
    }

    [Fact]
    public void MatchCondition_GenericCondition_NoMatch()
    {
        Assert.False(GraphIRToWorkflow.MatchCondition("no match here", "some_flag"));
    }

    // ═══════════════════════════════════════════
    //  TopologicalSort
    // ═══════════════════════════════════════════

    private static WorkflowGraphIR MakeGraph(string[] nodeIds, (string from, string to)[] edges)
    {
        var ir = new WorkflowGraphIR { Name = "test" };
        foreach (var id in nodeIds)
            ir.Nodes.Add(new GraphNode { Id = id, Type = GraphNodeType.Agent, AgentName = id });
        foreach (var (from, to) in edges)
            ir.Edges.Add(new GraphEdge { From = from, To = to, Type = GraphEdgeType.Control });
        return ir;
    }

    [Fact]
    public void TopologicalSort_LinearChain_ReturnsInOrder()
    {
        var ir = MakeGraph(["A", "B", "C"], [("A", "B"), ("B", "C")]);
        var sorted = GraphIRToWorkflow.TopologicalSort(ir);

        Assert.Equal(3, sorted.Count);
        Assert.Equal("A", sorted[0].Id);
        Assert.Equal("B", sorted[1].Id);
        Assert.Equal("C", sorted[2].Id);
    }

    [Fact]
    public void TopologicalSort_Diamond_ReturnsValidOrder()
    {
        var ir = MakeGraph(["A", "B", "C", "D"], [("A", "B"), ("A", "C"), ("B", "D"), ("C", "D")]);
        var sorted = GraphIRToWorkflow.TopologicalSort(ir);

        Assert.Equal(4, sorted.Count);
        Assert.Equal("A", sorted[0].Id);
        Assert.Equal("D", sorted[^1].Id);
    }

    [Fact]
    public void TopologicalSort_SingleNode_ReturnsNode()
    {
        var ir = MakeGraph(["A"], []);
        var sorted = GraphIRToWorkflow.TopologicalSort(ir);

        Assert.Single(sorted);
        Assert.Equal("A", sorted[0].Id);
    }

    [Fact]
    public void TopologicalSort_EmptyGraph_ReturnsEmpty()
    {
        var ir = MakeGraph([], []);
        var sorted = GraphIRToWorkflow.TopologicalSort(ir);

        Assert.Empty(sorted);
    }

    [Fact]
    public void TopologicalSort_Cycle_DoesNotCrash()
    {
        var ir = MakeGraph(["A", "B", "C"], [("A", "B"), ("B", "C"), ("C", "A")]);
        var sorted = GraphIRToWorkflow.TopologicalSort(ir);

        Assert.NotEmpty(sorted);
    }

    [Fact]
    public void TopologicalSort_Disconnected_ReturnsAll()
    {
        var ir = MakeGraph(["A", "B", "C", "D"], [("A", "B"), ("C", "D")]);
        var sorted = GraphIRToWorkflow.TopologicalSort(ir);

        Assert.Equal(4, sorted.Count);
    }

    // ═══════════════════════════════════════════
    //  ComputeLevels
    // ═══════════════════════════════════════════

    [Fact]
    public void ComputeLevels_LinearChain_SequentialLevels()
    {
        var ir = MakeGraph(["A", "B", "C"], [("A", "B"), ("B", "C")]);
        var levels = GraphIRToWorkflow.ComputeLevels(ir);

        Assert.Equal(3, levels.Count);
        Assert.Equal(["A"], levels[0]);
        Assert.Equal(["B"], levels[1]);
        Assert.Equal(["C"], levels[2]);
    }

    [Fact]
    public void ComputeLevels_Parallel_SameLevel()
    {
        var ir = MakeGraph(["A", "B", "C"], [("A", "B"), ("A", "C")]);
        var levels = GraphIRToWorkflow.ComputeLevels(ir);

        Assert.Equal(2, levels.Count);
        Assert.Equal(["A"], levels[0]);
        Assert.Equal(2, levels[1].Count);
        Assert.Contains("B", levels[1]);
        Assert.Contains("C", levels[1]);
    }

    [Fact]
    public void ComputeLevels_Diamond_ThreeLevels()
    {
        var ir = MakeGraph(["A", "B", "C", "D"], [("A", "B"), ("A", "C"), ("B", "D"), ("C", "D")]);
        var levels = GraphIRToWorkflow.ComputeLevels(ir);

        Assert.Equal(3, levels.Count);
        Assert.Equal(["A"], levels[0]);
        Assert.Equal(2, levels[1].Count);
        Assert.Equal(["D"], levels[2]);
    }

    [Fact]
    public void ComputeLevels_EmptyGraph_EmptyLevels()
    {
        var ir = MakeGraph([], []);
        var levels = GraphIRToWorkflow.ComputeLevels(ir);

        Assert.Empty(levels);
    }

    // ═══════════════════════════════════════════
    //  AnalyzeGraph
    // ═══════════════════════════════════════════

    [Fact]
    public void AnalyzeGraph_Linear_NoSwitchNoParallel()
    {
        var ir = MakeGraph(["A", "B"], [("A", "B")]);
        var result = GraphIRToWorkflow.AnalyzeGraph(ir);

        var hasSwitch = (bool)result.GetType().GetProperty("HasSwitch")!.GetValue(result)!;
        var hasParallel = (bool)result.GetType().GetProperty("HasParallel")!.GetValue(result)!;
        var hasLoop = (bool)result.GetType().GetProperty("HasLoop")!.GetValue(result)!;

        Assert.False(hasSwitch);
        Assert.False(hasParallel);
        Assert.False(hasLoop);
    }

    [Fact]
    public void AnalyzeGraph_WithSwitch_DetectsSwitch()
    {
        var ir = MakeGraph(["A", "B", "C"], [("A", "B"), ("A", "C")]);
        ir.Nodes[1].Type = GraphNodeType.Switch;
        var result = GraphIRToWorkflow.AnalyzeGraph(ir);

        var hasSwitch = (bool)result.GetType().GetProperty("HasSwitch")!.GetValue(result)!;
        Assert.True(hasSwitch);
    }

    [Fact]
    public void AnalyzeGraph_ParallelBranches_DetectsParallel()
    {
        var ir = MakeGraph(["A", "B", "C"], [("A", "B"), ("A", "C")]);
        var result = GraphIRToWorkflow.AnalyzeGraph(ir);

        var hasParallel = (bool)result.GetType().GetProperty("HasParallel")!.GetValue(result)!;
        Assert.True(hasParallel);
    }

    [Fact]
    public void AnalyzeGraph_EmptyGraph_NoFlags()
    {
        var ir = MakeGraph([], []);
        var result = GraphIRToWorkflow.AnalyzeGraph(ir);

        Assert.NotNull(result);
    }

    [Fact]
    public void AnalyzeGraph_WithLoop_DetectsLoop()
    {
        var ir = MakeGraph(["A", "B"], [("A", "B")]);
        ir.Nodes[1].Type = GraphNodeType.Loop;
        var result = GraphIRToWorkflow.AnalyzeGraph(ir);

        var hasLoop = (bool)result.GetType().GetProperty("HasLoop")!.GetValue(result)!;
        Assert.True(hasLoop);
    }
}
