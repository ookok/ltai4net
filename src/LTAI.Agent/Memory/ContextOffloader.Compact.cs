// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  ContextOffloader — Window compaction by token/role/importance
// ═══════════════════════════════════════════════════════════════

using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Memory;

partial class ContextOffloader
{
    public static int ComputeAdaptiveKeepN(
        List<ChatMessage> messages,
        int maxContextTokens = 128000,
        double reservedSystemRatio = 0.2,
        int minKeep = 5,
        int maxKeep = 30)
    {
        var systemMsgs = messages.Where(m => m.Role == ChatRole.System).ToList();
        var systemTokens = systemMsgs.Sum(m => EstimateTokens(m.Text ?? ""));
        var reservedSystem = (int)(maxContextTokens * reservedSystemRatio);
        var budgetForNonSystem = maxContextTokens - reservedSystem - systemTokens;
        if (budgetForNonSystem <= 0) return minKeep;

        var nonSystem = messages.Where(m => m.Role != ChatRole.System).ToList();
        if (nonSystem.Count == 0) return minKeep;

        var avgSize = nonSystem.Average(m => EstimateTokens(m.Text ?? ""));
        var idealKeep = (int)(budgetForNonSystem / Math.Max(avgSize, 10));
        return Math.Clamp(idealKeep, minKeep, maxKeep);
    }

    public async Task ForceWindowCompactByTokensAsync(
        List<ChatMessage> messages,
        string traceId,
        int maxContextTokens = 128000,
        double reservedSystemRatio = 0.2,
        MermaidStateTracker? mermaid = null)
    {
        var systemMsgs = messages.Where(m => m.Role == ChatRole.System).ToList();
        var nonSystemMsgs = messages.Where(m => m.Role != ChatRole.System).ToList();
        if (nonSystemMsgs.Count == 0) return;

        var systemTokens = systemMsgs.Sum(m => EstimateTokens(m.Text ?? ""));
        var reservedSystem = (int)(maxContextTokens * reservedSystemRatio);
        var budgetForNonSystem = maxContextTokens - reservedSystem - systemTokens;
        if (budgetForNonSystem <= 0) return;

        var diagram = "";
        var offlineRefs = new List<string>();
        if (mermaid != null)
        {
            var result = await mermaid.BuildFromMessageFlowAsync(nonSystemMsgs, traceId, this).ConfigureAwait(false);
            diagram = result.Diagram;
            offlineRefs = result.Refs;
        }

        var keepMsgs = new List<ChatMessage>();
        var totalTokens = 0.0;
        for (int i = nonSystemMsgs.Count - 1; i >= 0; i--)
        {
            var msgTokens = EstimateTokens(nonSystemMsgs[i].Text ?? "");
            if (totalTokens + msgTokens > budgetForNonSystem) break;
            keepMsgs.Insert(0, nonSystemMsgs[i]);
            totalTokens += msgTokens;
        }

        var keepCount = keepMsgs.Count;
        var compacted = nonSystemMsgs.Count - keepCount;
        if (compacted <= 0) return;

        var windowSummary = BuildWindowSummary(nonSystemMsgs, keepCount, traceId, diagram);

        messages.Clear();
        messages.AddRange(systemMsgs);
        messages.Add(new ChatMessage(ChatRole.System, windowSummary));
        messages.AddRange(keepMsgs);

        _logger.LogInformation(
            "ContextOffloader: ForceWindowCompactByTokens — kept {Keep}/{Total} msgs ({Tokens:F0}/{Budget} tok), {Refs} refs",
            keepCount, nonSystemMsgs.Count, totalTokens, budgetForNonSystem, offlineRefs.Count);
    }

    public async Task CompactByRoleAsync(
        List<ChatMessage> messages,
        string traceId,
        int keepUserLastN = 5,
        int toolSummaryMaxChars = 500,
        MermaidStateTracker? mermaid = null)
    {
        var systemMsgs = messages.Where(m => m.Role == ChatRole.System).ToList();
        var otherMsgs = messages.Where(m => m.Role != ChatRole.System).ToList();

        var diagram = "";
        var offlineRefs = new List<string>();
        if (mermaid != null && otherMsgs.Count > keepUserLastN)
        {
            var result = await mermaid.BuildFromMessageFlowAsync(otherMsgs, traceId, this).ConfigureAwait(false);
            diagram = result.Diagram;
            offlineRefs = result.Refs;
        }

        var userCount = 0;
        var compacted = 0;
        var processed = new List<ChatMessage>();

        foreach (var msg in otherMsgs)
        {
            var role = msg.Role.ToString().ToLowerInvariant();
            var text = msg.Text ?? "";

            if (role == "system")
            {
                processed.Add(msg);
            }
            else if (role == "user")
            {
                userCount++;
                var isRecent = userCount > (otherMsgs.Count(m => m.Role.ToString().ToLowerInvariant() == "user") - keepUserLastN);
                if (isRecent || !ShouldOffload(text))
                {
                    processed.Add(msg);
                }
                else
                {
                    var headTail = text.Length > 300
                        ? text[..150] + $"\n… [{text.Length - 300} chars trimmed] …\n" + text[^150..]
                        : text;
                    processed.Add(new ChatMessage(msg.Role, headTail)
                    {
                        AuthorName = msg.AuthorName,
                        RawRepresentation = msg.RawRepresentation,
                        AdditionalProperties = msg.AdditionalProperties,
                    });
                    compacted++;
                }
            }
            else if (role == "assistant")
            {
                if (ShouldOffload(text) && text.Length > toolSummaryMaxChars * 2)
                {
                    var head = text[..Math.Min(toolSummaryMaxChars, text.Length)];
                    processed.Add(new ChatMessage(msg.Role,
                        $"{head}\n… [{text.Length - toolSummaryMaxChars} chars → refs] …")
                    {
                        AuthorName = msg.AuthorName,
                        RawRepresentation = msg.RawRepresentation,
                        AdditionalProperties = msg.AdditionalProperties,
                    });
                    compacted++;
                }
                else
                {
                    processed.Add(msg);
                }
            }
            else if (role == "tool")
            {
                if (ShouldOffload(text))
                {
                    var summary = text.Length > toolSummaryMaxChars
                        ? text[..toolSummaryMaxChars] + $"\n… (tool result truncated, {text.Length} chars)"
                        : text;
                    processed.Add(new ChatMessage(msg.Role, summary)
                    {
                        AuthorName = msg.AuthorName,
                        RawRepresentation = msg.RawRepresentation,
                        AdditionalProperties = msg.AdditionalProperties,
                    });
                    compacted++;
                }
                else
                {
                    processed.Add(msg);
                }
            }
            else
            {
                processed.Add(msg);
            }
        }

        if (compacted == 0) return;

        var sb = new StringBuilder();
        sb.AppendLine($"## Role-Based Compression — {compacted} messages compacted");
        sb.AppendLine();
        sb.AppendLine($"- **Total messages**: {otherMsgs.Count}");
        sb.AppendLine($"- **Compacted**: {compacted}");
        sb.AppendLine($"- **TraceId**: `{traceId}`");
        if (!string.IsNullOrEmpty(diagram))
        {
            sb.AppendLine();
            sb.AppendLine(diagram);
        }

        messages.Clear();
        messages.AddRange(systemMsgs);
        messages.Add(new ChatMessage(ChatRole.System, sb.ToString()));
        messages.AddRange(processed);

        _logger.LogInformation(
            "ContextOffloader: CompactByRole — {Compacted}/{Total} msgs compacted, {Refs} refs",
            compacted, otherMsgs.Count, offlineRefs.Count);
    }

    public async Task ForceWindowCompactByImportanceAsync(
        List<ChatMessage> messages,
        string traceId,
        double importanceThreshold = 0.7,
        int minKeep = 5,
        MermaidStateTracker? mermaid = null)
    {
        var systemMsgs = messages.Where(m => m.Role == ChatRole.System).ToList();
        var nonSystemMsgs = messages.Where(m => m.Role != ChatRole.System).ToList();
        if (nonSystemMsgs.Count <= minKeep) return;

        var diagram = "";
        if (mermaid != null)
        {
            var result = await mermaid.BuildFromMessageFlowAsync(nonSystemMsgs, traceId, this).ConfigureAwait(false);
            diagram = result.Diagram;
        }

        var scored = nonSystemMsgs.Select((msg, idx) =>
        {
            var text = msg.Text ?? "";
            var roleScore = msg.Role.ToString().ToLowerInvariant() switch
            {
                "assistant" => 0.9,
                "user" => 0.7,
                "tool" => 0.5,
                _ => 0.3,
            };
            var recencyScore = (double)(idx + 1) / nonSystemMsgs.Count;
            var lengthScore = Math.Min(1.0, text.Length / 5000.0);
            var composite = (roleScore * 0.4 + recencyScore * 0.35 + lengthScore * 0.25);
            return (Msg: msg, Score: composite, Index: idx);
        }).OrderByDescending(x => x.Score).ToList();

        var keepSet = new HashSet<int>(scored.Take(Math.Max(minKeep, nonSystemMsgs.Count / 2)).Select(x => x.Index));
        for (int i = nonSystemMsgs.Count - minKeep; i < nonSystemMsgs.Count; i++)
            keepSet.Add(i);

        var keepMsgs = nonSystemMsgs.Where((_, i) => keepSet.Contains(i)).ToList();
        var compacted = nonSystemMsgs.Count - keepMsgs.Count;
        if (compacted <= 0) return;

        var windowSummary = BuildWindowSummary(nonSystemMsgs, keepMsgs.Count, traceId, diagram);

        messages.Clear();
        messages.AddRange(systemMsgs);
        messages.Add(new ChatMessage(ChatRole.System, windowSummary));
        messages.AddRange(keepMsgs);

        _logger.LogInformation(
            "ContextOffloader: ForceWindowCompactByImportance — kept {Keep}/{Total} by importance score, {Compacted} compacted",
            keepMsgs.Count, nonSystemMsgs.Count, compacted);
    }

    public async Task ForceWindowCompactAsync(
        List<ChatMessage> messages,
        string traceId,
        int keepLastN,
        MermaidStateTracker? mermaid = null)
    {
        if (messages.Count <= keepLastN) return;

        var systemMsgs = messages.Where(m => m.Role == ChatRole.System).ToList();
        var nonSystemMsgs = messages.Where(m => m.Role != ChatRole.System).ToList();

        var diagram = "";
        var offlineRefs = new List<string>();
        if (mermaid != null)
        {
            var result = await mermaid.BuildFromMessageFlowAsync(nonSystemMsgs, traceId, this).ConfigureAwait(false);
            diagram = result.Diagram;
            offlineRefs = result.Refs;
        }

        int keepCount = Math.Min(keepLastN, nonSystemMsgs.Count);
        var keepMsgs = nonSystemMsgs.Skip(nonSystemMsgs.Count - keepCount).ToList();

        var windowSummary = BuildWindowSummary(nonSystemMsgs, keepCount, traceId, diagram);

        messages.Clear();
        messages.AddRange(systemMsgs);
        messages.Add(new ChatMessage(ChatRole.System, windowSummary));
        messages.AddRange(keepMsgs);

        _logger.LogInformation(
            "ContextOffloader: ForceWindowCompact — {Total} msgs → {Sys}+1(summary)+{Keep} kept | {Refs} refs offloaded",
            systemMsgs.Count + nonSystemMsgs.Count,
            systemMsgs.Count, keepCount, offlineRefs.Count);
    }

    private static string BuildWindowSummary(
        List<ChatMessage> allNonSystem,
        int keepCount,
        string traceId,
        string mermaidDiagram)
    {
        var total = allNonSystem.Count;
        var compacted = total - keepCount;

        var sb = new StringBuilder();
        sb.AppendLine($"## Compressed History — {compacted} earlier messages → refs");
        sb.AppendLine();
        sb.AppendLine($"- **Total messages**: {total}");
        sb.AppendLine($"- **Kept verbatim**: {keepCount} (last)");
        sb.AppendLine($"- **Compacted to refs**: {compacted}");
        sb.AppendLine($"- **TraceId**: `{traceId}`");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(mermaidDiagram))
        {
            sb.AppendLine("### Execution Flow");
            sb.AppendLine();
            sb.AppendLine(mermaidDiagram);
            sb.AppendLine();
        }

        sb.AppendLine("> Early message content is available in `.livingtree/refs/` for drill-down.");
        return sb.ToString();
    }
}
