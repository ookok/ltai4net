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
/// Runtime-adaptive thresholds for offloading decisions.
/// </summary>
public sealed record AdaptiveThresholds
{
    public int MaxInlineBytes { get; init; } = 1024;
    public int MaxInlineLines { get; init; } = 40;
    public int MaxInlineChars { get; init; } = 2048;
}

/// <summary>
/// Offloads heavy tool execution traces to <c>.livingtree/refs/</c> files,
/// replacing them with lightweight <c>[refs/{filename}#{hash}]</c> references.
/// Provides lossless drill-down: Mermaid state diagram → refs index → full text.
/// </summary>
public sealed partial class ContextOffloader
{
    private readonly string _refsDir;
    private readonly ILogger _logger;
    private readonly DeltaStore? _deltaStore;
    private readonly PredictiveOffloadTracker? _predictiveTracker;
    private readonly Context.SemanticCompressor? _semanticCompressor;
    private static readonly ConcurrentDictionary<string, int> s_toolHistory = new(StringComparer.OrdinalIgnoreCase);

    private AdaptiveThresholds _currentThresholds = new();

    public const int MaxInlineBytes = 1024;
    public const int MaxInlineLines = 40;
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

    /// <summary>
    /// Compute adaptive thresholds from context signals.
    /// Higher aggressivenessMultiplier → lower thresholds (more aggressive offload).
    /// </summary>
    public AdaptiveThresholds ComputeDynamicThresholds(
        double aggressivenessMultiplier,
        int compactionPressure,
        int messageCount,
        int estimatedTokens)
    {
        var scale = aggressivenessMultiplier;
        if (compactionPressure > 5) scale *= 1.4;
        else if (compactionPressure > 2) scale *= 1.15;
        if (messageCount > 50) scale *= 1.2;
        if (messageCount > 100) scale *= 1.4;
        if (estimatedTokens > 100_000) scale *= 1.25;
        if (estimatedTokens > 200_000) scale *= 1.5;
        scale = Math.Clamp(scale, 0.4, 2.5);

        _currentThresholds = new AdaptiveThresholds
        {
            MaxInlineBytes = (int)(MaxInlineBytes / scale),
            MaxInlineLines = (int)(MaxInlineLines / scale),
            MaxInlineChars = (int)(MaxInlineChars / scale),
        };
        return _currentThresholds;
    }

    /// <summary>Reset thresholds to static defaults.</summary>
    public void ResetThresholds() => _currentThresholds = new();

    /// <summary>Instance-level ShouldOffload using current adaptive thresholds.</summary>
    public bool ShouldOffloadAdaptive(string content) =>
        content.Length > _currentThresholds.MaxInlineChars ||
        Encoding.UTF8.GetByteCount(content) > _currentThresholds.MaxInlineBytes ||
        CountLines(content) > _currentThresholds.MaxInlineLines;

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

    public static bool CrossSessionEnabled { get; set; } = true;

    private static readonly ConcurrentDictionary<string, int> s_crossSessionCounters = new(StringComparer.OrdinalIgnoreCase);

    public static void ResetCrossSessionCounters() => s_crossSessionCounters.Clear();

    public string RefsDirectory => _refsDir;

    private static double EstimateTokens(string text) =>
        text.Length * 0.35 + 5;
}
