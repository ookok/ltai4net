using System.Text;
using LTAI.Agent.Formats;
using LTAI.Agent.Vector;

namespace LTAI.Agent.SeedER;

/// <summary>
/// A single step in an exploration path: a node reached via a specific edge.
/// </summary>
public sealed record PathStep(NodeRow Node, EdgeRow? IncomingEdge, int Depth)
{
    public override string ToString()
    {
        if (IncomingEdge == null)
            return $"[{Node.Kind}] {Node.Name}";
        return $"─{IncomingEdge.Relation}→ [{Node.Kind}] {Node.Name}";
    }
}

/// <summary>
/// An exploration path from a seed entity through the knowledge graph.
/// Maintains the full chain of nodes and edges for traceable reasoning.
/// </summary>
public sealed class ExplorationPath
{
    public List<PathStep> Steps { get; } = [];
    public NodeRow Seed => Steps[0].Node;
    public NodeRow Target => Steps[^1].Node;
    public int Length => Steps.Count;

    public double Score { get; set; }

    public ExplorationPath(NodeRow seed)
    {
        Steps.Add(new PathStep(seed, null, 0));
        Score = 1.0;
    }

    public ExplorationPath(ExplorationPath prefix, NodeRow node, EdgeRow edge)
    {
        Steps.AddRange(prefix.Steps);
        Steps.Add(new PathStep(node, edge, prefix.Length));
        Score = prefix.Score * edge.Weight * GetKindBoost(node.Kind);
    }

    public bool ContainsNode(long nodeId) => Steps.Any(s => s.Node.Id == nodeId);

    public string ToPathString()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Steps.Count; i++)
        {
            if (i > 0)
            {
                var edge = Steps[i].IncomingEdge!;
                sb.Append($" ─{edge.Relation}(w={edge.Weight:F1})→ ");
            }
            var node = Steps[i].Node;
            sb.Append($"[{node.Kind}]{node.Name}");
        }
        return sb.ToString();
    }

    public string ToEvidenceString()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Path (score={Score:F3}, length={Length}):");
        for (int i = 0; i < Steps.Count; i++)
        {
            var indent = new string(' ', i * 2);
            var step = Steps[i];
            if (step.IncomingEdge != null)
                sb.AppendLine($"{indent}╙─{step.IncomingEdge.Relation}──→");
            sb.AppendLine($"{indent}● [{step.Node.Kind}] {step.Node.Name}" +
                (string.IsNullOrEmpty(step.Node.Namespace) ? "" : $" ({step.Node.Namespace})"));
        }
        return sb.ToString();
    }

    /// <summary>TOON tabular representation of this path: one row per step.</summary>
    public string ToToonString()
    {
        var tw = new ToonWriter();
        var cols = new[] { "depth", "kind", "name", "ns", "via_rel", "edge_w" };
        var rows = new List<IReadOnlyList<string>>();
        for (int i = 0; i < Steps.Count; i++)
        {
            var step = Steps[i];
            rows.Add(new[] {
                i.ToString(),
                step.Node.Kind,
                step.Node.Name,
                step.Node.Namespace ?? "",
                step.IncomingEdge?.Relation ?? "",
                step.IncomingEdge != null ? step.IncomingEdge.Weight.ToString("F1") : ""
            });
        }
        tw.Table("path", cols, rows);
        tw.KeyValue("score", Score);
        tw.KeyValue("length", Length);
        return tw.ToString();
    }

    private static double GetKindBoost(string kind) => kind switch
    {
        "method" or "function" => 1.4,
        "class" => 1.3,
        "interface" => 1.2,
        _ => 1.0,
    };
}

/// <summary>
/// Result of a SeedER exploration containing ranked paths and a consolidated answer.
/// </summary>
public sealed class SeedERResult
{
    public string Query { get; init; } = "";
    public List<ExplorationPath> Paths { get; init; } = [];
    public List<ExplorationPath> ReasoningPaths { get; init; } = [];
    public int EntitiesFound { get; init; }
    public int PathsExplored { get; init; }
    public string? LlmReasoning { get; set; }
    public string? ConsolidatedAnswer { get; set; }
    public int FsmLevel { get; set; }
    public string? FsmLabel { get; set; }
    public int FsmTotalSteps { get; set; }

    public string ToFullReport(ResultFormat format = ResultFormat.Markdown)
        => format == ResultFormat.Toon ? ToToonReport() : ToMarkdownReport();

    public string ToMarkdownReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# SeedER: {Query}");
        sb.AppendLine($"Entities: {EntitiesFound}, Paths: {PathsExplored}, Ranked: {ReasoningPaths.Count}" +
            (FsmLabel != null ? $", FSM: level={FsmLevel} label={FsmLabel} steps={FsmTotalSteps}" : ""));
        sb.AppendLine();

        if (!string.IsNullOrEmpty(LlmReasoning))
        {
            sb.AppendLine("## LLM Reasoning");
            sb.AppendLine(LlmReasoning);
            sb.AppendLine();
        }

        if (ReasoningPaths.Count > 0)
        {
            sb.AppendLine("## Top Paths");
            for (int i = 0; i < ReasoningPaths.Count; i++)
            {
                sb.AppendLine($"### Path {i + 1} (score={ReasoningPaths[i].Score:F3})");
                sb.AppendLine(ReasoningPaths[i].ToEvidenceString());
            }
        }

        if (!string.IsNullOrEmpty(ConsolidatedAnswer))
        {
            sb.AppendLine("## Answer");
            sb.AppendLine(ConsolidatedAnswer);
        }

        return sb.ToString();
    }

    public string ToToonReport()
    {
        var tw = new ToonWriter();
        tw.Comment($"SeedER: {Query}");
        tw.KeyValue("query", Query);
        tw.KeyValue("entities_found", EntitiesFound);
        tw.KeyValue("paths_explored", PathsExplored);
        tw.KeyValue("paths_ranked", ReasoningPaths.Count);

        if (!string.IsNullOrEmpty(LlmReasoning))
        {
            tw.BeginObject("llm_reasoning");
            tw.KeyValue("analysis", LlmReasoning!);
            tw.EndObject();
        }

        if (ReasoningPaths.Count > 0)
        {
            tw.Blank();
            tw.Comment("Top paths (ranked by score)");
            for (int i = 0; i < ReasoningPaths.Count; i++)
            {
                tw.Blank();
                tw.Comment($"Path {i + 1}");
                tw.KeyValue("path_score", ReasoningPaths[i].Score);
                tw.BeginObject("path_steps");
                var path = ReasoningPaths[i];
                var cols = new[] { "depth", "kind", "name", "ns", "via_rel", "edge_w" };
                var rows = new List<IReadOnlyList<string>>();
                for (int s = 0; s < path.Steps.Count; s++)
                {
                    var step = path.Steps[s];
                    rows.Add(new[] {
                        s.ToString(),
                        step.Node.Kind,
                        step.Node.Name,
                        step.Node.Namespace ?? "",
                        step.IncomingEdge?.Relation ?? "",
                        step.IncomingEdge != null ? step.IncomingEdge.Weight.ToString("F1") : ""
                    });
                }
                tw.Table("steps", cols, rows);
                tw.EndObject();
            }
        }

        if (!string.IsNullOrEmpty(ConsolidatedAnswer))
        {
            tw.Blank();
            tw.BeginObject("answer");
            tw.KeyValue("text", ConsolidatedAnswer!);
            tw.EndObject();
        }

        return tw.ToString();
    }
}
