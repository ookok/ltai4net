using System.Text;
using System.Text.RegularExpressions;
using LTAI.Agent.Skills;

namespace LTAI.Agent.Workflows;

public sealed class SkillGraphMarkdownBridge
{
    private readonly SkillGraph _graph;
    private readonly string _skillsRoot;

    public SkillGraphMarkdownBridge(SkillGraph graph, string? skillsRoot = null)
    {
        _graph = graph;
        _skillsRoot = skillsRoot ?? Path.Combine(AppContext.BaseDirectory, "skills");
    }

    public string SerializeGraphToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"generated: {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}");
        sb.AppendLine($"total_nodes: {_graph.NodeCount}");
        sb.AppendLine($"total_edges: {_graph.EdgeCount}");
        sb.AppendLine("graph_type: skill_graph");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("# Skill Graph Summary");
        sb.AppendLine();
        sb.AppendLine($"- **Nodes**: {_graph.NodeCount}");
        sb.AppendLine($"- **Edges**: {_graph.EdgeCount}");
        sb.AppendLine($"- **Prerequisite edges**: {_graph.GetEdgesByType(SkillEdgeType.Prerequisite).Count}");
        sb.AppendLine($"- **Enhancement edges**: {_graph.GetEdgesByType(SkillEdgeType.Enhancement).Count}");
        sb.AppendLine($"- **Co-occurrence edges**: {_graph.GetEdgesByType(SkillEdgeType.CoOccurrence).Count}");
        sb.AppendLine();
        sb.AppendLine("## Nodes");
        sb.AppendLine();
        sb.AppendLine("| ID | Name | Layer | Uses | Success | Centrality | Tags |");
        sb.AppendLine("|----|------|-------|------|---------|------------|------|");

        var nodes = _graph.GetAllNodes().OrderByDescending(n => n.Centrality);
        foreach (var node in nodes.Take(100))
        {
            sb.AppendLine($"| {node.Id} | {EscapeMd(node.Name)} | L{node.LayerLevel} | {node.UseCount} | {node.SuccessRate:F2} | {node.Centrality:F3} | {string.Join(", ", node.Tags.Take(5))} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Edges");
        sb.AppendLine();
        sb.AppendLine("| Source | Target | Type | Weight | Evidence | Reliability |");
        sb.AppendLine("|--------|--------|------|--------|----------|-------------|");

        var edges = _graph.GetAllEdges().OrderByDescending(e => e.Weight);
        foreach (var edge in edges.Take(100))
        {
            var srcNode = _graph.GetNode(edge.SourceId);
            var tgtNode = _graph.GetNode(edge.TargetId);
            sb.AppendLine($"| {EscapeMd(srcNode?.Name ?? edge.SourceId)} | {EscapeMd(tgtNode?.Name ?? edge.TargetId)} | {edge.Type} | {edge.Weight:F3} | {edge.EvidenceCount} | {edge.Reliability:F2} |");
        }

        return sb.ToString();
    }

    public async Task WriteNodeMarkdownAsync(SkillNode node, CancellationToken ct = default)
    {
        var layerDir = node.LayerLevel switch
        {
            0 => "l0_atomic",
            1 => "l1_task",
            2 => "l2_workflow",
            3 => "l3_domain",
            4 => "l4_meta",
            _ => "l1_task"
        };

        var dir = Path.Combine(_skillsRoot, layerDir);
        Directory.CreateDirectory(dir);

        var fileName = $"{FileNameSafeSkillName(node.Name)}.md";
        var filePath = Path.Combine(dir, fileName);

        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"skill_name: {node.Name}");
        sb.AppendLine($"skill_id: {node.Id}");
        sb.AppendLine($"layer: L{node.LayerLevel}");
        sb.AppendLine($"tags: [{string.Join(", ", node.Tags)}]");
        sb.AppendLine($"centrality: {node.Centrality:F3}");
        sb.AppendLine($"use_count: {node.UseCount}");
        sb.AppendLine($"success_rate: {node.SuccessRate:F3}");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# {node.Name}");
        sb.AppendLine();
        sb.AppendLine(node.Description);
        sb.AppendLine();

        var outgoing = _graph.GetOutgoingEdges(node.Id);
        if (outgoing.Count > 0)
        {
            sb.AppendLine("## Outgoing Edges");
            sb.AppendLine();
            foreach (var edge in outgoing)
            {
                var target = _graph.GetNode(edge.TargetId);
                sb.AppendLine($"- **{edge.Type}** → {target?.Name ?? edge.TargetId} (weight: {edge.Weight:F2}, reliability: {edge.Reliability:F2})");
            }
            sb.AppendLine();
        }

        var incoming = _graph.GetIncomingEdges(node.Id);
        if (incoming.Count > 0)
        {
            sb.AppendLine("## Incoming Edges");
            sb.AppendLine();
            foreach (var edge in incoming)
            {
                var source = _graph.GetNode(edge.SourceId);
                sb.AppendLine($"- **{edge.Type}** ← {source?.Name ?? edge.SourceId} (weight: {edge.Weight:F2})");
            }
            sb.AppendLine();
        }

        await File.WriteAllTextAsync(filePath, sb.ToString(), ct).ConfigureAwait(false);
        node.MarkdownPath = filePath;
    }

    public async Task SaveGraphDigestAsync(string outputPath, CancellationToken ct = default)
    {
        var md = SerializeGraphToMarkdown();
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(outputPath, md, ct).ConfigureAwait(false);
    }

    public async Task WriteAllNodesAsync(CancellationToken ct = default)
    {
        foreach (var node in _graph.GetAllNodes())
        {
            await WriteNodeMarkdownAsync(node, ct).ConfigureAwait(false);
        }
    }

    private static string EscapeMd(string text) =>
        text.Replace("|", "\\|");

    public static string FileNameSafeSkillName(string name) =>
        Regex.Replace(name.ToLower(), @"[^a-z0-9]+", "_").Trim('_');
}
