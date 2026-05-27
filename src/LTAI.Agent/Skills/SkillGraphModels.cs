using System.Text.Json.Serialization;

namespace LTAI.Agent.Skills;

public enum SkillEdgeType
{
    Prerequisite,
    Enhancement,
    CoOccurrence
}

public sealed record SkillNode
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public int LayerLevel { get; set; }
    public string Description { get; set; } = "";
    public int UseCount { get; set; }
    public double SuccessRate { get; set; }
    public double Centrality { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;
    public List<string> Tags { get; set; } = new();
    public string MarkdownPath { get; set; } = "";
}

public sealed record SkillEdge
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..10];
    public string SourceId { get; init; } = "";
    public string TargetId { get; init; } = "";
    public SkillEdgeType Type { get; init; }
    public double Weight { get; set; }
    public int EvidenceCount { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public int SuccessfulUses { get; set; }
    public int FailedUses { get; set; }

    public double Reliability => EvidenceCount > 0
        ? (double)SuccessfulUses / EvidenceCount
        : 0;
}

public sealed record SkillSubgraph
{
    public List<SkillNode> Nodes { get; init; } = new();
    public List<SkillEdge> Edges { get; init; } = new();
    public string EntryPointId { get; init; } = "";
    public int TotalSteps { get; init; }
    public double ConfidenceScore { get; init; }
    public List<string> ExecutionOrder { get; init; } = new();
}

[JsonSerializable(typeof(SkillNode))]
[JsonSerializable(typeof(SkillEdge))]
[JsonSerializable(typeof(SkillSubgraph))]
[JsonSerializable(typeof(List<SkillNode>))]
[JsonSerializable(typeof(List<SkillEdge>))]
public sealed partial class SkillGraphJsonContext : JsonSerializerContext
{
}
