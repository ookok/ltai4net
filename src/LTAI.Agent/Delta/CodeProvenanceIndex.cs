// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  CodeProvenanceIndex — bidirectional conversation↔code linking
//
//  Two-directional index:
//    Code → Conversation: given (file, line), find the conversation
//      and message that produced it.
//    Conversation → Code: given (conversation, message), find all
//      file edits it produced with line ranges.
//
//  This is the core of "source code is now source conversation"
//  (DeltaDB philosophy).
// ═══════════════════════════════════════════════════════════════

using System.Text;
using LTAI.Agent.Vector;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Delta;

public sealed class CodeProvenanceIndex
{
    private readonly DeltaStore _deltaStore;
    private readonly ILogger<CodeProvenanceIndex> _logger;

    public CodeProvenanceIndex(DeltaStore deltaStore, ILogger<CodeProvenanceIndex>? logger = null)
    {
        _deltaStore = deltaStore;
        _logger = logger ?? NullLogger<CodeProvenanceIndex>.Instance;
    }

    /// <summary>Find which conversation produced a given line of code.</summary>
    public async Task<List<CodeProvenanceResult>> FindProvenanceAsync(string filePath, int lineNumber)
    {
        return await FindProvenanceRangeAsync(filePath, lineNumber, lineNumber);
    }

    /// <summary>Find which conversation produced a range of code lines.</summary>
    public async Task<List<CodeProvenanceResult>> FindProvenanceRangeAsync(string filePath, int startLine, int endLine)
    {
        return _deltaStore.GetProvenanceForLines(filePath, startLine, endLine);
    }

    /// <summary>Find all code edits made by a specific conversation message.</summary>
    public async Task<string> GetConversationCodeMapAsync(string conversationId, string? messageId = null)
    {
        var links = _deltaStore.GetConversationCodeLinks(conversationId, messageId);
        if (links.Count == 0) return "";

        var sb = new StringBuilder();
        sb.AppendLine("## Code Produced by This Conversation");

        foreach (var group in links.GroupBy(l => l.FilePath))
        {
            sb.AppendLine($"\n### {group.Key}");
            foreach (var link in group.OrderBy(l => l.StartLine))
            {
                var delta = _deltaStore.GetDelta(link.DeltaId);
                var tool = delta?.ToolName ?? "unknown";
                sb.AppendLine($"  L{link.StartLine}-L{link.EndLine} [{tool}] `delta:{link.DeltaId[..12]}`");
            }
        }

        return sb.ToString();
    }

    /// <summary>Build a compact provenance summary for context injection.</summary>
    public async Task<string> BuildProvenanceSummaryAsync(string filePath, int contextLines = 5)
    {
        if (!File.Exists(filePath)) return "";

        var lines = await File.ReadAllLinesAsync(filePath).ConfigureAwait(false);
        var sb = new StringBuilder();
        sb.AppendLine($"## Provenance: `{filePath}`");
        sb.AppendLine();

        var batchSize = Math.Max(1, contextLines);
        for (int i = 0; i < lines.Length; i += batchSize)
        {
            var end = Math.Min(i + batchSize - 1, lines.Length - 1);
            var provenance = _deltaStore.GetProvenanceForLines(filePath, i + 1, end + 1);

            if (provenance.Count > 0)
            {
                var unique = provenance
                    .GroupBy(p => p.ConversationId)
                    .Select(g => g.First())
                    .Take(2);
                foreach (var p in unique)
                {
                    var summary = lines[i].Length > 60 ? lines[i][..60] + "..." : lines[i];
                    sb.AppendLine($"  L{i + 1} ← conv:{p.ConversationId[..8]} msg:{p.MessageId[..8]} `delta:{p.DeltaId[..12]}` ({p.ToolName})");
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>Find all conversations that touched a given symbol/function.</summary>
    public async Task<List<(string ConversationId, string MessageId, string ToolName, long Timestamp)>> FindProvenanceForSymbolAsync(
        string symbolName, CgGraph? cgGraph = null)
    {
        if (cgGraph == null) return [];

        var result = await cgGraph.QueryAsync(symbolName, topK: 20).ConfigureAwait(false);
        var provenances = new List<(string ConversationId, string MessageId, string ToolName, long Timestamp)>();

        // Parse compact graph result for file references
        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var parts = line.Split('|');
            if (parts.Length < 2) continue;
            var locationPart = parts[0].Trim();
            var match = System.Text.RegularExpressions.Regex.Match(locationPart,
                @"(.+?):(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success) continue;

            var filePath = match.Groups[1].Value;
            var lineNumber = int.Parse(match.Groups[2].Value);

            var provs = _deltaStore.GetProvenanceForLines(filePath, lineNumber, lineNumber);
            foreach (var p in provs)
            {
                provenances.Add((p.ConversationId, p.MessageId, p.ToolName, p.Timestamp));
            }
        }

        return provenances.OrderByDescending(p => p.Timestamp).Take(20).ToList();
    }

    /// <summary>Export full provenance for a file as Markdown.</summary>
    public async Task<string> ExportProvenanceMarkdownAsync(string filePath)
    {
        var chainInfo = _deltaStore.GetChainInfo(filePath);
        if (chainInfo == null) return $"# Provenance: `{filePath}`\n\n*No delta history found.*\n";

        var sb = new StringBuilder();
        sb.AppendLine($"# Provenance: `{filePath}`");
        sb.AppendLine();
        sb.AppendLine($"- **Total deltas**: {chainInfo.TotalDeltas}");
        sb.AppendLine($"- **Head delta**: `{chainInfo.HeadDeltaId?[..12] ?? "none"}`");
        sb.AppendLine($"- **First edit**: {DateTimeOffset.FromUnixTimeMilliseconds(chainInfo.FirstEditAt):yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- **Last edit**: {DateTimeOffset.FromUnixTimeMilliseconds(chainInfo.LastEditAt):yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        var deltas = _deltaStore.GetFileDeltas(filePath);
        sb.AppendLine("## Edit History");
        sb.AppendLine();
        sb.AppendLine("| # | Delta | Tool | Lines | Agent | Time |");
        sb.AppendLine("|---|-------|------|-------|-------|------|");

        for (int i = 0; i < deltas.Count; i++)
        {
            var d = deltas[i];
            var time = DateTimeOffset.FromUnixTimeMilliseconds(d.Timestamp).ToString("HH:mm:ss");
            var agent = d.AgentId?[..Math.Min(8, d.AgentId.Length)] ?? "?";
            sb.AppendLine($"| {i + 1} | `{d.Id[..12]}` | {d.ToolName} | L{d.StartLine}-L{d.EndLine} | {agent} | {time} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Line Provenance");
        sb.AppendLine();

        var lines = File.Exists(filePath) ? await File.ReadAllLinesAsync(filePath).ConfigureAwait(false) : [];
        for (int i = 0; i < lines.Length; i++)
        {
            var prov = _deltaStore.GetProvenanceForLines(filePath, i + 1, i + 1);
            if (prov.Count > 0)
            {
                var p = prov[0];
                var preview = lines[i].Length > 80 ? lines[i][..80] + "..." : lines[i];
                sb.AppendLine($"  L{i + 1,-6} | {preview,-80} | ← conv:{p.ConversationId[..8]} `delta:{p.DeltaId[..12]}`");
            }
        }

        return sb.ToString();
    }
}
