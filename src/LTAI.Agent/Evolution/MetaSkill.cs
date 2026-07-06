using System.Text.Json;
using System.Text.Json.Serialization;

namespace LTAI.Agent.Evolution;

public sealed record MetaSkillModule(
    IReadOnlyList<string> Principles)
{
    public string ToFormattedString(int indent = 0)
    {
        var prefix = new string(' ', indent);
        return string.Join("\n", Principles.Select(p => $"{prefix}- {p}"));
    }
}

public sealed record MetaSkillPatch(
    string Module,
    string Description,
    double ExpectedImpact);

public sealed record MetaSkill(
    int Version,
    int Round,
    string? EvolvedFrom,
    MetaSkillModule TaskDecomposition,
    MetaSkillModule AgentEngineering,
    MetaSkillModule WorkflowOrchestration,
    DateTime CreatedAt,
    IReadOnlyList<MetaSkillPatch>? PatchesApplied = null,
    double? ValidationScore = null)
{
    [JsonIgnore]
    public string ModuleCountLabel =>
        $"TD:{TaskDecomposition.Principles.Count} AE:{AgentEngineering.Principles.Count} WO:{WorkflowOrchestration.Principles.Count}";

    public static MetaSkill CreateInitial() => new(
        Version: 0,
        Round: 0,
        EvolvedFrom: null,
        TaskDecomposition: new MetaSkillModule([
            "Identify the macro objective and scope of the user's query",
            "Decompose the request into discrete, logically cohesive sub-tasks",
            "Each sub-task should require exactly one tool or skill",
            "Ensure sub-tasks do not overlap in responsibility",
            "Specify evaluable success criteria for each sub-task",
        ]),
        AgentEngineering: new MetaSkillModule([
            "Assign each sub-task to a specialized agent with a distinct role profile",
            "Provide each agent with the specific contextual inputs it requires",
            "Match agent capabilities to the domain of each sub-task",
            "Ensure agents have complementary rather than overlapping expertise",
        ]),
        WorkflowOrchestration: new MetaSkillModule([
            "Select an appropriate topology: sequential for linear pipelines, parallel for independent tasks",
            "Define precise input-output mappings between agents",
            "Use hierarchical routing for complex multi-step workflows",
            "Minimize round-trips between agents to reduce latency and token waste",
        ]),
        CreatedAt: DateTime.UtcNow
    );

    public static MetaSkill EvolvedFromPrevious(MetaSkill prev, MetaSkillPatch[] patches) => new(
        Version: prev.Version + 1,
        Round: prev.Round + 1,
        EvolvedFrom: $"v{prev.Version}",
        TaskDecomposition: prev.TaskDecomposition,
        AgentEngineering: prev.AgentEngineering,
        WorkflowOrchestration: prev.WorkflowOrchestration,
        CreatedAt: DateTime.UtcNow,
        PatchesApplied: patches);

    public string ToMarkdown()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Meta-Skill v{Version} (Round {Round})");
        sb.AppendLine($"- Created: {CreatedAt:yyyy-MM-dd HH:mm:ss} UTC");
        if (EvolvedFrom != null)
            sb.AppendLine($"- Evolved From: {EvolvedFrom}");
        if (ValidationScore.HasValue)
            sb.AppendLine($"- Validation Score: {ValidationScore.Value:P2}");
        sb.AppendLine();

        sb.AppendLine("## Task Decomposition");
        sb.AppendLine(TaskDecomposition.ToFormattedString());
        sb.AppendLine();

        sb.AppendLine("## Agent Engineering");
        sb.AppendLine(AgentEngineering.ToFormattedString());
        sb.AppendLine();

        sb.AppendLine("## Workflow Orchestration");
        sb.AppendLine(WorkflowOrchestration.ToFormattedString());

        if (PatchesApplied is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("## Patches Applied This Round");
            foreach (var p in PatchesApplied)
                sb.AppendLine($"- [{p.Module}] {p.Description} (impact: {p.ExpectedImpact:P1})");
        }

        return sb.ToString();
    }
}
