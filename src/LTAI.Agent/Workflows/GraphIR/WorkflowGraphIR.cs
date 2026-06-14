using System.Text.Json.Serialization;

namespace LTAI.Agent.Workflows.GraphIR;

public enum GraphNodeType
{
    Agent,
    Loop,
    Switch,
    Interaction,
    SubGraph,
}

public enum GraphEdgeType
{
    Control,
    Message,
    State,
}

public sealed class GraphNodeConfig
{
    public int? MaxIterations { get; set; }
    public string? Condition { get; set; }
    public string? Timeout { get; set; }
    public Dictionary<string, string> Params { get; set; } = [];
}

public sealed class GraphNode
{
    public string Id { get; set; } = "";
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public GraphNodeType Type { get; set; } = GraphNodeType.Agent;
    public string AgentName { get; set; } = "";
    public string PromptTemplate { get; set; } = "";
    public List<string> Tools { get; set; } = [];
    public GraphNodeConfig? Config { get; set; }
}

public sealed class GraphEdge
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public GraphEdgeType Type { get; set; } = GraphEdgeType.Control;
    public string? Condition { get; set; }
}

public sealed class GraphMetadata
{
    public string? Description { get; set; }
    public string? Author { get; set; }
    public string? Version { get; set; } = "1.0";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<string> Tags { get; set; } = [];
}

public sealed class WorkflowGraphIR
{
    public string Name { get; set; } = "";
    public List<GraphNode> Nodes { get; set; } = [];
    public List<GraphEdge> Edges { get; set; } = [];
    public GraphMetadata Metadata { get; set; } = new();
}
