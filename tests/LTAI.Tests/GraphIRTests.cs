using System.Text.Json;
using LTAI.Agent.Workflows.GraphIR;
using Xunit;

namespace LTAI.Tests;

public sealed class WorkflowGraphIRTests
{
    [Fact]
    public void SerializeDeserialize_Roundtrip()
    {
        var serializer = new GraphIRSerializer();
        var ir = new WorkflowGraphIR
        {
            Name = "test-workflow",
            Nodes =
            [
                new GraphNode { Id = "start", Type = GraphNodeType.Agent, AgentName = "LTAI-Chat" },
                new GraphNode { Id = "process", Type = GraphNodeType.Agent, AgentName = "LTAI-Data" },
                new GraphNode { Id = "decide", Type = GraphNodeType.Switch, AgentName = "router" },
                new GraphNode { Id = "end", Type = GraphNodeType.Agent, AgentName = "LTAI-Chat" },
            ],
            Edges =
            [
                new GraphEdge { From = "start", To = "process", Type = GraphEdgeType.Control },
                new GraphEdge { From = "process", To = "decide", Type = GraphEdgeType.Control },
                new GraphEdge { From = "decide", To = "end", Type = GraphEdgeType.Control, Condition = "approved" },
            ],
            Metadata = new GraphMetadata
            {
                Description = "A test workflow",
                Author = "test",
                Version = "1.0",
                Tags = ["test", "demo"],
            },
        };

        var json = serializer.ToJson(ir);
        Assert.False(string.IsNullOrEmpty(json));

        var deserialized = serializer.FromJson(json);
        Assert.Equal("test-workflow", deserialized.Name);
        Assert.Equal(4, deserialized.Nodes.Count);
        Assert.Equal(3, deserialized.Edges.Count);
        Assert.Equal("LTAI-Chat", deserialized.Nodes[0].AgentName);
        Assert.Equal(GraphNodeType.Switch, deserialized.Nodes[2].Type);
        Assert.Equal("approved", deserialized.Edges[2].Condition);
        Assert.Equal("A test workflow", deserialized.Metadata.Description);
        Assert.Contains("test", deserialized.Metadata.Tags);
    }

    [Fact]
    public void SerializeDeserialize_EmptyGraph()
    {
        var serializer = new GraphIRSerializer();
        var ir = new WorkflowGraphIR { Name = "empty" };
        var json = serializer.ToJson(ir);
        var deserialized = serializer.FromJson(json);
        Assert.Equal("empty", deserialized.Name);
        Assert.Empty(deserialized.Nodes);
        Assert.Empty(deserialized.Edges);
    }

    [Fact]
    public void Deserialize_InvalidJson_ThrowsJsonException()
    {
        var serializer = new GraphIRSerializer();
        Assert.Throws<JsonException>(() => serializer.FromJson("not valid json"));
    }
}

public sealed class ComposedWorkflowTemplateTests
{
    [Fact]
    public void Instantiate_ReplacesParameters()
    {
        var template = new ComposedWorkflowTemplate
        {
            Name = "test-template",
            Graph = new WorkflowGraphIR
            {
                Name = "test-graph",
                Nodes =
                [
                    new GraphNode { Id = "agent-{{.params.name}}", Type = GraphNodeType.Agent, AgentName = "{{.params.name}}" },
                ],
                Edges =
                [
                    new GraphEdge { From = "start", To = "agent-{{.params.name}}", Condition = "{{.params.condition}}" },
                ],
            },
        };

        var args = new Dictionary<string, string>
        {
            ["name"] = "LTAI-Code",
            ["condition"] = "true",
        };

        var ir = template.Instantiate(args);
        Assert.Equal("test-graph", ir.Name);
        var node = Assert.Single(ir.Nodes);
        Assert.Equal("agent-LTAI-Code", node.Id);
        Assert.Equal("LTAI-Code", node.AgentName);
        var edge = Assert.Single(ir.Edges);
        Assert.Equal("agent-LTAI-Code", edge.To);
        Assert.Equal("true", edge.Condition);
    }

    [Fact]
    public void Instantiate_NoParams_ReturnsOriginal()
    {
        var template = new ComposedWorkflowTemplate
        {
            Name = "static",
            Graph = new WorkflowGraphIR
            {
                Name = "static-graph",
                Nodes = [new GraphNode { Id = "node1", Type = GraphNodeType.Agent, AgentName = "LTAI-Chat" }],
                Edges = [],
            },
        };

        var ir = template.Instantiate(new Dictionary<string, string>());
        Assert.Equal("static-graph", ir.Name);
        var node = Assert.Single(ir.Nodes);
        Assert.Equal("node1", node.Id);
    }

    [Fact]
    public void Instantiate_MultipleParameters_AllReplaced()
    {
        var template = new ComposedWorkflowTemplate
        {
            Graph = new WorkflowGraphIR
            {
                Nodes =
                [
                    new GraphNode { Id = "{{.params.a}}", AgentName = "{{.params.b}}", Tools = ["{{.params.c}}"] },
                ],
            },
        };

        var ir = template.Instantiate(new Dictionary<string, string>
        {
            ["a"] = "id-1", ["b"] = "Agent-X", ["c"] = "tool-y",
        });

        var node = Assert.Single(ir.Nodes);
        Assert.Equal("id-1", node.Id);
        Assert.Equal("Agent-X", node.AgentName);
        Assert.Contains("tool-y", node.Tools);
    }

    [Fact]
    public void ComposedWorkflowRegistry_Roundtrip()
    {
        var registry = new ComposedWorkflowRegistry();
        registry.Register(new ComposedWorkflowTemplate { Name = "my-template", Description = "My custom template" });
        var retrieved = registry.Get("my-template");
        Assert.NotNull(retrieved);
        Assert.Equal("my-template", retrieved.Name);
        Assert.Equal("My custom template", retrieved.Description);
        Assert.Null(registry.Get("nonexistent"));
    }

    [Fact]
    public void ComposedWorkflowRegistry_LoadDefaults()
    {
        var registry = new ComposedWorkflowRegistry();
        registry.LoadDefaults();
        Assert.NotEmpty(registry.Templates);
        Assert.Contains(registry.Templates, t => t.Name == "review-revise");
        Assert.Contains(registry.Templates, t => t.Name == "plan-code-review");
        Assert.Contains(registry.Templates, t => t.Name == "debate-consensus");
        Assert.Contains(registry.Templates, t => t.Name == "explore-exploit");
    }
}

public sealed class GraphIRSerializerTests
{
    private static string NormalizeNewlines(string s) =>
        s.Replace("\r\n", "\n");

    [Fact]
    public void ToMermaid_BasicGraph_GeneratesFlowchart()
    {
        var serializer = new GraphIRSerializer();
        var ir = new WorkflowGraphIR
        {
            Nodes =
            [
                new GraphNode { Id = "start", Type = GraphNodeType.Agent, AgentName = "Chat" },
                new GraphNode { Id = "check", Type = GraphNodeType.Switch, AgentName = "Verify" },
                new GraphNode { Id = "loop", Type = GraphNodeType.Loop, AgentName = "Retry" },
                new GraphNode { Id = "end", Type = GraphNodeType.Agent, AgentName = "Done" },
            ],
            Edges =
            [
                new GraphEdge { From = "start", To = "check", Type = GraphEdgeType.Control },
                new GraphEdge { From = "check", To = "loop", Type = GraphEdgeType.Control, Condition = "!approved" },
                new GraphEdge { From = "loop", To = "check", Type = GraphEdgeType.State },
                new GraphEdge { From = "check", To = "end", Type = GraphEdgeType.Control, Condition = "approved" },
            ],
        };

        var mermaid = NormalizeNewlines(serializer.ToMermaid(ir));
        Assert.StartsWith("flowchart TD", mermaid);
        Assert.Contains("start[Chat]", mermaid);
        Assert.Contains("check", mermaid);
        Assert.Contains("Verify", mermaid);
        Assert.Contains("loop{Retry}", mermaid);
        Assert.Contains("end[Done]", mermaid);
        Assert.Contains("start --> check", mermaid);
    }

    [Fact]
    public void ToMermaid_MessageEdges_UsesDottedStyle()
    {
        var serializer = new GraphIRSerializer();
        var ir = new WorkflowGraphIR
        {
            Nodes =
            [
                new GraphNode { Id = "a", AgentName = "AgentA" },
                new GraphNode { Id = "b", AgentName = "AgentB" },
            ],
            Edges = [new GraphEdge { From = "a", To = "b", Type = GraphEdgeType.Message }],
        };

        var mermaid = NormalizeNewlines(serializer.ToMermaid(ir));
        Assert.Contains("a -.-> b", mermaid);
    }

    [Fact]
    public void ToMermaid_EmptyGraph_ReturnsHeaderOnly()
    {
        var serializer = new GraphIRSerializer();
        var mermaid = NormalizeNewlines(serializer.ToMermaid(new WorkflowGraphIR()));
        Assert.Equal("flowchart TD\n", mermaid);
    }
}
