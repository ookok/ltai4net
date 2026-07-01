// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  ContextOffloader — Offload operations: tool calls, messages,
//  refs index/summary, cross-session naming
// ═══════════════════════════════════════════════════════════════

using System.Collections.Concurrent;
using System.Text;
using LTAI.Agent.Delta;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Memory;

partial class ContextOffloader
{
    public async Task<OffloadSummary> OffloadToolCallsAsync(
        List<(string Name, string Arguments, string Result)> toolCalls,
        string traceId)
    {
        var entries = new List<OffloadEntry>(toolCalls.Count);
        var offloadCount = 0;
        var savedBytes = 0;

        foreach (var tc in toolCalls)
        {
            s_toolHistory.AddOrUpdate(tc.Name, tc.Result.Length, (_, e) => (e + tc.Result.Length) / 2);
            _predictiveTracker?.RecordResult(tc.Name, tc.Result.Length);
        }

        for (int i = 0; i < toolCalls.Count; i++)
        {
            var tc = toolCalls[i];

            bool predictiveOffload = _predictiveTracker?.ShouldPreOffload(tc.Name, tc.Result.Length) == true;
            if (!predictiveOffload && !ShouldOffloadAdaptive(tc.Result))
            {
                entries.Add(new OffloadEntry(tc.Name, tc.Arguments, tc.Result, null));
                continue;
            }

            var seq = offloadCount + 1;
            var label = SanitizeLabel(tc.Name);
            string filename;
            if (CrossSessionEnabled && IsFileTool(tc.Name) && TryExtractFilePath(tc.Arguments, out var csFilePath))
            {
                filename = GetCrossSessionRefName(csFilePath, tc.Name, out _);
            }
            else
            {
                filename = $"{traceId}-{seq:D3}-{label}.md";
            }
            var refPath = Path.Combine(_refsDir, filename);

            var md = new StringBuilder();
            md.AppendLine($"# {tc.Name} — Execution Trace #{seq}");
            md.AppendLine();
            md.AppendLine($"- **TraceId**: `{traceId}`");
            md.AppendLine($"- **Tool**: `{tc.Name}`");
            md.AppendLine($"- **Arguments**:");
            md.AppendLine("```json");
            md.AppendLine(tc.Arguments);
            md.AppendLine("```");
            md.AppendLine($"- **Result** ({tc.Result.Length} chars):");
            md.AppendLine("```");
            md.AppendLine(tc.Result);
            md.AppendLine("```");

            await File.WriteAllTextAsync(refPath, md.ToString(), Encoding.UTF8);

            var hash = HexHash(tc.Result, 12);
            var refId = $"{filename}#{hash}";
            offloadCount++;
            savedBytes += tc.Result.Length;

            var babelTele = Format.BabelTeleFormatter.EncodeToolResult(tc.Name, tc.Arguments, tc.Result, seq);
            toolCalls[i] = (tc.Name, tc.Arguments, $"{babelTele} [refs/{refId}]");
            entries.Add(new OffloadEntry(tc.Name, tc.Arguments, $"{babelTele} [refs/{refId}]", refId));

            if (_deltaStore != null && IsFileTool(tc.Name) && TryExtractFilePath(tc.Arguments, out var filePath))
            {
                try
                {
                    var dId = await _deltaStore.CreateDeltaForEditAsync(
                        filePath: filePath,
                        startLine: 1,
                        endLine: 1,
                        diffContent: $"[offloaded refs/{refId}]",
                        toolName: tc.Name,
                        conversationId: traceId,
                        messageId: $"{traceId}-{seq:D3}",
                        agentId: "ContextOffloader").ConfigureAwait(false);
                    _logger.LogDebug("ContextOffloader: delta {DeltaId} for {File}", dId[..12], filePath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ContextOffloader: failed to record delta for {File}", filePath);
                }
            }
        }

        var summary = new OffloadSummary
        {
            TraceId = traceId,
            TotalToolCalls = toolCalls.Count,
            OffloadedCount = offloadCount,
            SavedBytes = savedBytes,
            Entries = entries,
        };

        summary = summary with { IndexFile = await GenerateIndexAsync(traceId, entries) };
        _logger.LogInformation(
            "ContextOffloader: offloaded {Offloaded}/{Total} tool calls, saved {SavedBytes:N0} bytes",
            offloadCount, toolCalls.Count, savedBytes);

        return summary;
    }

    public async Task<string> OffloadMessageTextAsync(
        string text, string traceId, string label, int seq)
    {
        if (!ShouldOffloadAdaptive(text)) return text;

        var filename = $"{traceId}-msg-{seq:D3}-{SanitizeLabel(label)}.md";
        var refPath = Path.Combine(_refsDir, filename);

        var md = new StringBuilder();
        md.AppendLine($"# Message — {label} #{seq}");
        md.AppendLine();
        md.AppendLine($"- **TraceId**: `{traceId}`");
        md.AppendLine($"- **Label**: {label}");
        md.AppendLine($"- **Content** ({text.Length} chars):");
        md.AppendLine("```");
        md.AppendLine(text);
        md.AppendLine("```");

        await File.WriteAllTextAsync(refPath, md.ToString(), Encoding.UTF8);

        var hash = HexHash(text, 8);
        return $"[refs/{filename}#{hash}]";
    }

    public async Task<string> GenerateIndexAsync(
        string traceId, IReadOnlyList<OffloadEntry> entries)
    {
        var filename = $"{traceId}-index.md";
        var refPath = Path.Combine(_refsDir, filename);

        var md = new StringBuilder();
        md.AppendLine($"# Refs Index — {traceId}");
        md.AppendLine();
        md.AppendLine($"| # | Tool | Ref | Arguments |");
        md.AppendLine($"|---|------|-----|-----------|");

        var idx = 0;
        foreach (var entry in entries)
        {
            idx++;
            var args = entry.Arguments.Length > 80
                ? entry.Arguments[..80] + "…"
                : entry.Arguments;
            var refLink = entry.RefId ?? "(inline)";
            md.AppendLine($"| {idx} | `{entry.ToolName}` | `{refLink}` | `{args}` |");
        }

        md.AppendLine();
        md.AppendLine($"---");
        md.AppendLine($"*Index generated at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC*");

        await File.WriteAllTextAsync(refPath, md.ToString(), Encoding.UTF8);
        return filename;
    }

    public async Task WriteSummaryAsync(OffloadSummary summary)
    {
        var filename = $"{summary.TraceId}-summary.md";
        var refPath = Path.Combine(_refsDir, filename);

        var md = new StringBuilder();
        md.AppendLine($"# Offload Summary — {summary.TraceId}");
        md.AppendLine();
        md.AppendLine($"- **Total tool calls**: {summary.TotalToolCalls}");
        md.AppendLine($"- **Offloaded**: {summary.OffloadedCount}");
        md.AppendLine($"- **Saved bytes**: {summary.SavedBytes:N0}");
        md.AppendLine($"- **Index**: `{summary.IndexFile}`");
        md.AppendLine();

        var offloaded = summary.Entries.Where(e => e.RefId != null).ToList();
        if (offloaded.Count > 0)
        {
            md.AppendLine("## Offloaded Entries");
            md.AppendLine();
            foreach (var entry in offloaded)
            {
                md.AppendLine($"- `{entry.RefId}` — **{entry.ToolName}**");
            }
        }

        await File.WriteAllTextAsync(refPath, md.ToString(), Encoding.UTF8);
    }

    public async Task<string?> ReadRefAsync(string refId)
    {
        var parts = refId.Split('#');
        var filename = parts[0];
        var refPath = Path.Combine(_refsDir, filename);
        if (!File.Exists(refPath)) return null;

        var content = await File.ReadAllTextAsync(refPath, Encoding.UTF8);

        if (parts.Length > 1 && !string.IsNullOrEmpty(parts[1]))
        {
            var expectedHash = parts[1];
            var actualHash = HexHash(content, expectedHash.Length);
            if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("ContextOffloader: hash mismatch for {RefId}", refId);
                return null;
            }
        }

        return content;
    }

    public static string GetCrossSessionRefName(string filePath, string toolName, out string hashPrefix)
    {
        var fileHash = HexHash(filePath, 12);
        var counter = s_crossSessionCounters.AddOrUpdate(fileHash, 1, (_, c) => c + 1);
        hashPrefix = fileHash;
        return $"file-{fileHash}-{counter:D3}-{SanitizeLabel(Path.GetFileNameWithoutExtension(filePath))}.md";
    }
}
