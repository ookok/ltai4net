// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  ContextOffloader — Tiered offloading, dedup, semantic compression
// ═══════════════════════════════════════════════════════════════

using System.Collections.Concurrent;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Memory;

partial class ContextOffloader
{
    public enum OffloadTier { Verbatim, HeadTailSummary, FullRefs }

    public async Task<string> TieredOffloadAsync(
        string text,
        string traceId,
        string label,
        int seq,
        OffloadTier tier,
        int headTailChars = 200)
    {
        if (!ShouldOffload(text)) return text;

        switch (tier)
        {
            case OffloadTier.Verbatim:
                return text;

            case OffloadTier.HeadTailSummary:
                if (text.Length <= headTailChars * 2 + 50) return text;
                var head = text[..headTailChars];
                var tail = text[^Math.Min(headTailChars, text.Length)..];
                var midSummary = $"… [{text.Length - headTailChars * 2} chars compressed] …";
                return $"{head}{midSummary}{tail}";

            case OffloadTier.FullRefs:
                return await OffloadMessageTextAsync(text, traceId, label, seq).ConfigureAwait(false);

            default:
                return text;
        }
    }

    public async Task<List<(int Index, string NewText, OffloadTier Tier, string? RefId)>> BulkTieredOffloadAsync(
        List<Microsoft.Extensions.AI.ChatMessage> nonSystemMessages,
        string traceId)
    {
        var results = new List<(int Index, string NewText, OffloadTier Tier, string? RefId)>();
        if (nonSystemMessages.Count == 0) return results;

        var total = nonSystemMessages.Count;
        int fullRefsCutoff = total / 3;
        int headTailCutoff = total * 2 / 3;

        for (int i = 0; i < total; i++)
        {
            var msg = nonSystemMessages[i];
            var tier = i < fullRefsCutoff ? OffloadTier.FullRefs
                : i < headTailCutoff ? OffloadTier.HeadTailSummary
                : OffloadTier.Verbatim;

            var label = $"{msg.Role}-{i}";
            var newText = await TieredOffloadAsync(
                msg.Text ?? "", traceId, label, i, tier).ConfigureAwait(false);

            string? refId = null;
            if (newText.StartsWith("[refs/"))
                refId = newText.Replace("[refs/", "").Replace("]", "");

            results.Add((i, newText, tier, refId));
        }

        return results;
    }

    public async Task<List<OffloadEntry>> DeduplicateToolEntriesAsync(
        List<OffloadEntry> entries,
        string traceId)
    {
        if (entries.Count < 2) return entries;

        var grouped = new Dictionary<string, List<OffloadEntry>>(StringComparer.OrdinalIgnoreCase);
        var standalone = new List<OffloadEntry>();

        foreach (var entry in entries)
        {
            if (TryExtractFilePath(entry.Arguments, out var fp) && entry.RefId != null)
            {
                if (!grouped.ContainsKey(fp))
                    grouped[fp] = [];
                grouped[fp].Add(entry);
            }
            else
            {
                standalone.Add(entry);
            }
        }

        var merged = new List<OffloadEntry>(standalone);

        foreach (var (filePath, group) in grouped)
        {
            if (group.Count <= 1)
            {
                merged.AddRange(group);
                continue;
            }

            var seq = merged.Count + 1;
            var filename = $"{traceId}-merged-{seq}-{SanitizeLabel(Path.GetFileNameWithoutExtension(filePath))}.md";
            var refPath = Path.Combine(_refsDir, filename);

            var md = new StringBuilder();
            md.AppendLine($"# Merged Tool Calls — `{filePath}`");
            md.AppendLine();
            md.AppendLine($"- **File**: `{filePath}`");
            md.AppendLine($"- **Merged entries**: {group.Count}");
            md.AppendLine();

            for (int i = 0; i < group.Count; i++)
            {
                var e = group[i];
                md.AppendLine($"## {i + 1}. {e.ToolName}");
                md.AppendLine();
                md.AppendLine($"- **Arguments**: `{e.Arguments}`");
                md.AppendLine($"- **Ref**: `{e.RefId}`");
                md.AppendLine();
                md.AppendLine("### Result");
                md.AppendLine("```");
                md.AppendLine(e.ContextResult);
                md.AppendLine("```");
                md.AppendLine();
            }

            await File.WriteAllTextAsync(refPath, md.ToString(), Encoding.UTF8);
            var hash = HexHash(md.ToString(), 12);
            var mergedRefId = $"{filename}#{hash}";

            var combinedResult = $"[refs/{mergedRefId}] (merged {group.Count} calls on {filePath})";
            merged.Add(new OffloadEntry(
                $"{group[0].ToolName}+{group.Count}",
                group[0].Arguments,
                combinedResult,
                mergedRefId));

            _logger.LogInformation(
                "ContextOffloader: deduped {Count} tool calls on {File} → {Ref}",
                group.Count, filePath, mergedRefId);
        }

        return merged;
    }

    public async Task<string> CompressSemanticallyAsync(string text, double targetRatio = 0.5, CancellationToken ct = default)
    {
        if (_semanticCompressor != null)
        {
            return await _semanticCompressor.CompressSemanticallyAsync(text, targetRatio, ct).ConfigureAwait(false);
        }
        var targetLen = Math.Max(100, (int)(text.Length * targetRatio));
        return text.Length <= targetLen
            ? text
            : text[..(targetLen / 2)] + "\n… [semantic compression unavailable] …\n" + text[^(targetLen / 2)..];
    }
}
