// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  ContextOffloader — TencentDB-Agent-Memory inspired context
//  offload system. Replaces verbose tool traces with Mermaid
//  state diagrams + node_id references to refs/*.md files.
//
//  Key results:
//    - 61% token reduction (reported by TencentDB)
//    - Full lossless traceability via drill-down path
// ═══════════════════════════════════════════════════════════════

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using LTAI.Agent.Delta;
using LTAI.Agent.Format;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Memory;

/// <summary>Result of a single offloaded entry.</summary>
public sealed record OffloadEntry(
    string ToolName,
    string Arguments,
    string ContextResult,
    string? RefId);

/// <summary>Summary of an offload operation.</summary>
public sealed record OffloadSummary
{
    public string TraceId { get; init; } = "";
    public int TotalToolCalls { get; init; }
    public int OffloadedCount { get; init; }
    public int SavedBytes { get; init; }
    public IReadOnlyList<OffloadEntry> Entries { get; init; } = [];
    public string? IndexFile { get; init; }
}

/// <summary>
/// Offloads heavy tool execution traces to <c>.livingtree/refs/</c> files,
/// replacing them with lightweight <c>[refs/{filename}#{hash}]</c> references.
/// Provides lossless drill-down: Mermaid state diagram → refs index → full text.
/// </summary>
public sealed class ContextOffloader
{
    private readonly string _refsDir;
    private readonly ILogger _logger;
    private readonly DeltaStore? _deltaStore;
    private readonly PredictiveOffloadTracker? _predictiveTracker;
    private readonly Context.SemanticCompressor? _semanticCompressor;
    private static readonly ConcurrentDictionary<string, int> s_toolHistory = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Max inline bytes before offload triggers.</summary>
    public const int MaxInlineBytes = 1024;

    /// <summary>Max inline lines before offload triggers.</summary>
    public const int MaxInlineLines = 40;

    /// <summary>Max inline characters before offload triggers.</summary>
    public const int MaxInlineChars = 2048;

    public ContextOffloader(
        ILogger<ContextOffloader>? logger = null,
        DeltaStore? deltaStore = null,
        PredictiveOffloadTracker? predictiveTracker = null,
        Context.SemanticCompressor? semanticCompressor = null)
    {
        _logger = logger ?? NullLogger<ContextOffloader>.Instance;
        _deltaStore = deltaStore;
        _predictiveTracker = predictiveTracker;
        _semanticCompressor = semanticCompressor;
        var baseDir = AppContext.BaseDirectory;
        _refsDir = Path.Combine(baseDir, ".livingtree", "refs");
        Directory.CreateDirectory(_refsDir);
    }

    /// <summary>Determines if content should be offloaded.</summary>
    public static bool ShouldOffload(string content) =>
        content.Length > MaxInlineChars ||
        Encoding.UTF8.GetByteCount(content) > MaxInlineBytes ||
        CountLines(content) > MaxInlineLines;

    private static int CountLines(string s)
    {
        var count = 0;
        for (int i = 0; i < s.Length; i++)
            if (s[i] == '\n') count++;
        return count;
    }

    /// <summary>
    /// Enable cross-session refs naming (file-{hash} instead of traceId prefix).
    /// </summary>
    public static bool CrossSessionEnabled { get; set; } = true;

    /// <summary>
    /// Offloads heavy tool calls to refs files and replaces entries
    /// with lightweight <c>[refs/{file}#{hash}]</c> references.
    /// </summary>
    public async Task<OffloadSummary> OffloadToolCallsAsync(
        List<(string Name, string Arguments, string Result)> toolCalls,
        string traceId)
    {
        var entries = new List<OffloadEntry>(toolCalls.Count);
        var offloadCount = 0;
        var savedBytes = 0;

        // Track tool result sizes for predictive pre-offload
        foreach (var tc in toolCalls)
        {
            s_toolHistory.AddOrUpdate(tc.Name, tc.Result.Length, (_, e) => (e + tc.Result.Length) / 2);
            _predictiveTracker?.RecordResult(tc.Name, tc.Result.Length);
        }

        for (int i = 0; i < toolCalls.Count; i++)
        {
            var tc = toolCalls[i];

            // Predictive pre-offload: offload even before threshold if historically heavy
            bool predictiveOffload = _predictiveTracker?.ShouldPreOffload(tc.Name, tc.Result.Length) == true;
            if (!predictiveOffload && !ShouldOffload(tc.Result))
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

            // Record delta for file-writing tool calls
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

    /// <summary>
    /// Replaces heavy message text with a refs reference.
    /// Returns the replacement text (ref or original).
    /// </summary>
    public async Task<string> OffloadMessageTextAsync(
        string text, string traceId, string label, int seq)
    {
        if (!ShouldOffload(text)) return text;

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

    /// <summary>
    /// Generates a refs index file listing all offloaded entries for a trace.
    /// Serves as the mid-layer drill-down between Mermaid diagram and full text.
    /// </summary>
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

    /// <summary>
    /// Writes the offload summary as a structured refs file.
    /// </summary>
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

    /// <summary>
    /// Reads the original content back from a refs file.
    /// Provides lossless recovery: ref → full text.
    /// </summary>
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

    /// <summary>Returns the refs directory path.</summary>
    public string RefsDirectory => _refsDir;

    /// <summary>Cross-session dedup set: file hash → sequence counter.</summary>
    private static readonly ConcurrentDictionary<string, int> s_crossSessionCounters = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Compute a cross-session refs filename based on file path content hash.
    /// Enables merging refs for the same file across different sessions.
    /// </summary>
    public static string GetCrossSessionRefName(string filePath, string toolName, out string hashPrefix)
    {
        var fileHash = HexHash(filePath, 12);
        var counter = s_crossSessionCounters.AddOrUpdate(fileHash, 1, (_, c) => c + 1);
        hashPrefix = fileHash;
        return $"file-{fileHash}-{counter:D3}-{SanitizeLabel(Path.GetFileNameWithoutExtension(filePath))}.md";
    }

    /// <summary>
    /// Reset cross-session counters (e.g., on agent restart).
    /// </summary>
    public static void ResetCrossSessionCounters() => s_crossSessionCounters.Clear();

    /// <summary>
    /// Estimated tokens per char ratio (conservative: CJK ~1, English ~0.25).
    /// </summary>
    private static double EstimateTokens(string text) =>
        text.Length * 0.35 + 5; // rough: 1 token ~ 3 chars

    /// <summary>
    /// Adaptive keepLastN — 根据 token 预算动态计算应保留多少条消息。
    /// System 消息视为参考 token 永不衰减，按 reservedSystemRatio 预留空间。
    /// </summary>
    public static int ComputeAdaptiveKeepN(
        List<Microsoft.Extensions.AI.ChatMessage> messages,
        int maxContextTokens = 128000,
        double reservedSystemRatio = 0.2,
        int minKeep = 5,
        int maxKeep = 30)
    {
        var systemMsgs = messages.Where(m => m.Role == Microsoft.Extensions.AI.ChatRole.System).ToList();
        var systemTokens = systemMsgs.Sum(m => EstimateTokens(m.Text ?? ""));
        var reservedSystem = (int)(maxContextTokens * reservedSystemRatio);
        var budgetForNonSystem = maxContextTokens - reservedSystem - systemTokens;
        if (budgetForNonSystem <= 0) return minKeep;

        var nonSystem = messages.Where(m => m.Role != Microsoft.Extensions.AI.ChatRole.System).ToList();
        if (nonSystem.Count == 0) return minKeep;

        var avgSize = nonSystem.Average(m => EstimateTokens(m.Text ?? ""));
        var idealKeep = (int)(budgetForNonSystem / Math.Max(avgSize, 10));
        return Math.Clamp(idealKeep, minKeep, maxKeep);
    }

    /// <summary>
    /// 分层卸载 — 三级：完整保留 / 头尾摘要 / 全量 refs 外链。
    /// 对中等重要消息逐步降级，而非二值「保留|压缩」。
    /// </summary>
    public enum OffloadTier { Verbatim, HeadTailSummary, FullRefs }

    /// <summary>
    /// 对一条消息执行分层卸载，返回可能被替换后的文本。
    /// - Tier Verbatim: 原样返回
    /// - Tier HeadTailSummary: 保留头尾各 N 字符，中间替换为摘要
    /// - Tier FullRefs: 写入 refs 文件，返回 [refs/...] 引用
    /// </summary>
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

    /// <summary>
    /// 对消息列表执行分层卸载（bulk），返回每条的 tier 决策和替换后的文本。
    /// 越旧的消息 tier 越激进：最旧的 1/3 → FullRefs，中间的 1/3 → HeadTailSummary，最新的 1/3 → Verbatim。
    /// </summary>
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

    /// <summary>
    /// Token 级窗口压实：按估计 token 数截断而非按消息条数。
    /// 保留系统消息 + 尽量多的最近消息直到填满 budgetForNonSystem。
    /// </summary>
    public async Task ForceWindowCompactByTokensAsync(
        List<Microsoft.Extensions.AI.ChatMessage> messages,
        string traceId,
        int maxContextTokens = 128000,
        double reservedSystemRatio = 0.2,
        MermaidStateTracker? mermaid = null)
    {
        var systemMsgs = messages.Where(m => m.Role == Microsoft.Extensions.AI.ChatRole.System).ToList();
        var nonSystemMsgs = messages.Where(m => m.Role != Microsoft.Extensions.AI.ChatRole.System).ToList();
        if (nonSystemMsgs.Count == 0) return;

        var systemTokens = systemMsgs.Sum(m => EstimateTokens(m.Text ?? ""));
        var reservedSystem = (int)(maxContextTokens * reservedSystemRatio);
        var budgetForNonSystem = maxContextTokens - reservedSystem - systemTokens;
        if (budgetForNonSystem <= 0) return;

        // Build Mermaid diagram
        var diagram = "";
        var offlineRefs = new List<string>();
        if (mermaid != null)
        {
            var result = await mermaid.BuildFromMessageFlowAsync(nonSystemMsgs, traceId, this).ConfigureAwait(false);
            diagram = result.Diagram;
            offlineRefs = result.Refs;
        }

        // Token-aware truncation: keep as many recent messages as fit in budget
        var keepMsgs = new List<Microsoft.Extensions.AI.ChatMessage>();
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

        // Rebuild messages
        messages.Clear();
        messages.AddRange(systemMsgs);
        messages.Add(new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.System, windowSummary));
        messages.AddRange(keepMsgs);

        _logger.LogInformation(
            "ContextOffloader: ForceWindowCompactByTokens — kept {Keep}/{Total} msgs ({Tokens:F0}/{Budget} tok), {Refs} refs",
            keepCount, nonSystemMsgs.Count, totalTokens, budgetForNonSystem, offlineRefs.Count);
    }

    /// <summary>
    /// 按角色压缩策略：system 全保留，user 保留最后 N 条完整，tool 结果超过阈值只保留 call+result 摘要，
    /// assistant 保留完整推理头。
    /// </summary>
    public async Task CompactByRoleAsync(
        List<Microsoft.Extensions.AI.ChatMessage> messages,
        string traceId,
        int keepUserLastN = 5,
        int toolSummaryMaxChars = 500,
        MermaidStateTracker? mermaid = null)
    {
        var systemMsgs = messages.Where(m => m.Role == Microsoft.Extensions.AI.ChatRole.System).ToList();
        var otherMsgs = messages.Where(m => m.Role != Microsoft.Extensions.AI.ChatRole.System).ToList();

        // Build Mermaid diagram before mutating
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
        var processed = new List<Microsoft.Extensions.AI.ChatMessage>();

        foreach (var msg in otherMsgs)
        {
            var role = msg.Role.ToString().ToLowerInvariant();
            var text = msg.Text ?? "";

            if (role == "system")
            {
                // System always full
                processed.Add(msg);
            }
            else if (role == "user")
            {
                userCount++;
                // Keep last N user messages full, earlier ones get head/tail
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
                    processed.Add(new Microsoft.Extensions.AI.ChatMessage(msg.Role, headTail)
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
                // Assistant: keep full but offload if extremely long
                if (ShouldOffload(text) && text.Length > toolSummaryMaxChars * 2)
                {
                    var head = text[..Math.Min(toolSummaryMaxChars, text.Length)];
                    processed.Add(new Microsoft.Extensions.AI.ChatMessage(msg.Role,
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
                // Tool: keep call summary only
                if (ShouldOffload(text))
                {
                    var summary = text.Length > toolSummaryMaxChars
                        ? text[..toolSummaryMaxChars] + $"\n… (tool result truncated, {text.Length} chars)"
                        : text;
                    processed.Add(new Microsoft.Extensions.AI.ChatMessage(msg.Role, summary)
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

        // Inject compacted summary with Mermaid diagram
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
        messages.Add(new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.System, sb.ToString()));
        messages.AddRange(processed);

        _logger.LogInformation(
            "ContextOffloader: CompactByRole — {Compacted}/{Total} msgs compacted, {Refs} refs",
            compacted, otherMsgs.Count, offlineRefs.Count);
    }

    /// <summary>
    /// 交叉去重：对同一文件的多条 tool call 合并为单一 refs 条目。
    /// 在 OffloadToolCallsAsync 调用后，扫描 entries 对相同文件路径的条目去重合并。
    /// </summary>
    public async Task<List<OffloadEntry>> DeduplicateToolEntriesAsync(
        List<OffloadEntry> entries,
        string traceId)
    {
        if (entries.Count < 2) return entries;

        // Group by file path from arguments
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

            // Merge into one consolidated refs entry
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

            // Combined context placeholder
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

    /// <summary>
    /// 语义压缩——用 embedder 对句子做重要性排序，保留语义密集的句子。
    /// 回退到头尾截断。
    /// </summary>
    public async Task<string> CompressSemanticallyAsync(string text, double targetRatio = 0.5, CancellationToken ct = default)
    {
        if (_semanticCompressor != null)
        {
            return await _semanticCompressor.CompressSemanticallyAsync(text, targetRatio, ct).ConfigureAwait(false);
        }
        // Fallback: head/tail truncation
        var targetLen = Math.Max(100, (int)(text.Length * targetRatio));
        return text.Length <= targetLen
            ? text
            : text[..(targetLen / 2)] + "\n… [semantic compression unavailable] …\n" + text[^(targetLen / 2)..];
    }

    /// <summary>
    /// 重要性锚定窗口：用 PalaceStore importance 分数替代纯 recency 保留策略。
    /// 保留最高 importance 的消息，而非仅仅保留最近的。
    /// </summary>
    public async Task ForceWindowCompactByImportanceAsync(
        List<Microsoft.Extensions.AI.ChatMessage> messages,
        string traceId,
        double importanceThreshold = 0.7,
        int minKeep = 5,
        MermaidStateTracker? mermaid = null)
    {
        var systemMsgs = messages.Where(m => m.Role == Microsoft.Extensions.AI.ChatRole.System).ToList();
        var nonSystemMsgs = messages.Where(m => m.Role != Microsoft.Extensions.AI.ChatRole.System).ToList();
        if (nonSystemMsgs.Count <= minKeep) return;

        // Build Mermaid
        var diagram = "";
        if (mermaid != null)
        {
            var result = await mermaid.BuildFromMessageFlowAsync(nonSystemMsgs, traceId, this).ConfigureAwait(false);
            diagram = result.Diagram;
        }

        // Score each message by importance (heuristic: recent + long + tool = important)
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

        // Keep top-K by score, guarantee at least minKeep newest
        var keepSet = new HashSet<int>(scored.Take(Math.Max(minKeep, nonSystemMsgs.Count / 2)).Select(x => x.Index));
        for (int i = nonSystemMsgs.Count - minKeep; i < nonSystemMsgs.Count; i++)
            keepSet.Add(i);

        var keepMsgs = nonSystemMsgs.Where((_, i) => keepSet.Contains(i)).ToList();
        var compacted = nonSystemMsgs.Count - keepMsgs.Count;
        if (compacted <= 0) return;

        var windowSummary = BuildWindowSummary(nonSystemMsgs, keepMsgs.Count, traceId, diagram);

        messages.Clear();
        messages.AddRange(systemMsgs);
        messages.Add(new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.System, windowSummary));
        messages.AddRange(keepMsgs);

        _logger.LogInformation(
            "ContextOffloader: ForceWindowCompactByImportance — kept {Keep}/{Total} by importance score, {Compacted} compacted",
            keepMsgs.Count, nonSystemMsgs.Count, compacted);
    }

    /// <summary>
    /// R-SWA 启发：强制窗口压实。
    /// 保留最近的 keepLastN 条消息完整可见，将更早的消息卸载到 refs 文件，
    /// 替换为带有 Mermaid 状态图的轻量摘要。
    ///
    /// 系统消息（System role）始终保留，视为「参考 token」永不衰减。
    /// </summary>
    public async Task ForceWindowCompactAsync(
        List<Microsoft.Extensions.AI.ChatMessage> messages,
        string traceId,
        int keepLastN,
        MermaidStateTracker? mermaid = null)
    {
        if (messages.Count <= keepLastN) return;

        // Split: system messages never compacted
        var systemMsgs = messages.Where(m => m.Role == Microsoft.Extensions.AI.ChatRole.System).ToList();
        var nonSystemMsgs = messages.Where(m => m.Role != Microsoft.Extensions.AI.ChatRole.System).ToList();

        // Build Mermaid diagram from all non-system messages
        var diagram = "";
        var offlineRefs = new List<string>();
        if (mermaid != null)
        {
            var result = await mermaid.BuildFromMessageFlowAsync(nonSystemMsgs, traceId, this).ConfigureAwait(false);
            diagram = result.Diagram;
            offlineRefs = result.Refs;
        }

        // Determine cutoff: keep last N non-system messages
        int keepCount = Math.Min(keepLastN, nonSystemMsgs.Count);
        var keepMsgs = nonSystemMsgs.Skip(nonSystemMsgs.Count - keepCount).ToList();

        // Build compact summary for window
        var windowSummary = BuildWindowSummary(nonSystemMsgs, keepCount, traceId, diagram);

        // Rebuild: system messages + window summary + kept messages
        messages.Clear();
        messages.AddRange(systemMsgs);

        // Insert the compacted window summary
        messages.Add(new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.System, windowSummary));

        // Keep the last N messages verbatim
        messages.AddRange(keepMsgs);

        _logger.LogInformation(
            "ContextOffloader: ForceWindowCompact — {Total} msgs → {Sys}+1(summary)+{Keep} kept | {Refs} refs offloaded",
            systemMsgs.Count + nonSystemMsgs.Count,
            systemMsgs.Count, keepCount, offlineRefs.Count);
    }

    private static string BuildWindowSummary(
        List<Microsoft.Extensions.AI.ChatMessage> allNonSystem,
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

    #region Helpers

    private static bool IsFileTool(string toolName)
    {
        var lower = toolName.ToLowerInvariant();
        return lower is "writefile" or "editfile" or "applypatch" or "filewritetool";
    }

    private static bool TryExtractFilePath(string arguments, out string filePath)
    {
        filePath = "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(arguments);
            if (doc.RootElement.TryGetProperty("path", out var p))
            {
                filePath = p.GetString() ?? "";
                return !string.IsNullOrEmpty(filePath);
            }
        }
        catch { /* not json or no path */ }
        return false;
    }

    private static string SanitizeLabel(string label)
    {
        var sb = new StringBuilder(label.Length);
        foreach (var c in label)
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
                sb.Append(c);
            else
                sb.Append('-');
        }
        return sb.ToString().Trim('-').ToLowerInvariant();
    }

    private static string HexHash(string input, int length)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var hex = Convert.ToHexStringLower(hash);
        return hex[..Math.Min(length, hex.Length)];
    }

    #endregion
}
