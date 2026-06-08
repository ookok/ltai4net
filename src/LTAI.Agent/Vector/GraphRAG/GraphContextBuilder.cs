// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  GraphContextBuilder — subgraph → LLM context
//
//  Phase 4c: converts a SubgraphResult into structured prompt
//  context that can be injected into the agent's system prompt.
//
//  RRF fusion: GraphRAG score × 0.4 + vector search score × 0.6
//  when both paths are available.
//
//  Output formats:
//    - Markdown (default, human-readable)
//    - TOON (compact, ~50% token reduction)
// ═══════════════════════════════════════════════════════════════

using System.Text;
using LTAI.Agent.Formats;

namespace LTAI.Agent.Vector.GraphRAG;

/// <summary>
/// Converts extracted knowledge graph subgraphs into LLM-ready context.
/// Supports RRF fusion between GraphRAG and vector search scores.
/// </summary>
public sealed class GraphContextBuilder
{
    /// <summary>
    /// RRF fusion weight for GraphRAG path (vs vector search).
    /// Default 0.4 (plan says: GraphRAG score × 0.4 + vector × 0.6).
    /// </summary>
    public double GraphRagWeight { get; set; } = 0.4;

    /// <summary>
    /// RRF fusion weight for vector search path.
    /// Default 0.6.
    /// </summary>
    public double VectorWeight { get; set; } = 0.6;

    private readonly int _maxTokens;

    public GraphContextBuilder(int maxTokens = 2048)
    {
        _maxTokens = maxTokens;
    }

    /// <summary>
    /// Build context from a subgraph result in the specified format.
    /// </summary>
    /// <param name="result">Subgraph from SubgraphExtractor.</param>
    /// <param name="format">Output format (Markdown or Toon).</param>
    /// <returns>Structured context string ready for LLM injection.</returns>
    public string BuildContext(
        SubgraphExtractor.SubgraphResult result,
        ResultFormat format = ResultFormat.Markdown)
    {
        return format switch
        {
            ResultFormat.Toon => BuildToonContext(result),
            _ => BuildMarkdownContext(result),
        };
    }

    /// <summary>
    /// Build context from a subgraph result with RRF-fused vector search results.
    /// </summary>
    /// <param name="result">Subgraph from SubgraphExtractor.</param>
    /// <param name="vectorNodes">Vector search results: (nodeId, distance).</param>
    /// <param name="format">Output format.</param>
    /// <returns>Fused context string.</returns>
    public string BuildFusedContext(
        SubgraphExtractor.SubgraphResult result,
        IReadOnlyList<(long nodeId, float distance)> vectorNodes,
        ResultFormat format = ResultFormat.Markdown)
    {
        // RRF fusion: combine graph and vector scores
        var fusedScores = new Dictionary<long, double>();

        // GraphRAG scores
        int rank = 0;
        foreach (var node in result.Nodes.OrderByDescending(n => n.Score))
            fusedScores[node.NodeId] = GraphRagWeight * (1.0 / (60 + rank++));

        // Vector search scores
        rank = 0;
        foreach (var (nodeId, distance) in vectorNodes)
        {
            // Convert cosine distance [0,2] to similarity [0,1]
            float similarity = 1.0f - Math.Clamp(distance / 2.0f, 0, 1);
            fusedScores[nodeId] = fusedScores.GetValueOrDefault(nodeId)
                + VectorWeight * similarity;
        }

        var sb = new StringBuilder();
        sb.AppendLine("## Knowledge Graph Context (Fused)");
        sb.AppendLine();

        // Sort nodes by fused score
        var sorted = result.Nodes
            .Select(n => (Node: n, FusedScore: fusedScores.GetValueOrDefault(n.NodeId, 0)))
            .OrderByDescending(x => x.FusedScore)
            .ToList();

        if (sorted.Count > 0)
        {
            sb.AppendLine($"### Key Entities ({sorted.Count})");
            foreach (var (node, score) in sorted.Take(20))
            {
                var icon = node.Kind switch
                {
                    "class" => "🔷", "method" or "function" => "⚙️",
                    "interface" => "🔲", "enum" => "🔢", "struct" => "🏗️",
                    "document" => "📄", "concept" => "🏷️", "fact" => "💡",
                    "file" => "📁", _ => "▪️"
                };
                sb.AppendLine($"- {icon} **[{node.Kind}] {node.Name}** (score: {score:F3})"
                    + (string.IsNullOrEmpty(node.Namespace) ? "" : $" ({node.Namespace})"));
            }
            sb.AppendLine();
        }

        // Communities
        if (result.Communities.Count > 1)
        {
            sb.AppendLine($"### Communities ({result.Communities.Count})");
            foreach (var community in result.Communities.Take(5))
            {
                sb.AppendLine($"- **{community.Label}** (avg weight: {community.AverageWeight:F2})");
                foreach (var member in community.Members.Take(5))
                {
                    sb.AppendLine($"  - [{member.Kind}] {member.Name}");
                }
            }
            sb.AppendLine();
        }

        // Relationships
        if (result.Edges.Count > 0)
        {
            sb.AppendLine($"### Relationships ({result.Edges.Count})");
            foreach (var edge in result.Edges.Take(15))
            {
                var srcName = result.Nodes.FirstOrDefault(n => n.NodeId == edge.SrcId)?.Name ?? $"#{edge.SrcId}";
                var dstName = result.Nodes.FirstOrDefault(n => n.NodeId == edge.DstId)?.Name ?? $"#{edge.DstId}";
                sb.AppendLine($"- **{srcName}** ══ *{edge.Relation}* ══ **{dstName}** (w={edge.Weight:F1})");
            }
        }

        var result_text = sb.ToString();

        // Truncate to max tokens (rough approximation: 1 token ≈ 2 chars for Chinese)
        if (result_text.Length > _maxTokens * 2)
            result_text = result_text[..(_maxTokens * 2)] + "\n...(truncated)";

        return result_text;
    }

    private string BuildMarkdownContext(SubgraphExtractor.SubgraphResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Knowledge Graph Context");
        sb.AppendLine();

        // Entities
        if (result.Nodes.Count > 0)
        {
            sb.AppendLine($"### Entities ({result.Nodes.Count})");
            foreach (var node in result.Nodes.OrderBy(n => n.Depth).ThenByDescending(n => n.Score))
            {
                var icon = node.Kind switch
                {
                    "class" => "🔷", "method" or "function" => "⚙️",
                    "interface" => "🔲", "enum" => "🔢", "struct" => "🏗️",
                    "document" => "📄", "concept" => "🏷️", "fact" => "💡",
                    "file" => "📁", _ => "▪️"
                };
                sb.AppendLine($"- {icon} **[{node.Kind}] {node.Name}**"
                    + (string.IsNullOrEmpty(node.Namespace) ? "" : $" ({node.Namespace})"));
            }
            sb.AppendLine();
        }

        // Communities
        if (result.Communities.Count > 0)
        {
            sb.AppendLine($"### Communities ({result.Communities.Count})");
            foreach (var community in result.Communities.OrderByDescending(c => c.Members.Count).Take(5))
            {
                sb.AppendLine($"- **{community.Label}** ({community.Members.Count} members, avg weight: {community.AverageWeight:F2})");
            }
            sb.AppendLine();
        }

        // Relationships
        if (result.Edges.Count > 0)
        {
            sb.AppendLine($"### Relationships ({result.Edges.Count})");
            foreach (var edge in result.Edges.Take(15))
            {
                var srcName = result.Nodes.FirstOrDefault(n => n.NodeId == edge.SrcId)?.Name ?? $"#{edge.SrcId}";
                var dstName = result.Nodes.FirstOrDefault(n => n.NodeId == edge.DstId)?.Name ?? $"#{edge.DstId}";
                sb.AppendLine($"- **{srcName}** ══ *{edge.Relation}* ══ **{dstName}** (w={edge.Weight:F1})");
            }
        }

        return sb.ToString();
    }

    private string BuildToonContext(SubgraphExtractor.SubgraphResult result)
    {
        var tw = new ToonWriter();
        tw.Comment($"graphrag context: {result.Nodes.Count} entities, {result.Edges.Count} edges, {result.Communities.Count} communities");

        // Entities table
        if (result.Nodes.Count > 0)
        {
            var cols = new[] { "kind", "name", "ns", "depth", "score" };
            var rows = result.Nodes
                .OrderBy(n => n.Depth)
                .Select(n => (IReadOnlyList<string>)new[] {
                    n.Kind, n.Name,
                    n.Namespace ?? "",
                    n.Depth.ToString(),
                    n.Score.ToString("F2")
                }).ToList();
            tw.Table("entities", cols, rows);
        }

        // Communities
        if (result.Communities.Count > 0)
        {
            tw.BeginObject("communities");
            foreach (var c in result.Communities.OrderByDescending(c => c.Members.Count).Take(5))
            {
                tw.Comment($"{c.Label}: {c.Members.Count} members, avg weight {c.AverageWeight:F2}");
            }
            tw.EndObject();
        }

        // Relationships
        if (result.Edges.Count > 0)
        {
            var cols = new[] { "src", "rel", "dst", "w" };
            var rows = result.Edges.Take(15)
                .Select(e => {
                    var srcName = result.Nodes.FirstOrDefault(n => n.NodeId == e.SrcId)?.Name ?? $"#{e.SrcId}";
                    var dstName = result.Nodes.FirstOrDefault(n => n.NodeId == e.DstId)?.Name ?? $"#{e.DstId}";
                    return (IReadOnlyList<string>)new[] { srcName, e.Relation, dstName, e.Weight.ToString("F1") };
                }).ToList();
            tw.Table("rels", cols, rows);
        }

        return tw.ToString();
    }
}
