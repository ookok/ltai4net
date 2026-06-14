using System.Text.Json;

namespace LTAI.Agent.Workflows.GraphIR;

public sealed class GraphIRSerializer
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public string ToJson(WorkflowGraphIR ir) =>
        JsonSerializer.Serialize(ir, JsonOpts);

    public WorkflowGraphIR FromJson(string json)
    {
        var ir = JsonSerializer.Deserialize<WorkflowGraphIR>(json, JsonOpts);
        if (ir == null)
        {
            System.Diagnostics.Debug.WriteLine("GraphIRSerializer: deserialization returned null, returning empty IR");
            return new WorkflowGraphIR();
        }
        return ir;
    }

    public string ToMermaid(WorkflowGraphIR ir)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("flowchart TD");

        foreach (var n in ir.Nodes)
        {
            var label = string.IsNullOrEmpty(n.AgentName) ? n.Id : n.AgentName;
            switch (n.Type)
            {
                case GraphNodeType.Switch:
                    sb.AppendLine("    " + n.Id + "{{" + label + "}}");
                    break;
                case GraphNodeType.Loop:
                    sb.AppendLine("    " + n.Id + "{" + label + "}");
                    break;
                default:
                    sb.AppendLine("    " + n.Id + "[" + label + "]");
                    break;
            }
        }

        foreach (var edge in ir.Edges)
        {
            var style = edge.Type switch
            {
                GraphEdgeType.Message => "-.->",
                GraphEdgeType.State => "= .=>",
                _ => "-->",
            };
            var cond = !string.IsNullOrEmpty(edge.Condition) ? " |" + edge.Condition + "|" : "";
            sb.AppendLine("    " + edge.From + " " + style + cond + " " + edge.To);
        }

        return sb.ToString();
    }
}
