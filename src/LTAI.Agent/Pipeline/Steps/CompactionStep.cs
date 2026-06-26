// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  CompactionStep — history/token compression with TencentDB-
//  Agent-Memory inspired context offload + Mermaid state diagram.
//
//  V2: KVEraser-inspired incremental compaction (arXiv:2606.17034).
//  Instead of re-compressing all messages each time, tracks which
//  messages have been compressed and skips them. Only newly-added,
//  uncompressed messages are compacted — avoiding "re-compression
//  damage" and reducing CPU cost for stable context windows.
// ═══════════════════════════════════════════════════════════════

using System.Text;
using System.Text.Json;
using LTAI.Agent.Context;
using LTAI.Agent.Memory;
using LTAI.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Pipeline.Steps;

public sealed class CompactionStep : IPipelineStep
{
    private readonly ILogger<CompactionStep> _logger;
    private readonly double _ratioThreshold;
    private readonly TieredCompressor _tiered;
    private readonly ContextOffloader? _offloader;
    private readonly MermaidStateTracker? _mermaid;
    private readonly int _keepLastN;
    private readonly int _maxContextTokens;
    private readonly double _reservedSystemRatio;
    private CompactionConfig? _config;
    private DateTime _configLastLoad = DateTime.MinValue;
    private static readonly TimeSpan s_configReloadInterval = TimeSpan.FromSeconds(30);
    private readonly string _configPath;
    private int _streamTokenCheckCounter;

    // KVEraser-inspired: track compressed message hashes for incremental compaction.
    // Messages whose content hash matches a previously-compressed version are skipped.
    private readonly HashSet<int> _compressedHashes = [];

    public string Name => "Compaction";

    public double RatioThreshold => _ratioThreshold;
    public int KeepLastN => _keepLastN;

    public CompactionStep(
        ILogger<CompactionStep>? logger = null,
        double ratioThreshold = 0.75,
        TieredCompressor? tieredCompressor = null,
        ContextOffloader? offloader = null,
        MermaidStateTracker? mermaidTracker = null,
        int keepLastN = 10,
        int maxContextTokens = 128000,
        double reservedSystemRatio = 0.2,
        string? configPath = null)
    {
        _logger = logger ?? NullLogger<CompactionStep>.Instance;
        _ratioThreshold = ratioThreshold;
        _tiered = tieredCompressor ?? new TieredCompressor();
        _offloader = offloader;
        _mermaid = mermaidTracker;
        _keepLastN = keepLastN;
        _maxContextTokens = maxContextTokens;
        _reservedSystemRatio = reservedSystemRatio;
        _configPath = configPath ?? Path.Combine(AppContext.BaseDirectory, ".livingtree", "workflows", "compact-config.json");
    }

    public CompactionStep() : this(null, 0.75, null, null, null, 10, 128000, 0.2, null) { }

    private CompactionConfig GetConfig()
    {
        if (_config != null && (DateTime.UtcNow - _configLastLoad) < s_configReloadInterval)
            return _config;

        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                var parsed = CompactionConfig.Parse(json);
                if (parsed != null)
                {
                    _config = parsed;
                    _configLastLoad = DateTime.UtcNow;
                    _logger.LogDebug("CompactionStep: loaded config from {Path}", _configPath);
                    return _config;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CompactionStep: failed to load config from {Path}", _configPath);
        }

        _config = CompactionConfig.Default;
        _configLastLoad = DateTime.UtcNow;
        return _config;
    }

    // ── Fidelity scoring ──
    private static CompressionFidelity ComputeFidelity(
        List<ChatMessage> before,
        List<ChatMessage> after,
        string action)
    {
        var perRole = new Dictionary<string, double>();
        foreach (var role in new[] { "system", "user", "assistant", "tool" })
        {
            var bTexts = before.Where(m => string.Equals(m.Role.ToString(), role, StringComparison.OrdinalIgnoreCase))
                .Sum(m => m.Text?.Length ?? 0);
            var aTexts = after.Where(m => string.Equals(m.Role.ToString(), role, StringComparison.OrdinalIgnoreCase))
                .Sum(m => m.Text?.Length ?? 0);
            perRole[role] = bTexts > 0 ? Math.Round((double)aTexts / bTexts, 2) : 1.0;
        }

        var totalBefore = before.Sum(m => m.Text?.Length ?? 0);
        var totalAfter = after.Sum(m => m.Text?.Length ?? 0);
        var overall = totalBefore > 0 ? Math.Round((double)totalAfter / totalBefore, 2) : 1.0;

        return new CompressionFidelity
        {
            TotalMessages = before.Count,
            CompactedMessages = before.Count - after.Count,
            OverallFidelity = overall,
            PerRoleFidelity = perRole,
            CompressionLevel = overall >= 0.8 ? "light" : overall >= 0.5 ? "moderate" : "heavy",
            ActionTaken = action,
        };
    }

    public async Task<MessageContext> ProcessAsync(MessageContext context)
    {
        var cfg = GetConfig();
        var contextRatio = UsageTracker.ContextRatio();
        if (contextRatio < _ratioThreshold)
        {
            _logger.LogDebug("CompactionStep: context {Pct:F0}% < threshold {Threshold:P0}, skipping",
                contextRatio * 100, _ratioThreshold);
            return context;
        }

        _logger.LogInformation("CompactionStep: context {Pct:F0}% > threshold {Threshold:P0}, compressing",
            contextRatio * 100, _ratioThreshold);

        var traceId = context.TraceId ?? Guid.NewGuid().ToString("N")[..12];
        var beforeSnapshot = context.Messages.Select(m => new ChatMessage(m.Role, m.Text ?? "") { AuthorName = m.AuthorName }).ToList();

        // ── Phase A: Tool trace offload + predictive tracking ──
        await OffloadToolTracesAsync(context);

        // ── Phase 0: Load config thresholds ──
        var t = cfg.Thresholds;
        var keep = cfg.Keep;
        var roles = cfg.Roles;
        string compressionAction;

        // ── Phase 1: Progressive back-pressure ──
        if (contextRatio >= t.Critical)
        {
            // critical: emergency — keep only system + 1 message
            var systemMsgs = context.Messages.Where(m => m.Role == ChatRole.System).ToList();
            var lastMsg = context.Messages.LastOrDefault(m => m.Role != ChatRole.System);
            context.Messages.Clear();
            context.Messages.AddRange(systemMsgs);
            if (lastMsg != null) context.Messages.Add(lastMsg);
            compressionAction = $"critical(keep=system+1, ratio={contextRatio:P0})";
            context.CompactionPressure += 5;
        }
        else if (contextRatio >= t.Heavy && _offloader != null)
        {
            // heavy: importance-anchored window compaction (preserve important messages, not just recent)
            if (_offloader != null)
            {
                await _offloader.ForceWindowCompactByImportanceAsync(
                    context.Messages, traceId,
                    importanceThreshold: 0.7,
                    minKeep: keep.Heavy,
                    _mermaid).ConfigureAwait(false);
                compressionAction = $"heavy(importance-anchored minKeep={keep.Heavy})";
            }
            else
            {
                var effectiveKeep = (int)(keep.Heavy * context.AggressivenessMultiplier);
                await _offloader.ForceWindowCompactByTokensAsync(
                    context.Messages, traceId, _maxContextTokens, _reservedSystemRatio, _mermaid).ConfigureAwait(false);
                compressionAction = $"heavy(adaptiveKeep={effectiveKeep})";
            }
            context.CompactionPressure += 3;

            if (_mermaid != null)
                context.Set("MermaidDiagram", _mermaid.AppendNote($"importance-anchored compaction at {contextRatio:P0}"));
        }
        else if (contextRatio >= t.Moderate && _offloader != null)
        {
            // moderate: CompactByRole
            await _offloader.CompactByRoleAsync(
                context.Messages, traceId,
                keepUserLastN: Math.Max(3, (int)(roles.KeepUserLastN * context.AggressivenessMultiplier)),
                toolSummaryMaxChars: roles.ToolSummaryMaxChars,
                _mermaid).ConfigureAwait(false);
            compressionAction = $"moderate(CompactByRole)";
            context.CompactionPressure += 1;

            if (_mermaid != null)
                context.Set("MermaidDiagram", _mermaid.AppendNote($"role compaction at {contextRatio:P0}"));
        }
        else if (contextRatio >= t.Light)
        {
            // light: semantic compression first, fallback to tiered (KVEraser: incremental)
            var compressedCount = 0;
            var skippedCount = 0;
            var originalLength = 0;
            var compressedLength = 0;

            if (_offloader != null && context.Messages.Count > 3)
            {
                // Semantic compression on earliest non-system, uncompressed messages
                for (int i = 0; i < context.Messages.Count / 2; i++)
                {
                    var msg = context.Messages[i];
                    if (msg.Role == ChatRole.System || string.IsNullOrEmpty(msg.Text)) continue;

                    // KVEraser: skip if already compressed (same content hash)
                    var msgHash = GetContentHash(msg.Text);
                    if (_compressedHashes.Contains(msgHash))
                    {
                        skippedCount++;
                        continue;
                    }

                    originalLength += msg.Text.Length;

                    var compressed = await _offloader.CompressSemanticallyAsync(msg.Text, targetRatio: 0.6).ConfigureAwait(false);
                    if (compressed.Length < msg.Text.Length)
                    {
                        context.Messages[i] = new ChatMessage(msg.Role, compressed) { AuthorName = msg.AuthorName };
                        compressedLength += compressed.Length;
                        compressedCount++;
                        _compressedHashes.Add(GetContentHash(compressed));
                    }
                    else
                    {
                        compressedLength += msg.Text.Length;
                        _compressedHashes.Add(msgHash);
                    }
                }
            }

            if (compressedCount == 0 && skippedCount == 0)
            {
                // Fallback: standard tiered with incremental skip
                var messageTexts = context.Messages.Where(m => !string.IsNullOrEmpty(m.Text)).Select(m => m.Text!).ToList();
                var convType = _tiered.DetectType(messageTexts);

                for (int i = 0; i < context.Messages.Count; i++)
                {
                    var msg = context.Messages[i];
                    if (string.IsNullOrEmpty(msg.Text)) continue;

                    var msgHash = GetContentHash(msg.Text);
                    if (_compressedHashes.Contains(msgHash))
                    {
                        skippedCount++;
                        continue;
                    }

                    var tier = _tiered.Classify(i, context.Messages.Count);
                    var ratio = _tiered.GetCompressionRatio(tier, convType);
                    originalLength += msg.Text.Length;
                    var compressed = CompressWithRatio(msg.Text, ratio);
                    if (compressed.Length < msg.Text.Length)
                    {
                        context.Messages[i] = new ChatMessage(msg.Role, compressed) { AuthorName = msg.AuthorName };
                        compressedLength += compressed.Length;
                        compressedCount++;
                        _compressedHashes.Add(GetContentHash(compressed));
                    }
                    else
                    {
                        compressedLength += msg.Text.Length;
                        _compressedHashes.Add(msgHash);
                    }
                }
            }

            compressionAction = $"light(semantic/tiered {compressedCount}c/{skippedCount}s/{context.Messages.Count}t)";
            context.CompactionPressure = Math.Max(0, context.CompactionPressure - 1);

            if (compressedCount > 0)
            {
                var ratio = originalLength > 0 ? (double)compressedLength / originalLength : 1.0;
                context.Set("CompactionSummary", $"semantic/tiered compression ({ratio:P0}, {skippedCount} incremental skips)");
                _logger.LogInformation("CompactionStep: light compression ({Ratio:P0}, {Skipped} skipped)", ratio, skippedCount);
            }

            // Prune stale hashes when set grows too large
            if (_compressedHashes.Count > 500) _compressedHashes.Clear();
        }
        else
        {
            compressionAction = "none(below light threshold)";
        }

        // ── Phase 2: Fidelity scoring (#2) ──
        if (cfg.Fidelity.Enabled)
        {
            var fidelity = ComputeFidelity(beforeSnapshot, context.Messages, compressionAction);
            context.Fidelity = fidelity;

            if (fidelity.OverallFidelity >= cfg.Fidelity.MinReportable)
            {
                var fiMsg = new ChatMessage(ChatRole.System,
                    $"## Compression Fidelity\n- **Level**: {fidelity.CompressionLevel}\n- **Overall**: {fidelity.OverallFidelity:P0}\n- " +
                    string.Join("\n- ", fidelity.PerRoleFidelity.Select(kv => $"**{kv.Key}**: {kv.Value:P0}")) +
                    $"\n- **Action**: {compressionAction}");
                context.Messages.Add(fiMsg);
                context.Set("CompressionFidelity", fidelity);
                _logger.LogInformation("CompactionStep: fidelity={Fidelity:P0} level={Level}", fidelity.OverallFidelity, fidelity.CompressionLevel);
            }
        }

        // ── Phase 3: Stream progressive compression flag (#5) ──
        _streamTokenCheckCounter++;
        if (_streamTokenCheckCounter >= 10)
        {
            _streamTokenCheckCounter = 0;
            var nextRatio = UsageTracker.ContextRatio();
            if (nextRatio > cfg.Thresholds.Light)
            {
                _logger.LogInformation("CompactionStep: streaming progressive — context still at {Pct:F0}% after compaction, will re-check", nextRatio * 100);
                context.Set("StreamingCompactionDue", true);
            }
        }

        // ── Phase 4: Register lazy restore for injected refs (#7 continued) ──
        if (_offloader != null)
        {
            foreach (var msg in context.Messages)
            {
                if (msg.Text == null) continue;
                int idx = 0;
                while ((idx = msg.Text.IndexOf("[refs/", idx, StringComparison.Ordinal)) >= 0)
                {
                    var end = msg.Text.IndexOf(']', idx);
                    if (end < 0) break;
                    var refId = msg.Text[(idx + 6)..end];
                    var captured = refId;
                    context.RegisterRefRestore(refId, "compacted-message",
                        async () => await _offloader.ReadRefAsync(captured).ConfigureAwait(false));
                    idx = end + 1;
                }
            }
        }

        // ── Phase 5: GC hint (#4) ──
        if (cfg.Gc.TtlHours > 0)
        {
            context.Set("RefsGcHint", $"TTL={cfg.Gc.TtlHours}h, maxFiles={cfg.Gc.MaxFiles}");
        }

        context.Set("CompressionAction", compressionAction);
        return context;
    }

    // ── Phase A: Tool trace offload (unchanged from previous) ──
    private async Task OffloadToolTracesAsync(MessageContext context)
    {
        if (_offloader == null) return;

        var traceId = context.TraceId ?? Guid.NewGuid().ToString("N")[..12];

        if (context.ToolCalls.Count > 0)
        {
            var summary = await _offloader.OffloadToolCallsAsync(context.ToolCalls, traceId);
            context.Set("OffloadSummary", summary);
            context.Set("OffloadEntries", summary.Entries.ToList());

            if (_mermaid != null)
            {
                foreach (var entry in summary.Entries)
                {
                    var isSuccess = entry.ContextResult != "Error"
                        && !entry.ContextResult.Contains("\"success\": false", StringComparison.OrdinalIgnoreCase);
                    _mermaid.RecordToolCall(entry.ToolName, entry.RefId, isSuccess);
                }
            }

            foreach (var entry in summary.Entries.Where(e => e.RefId != null))
            {
                var capturedRefId = entry.RefId!;
                context.RegisterRefRestore(capturedRefId, entry.ToolName,
                    async () => await _offloader.ReadRefAsync(capturedRefId).ConfigureAwait(false));
            }

            _logger.LogInformation(
                "CompactionStep: offloaded {Offloaded}/{Total} tool traces ({SavedBytes:N0} bytes saved)",
                summary.OffloadedCount, summary.TotalToolCalls, summary.SavedBytes);
        }

        var indexFile = $"{traceId}-session-index.md";
        var refsDir = Path.Combine(AppContext.BaseDirectory, ".livingtree", "refs");
        Directory.CreateDirectory(refsDir);
        var indexPath = Path.Combine(refsDir, indexFile);

        var indexMd = new StringBuilder();
        indexMd.AppendLine($"# Session Index — {traceId}");
        indexMd.AppendLine();
        indexMd.AppendLine($"- **Messages**: {context.Messages.Count}");
        indexMd.AppendLine($"- **Tool calls**: {context.ToolCalls.Count}");
        indexMd.AppendLine($"- **TraceId**: `{traceId}`");
        indexMd.AppendLine();
        indexMd.AppendLine("## Drill-down Path");
        indexMd.AppendLine("1. **Mermaid diagram** → injected in context above");
        indexMd.AppendLine("2. **Refs index** → `refs/{traceId}-index.md`");
        indexMd.AppendLine("3. **Full traces** → `refs/{traceId}-*.md` files");
        indexMd.AppendLine();
        indexMd.AppendLine("## Lazy Restore");
        indexMd.AppendLine("Refs content will be auto-loaded on first reference.");

        await File.WriteAllTextAsync(indexPath, indexMd.ToString(), Encoding.UTF8);
        context.Set("RefsIndex", indexFile);
    }

    private static string CompressWithRatio(string text, double ratio)
    {
        if (ratio >= 1.0) return text;
        var contentType = ContentCompressor.Detect(text);
        var compressed = ContentCompressor.Compress(text, contentType);
        if (ratio >= 0.7 || compressed.Length >= text.Length * ratio)
            return compressed;
        var targetLen = (int)(text.Length * ratio);
        if (targetLen < 50) targetLen = 50;
        return text.Length <= targetLen ? text : text[..targetLen] + "\n...(压缩)";
    }

    /// <summary>KVEraser-inspired: content hash for incremental compaction skip detection.</summary>
    private static int GetContentHash(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var hash = 17;
        // Hash first 256 chars (enough to identify unique messages, fast enough to not dominate)
        var len = Math.Min(text.Length, 256);
        for (int i = 0; i < len; i++)
            hash = hash * 31 + text[i];
        return hash;
    }

    /// <summary>Reset incremental compaction state (e.g., on new session).</summary>
    public void ResetIncrementalState() => _compressedHashes.Clear();
}
