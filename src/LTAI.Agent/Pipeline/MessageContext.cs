// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  MessageContext — request/response context for pipeline steps
//
//  Phase 3a: flows through IPipelineStep.ProcessAsync.
//  Contains the full request/response state + tool calls + spans.
// ═══════════════════════════════════════════════════════════════

using System.Collections.Concurrent;
using LTAI.Agent.Execution;
using Microsoft.Extensions.AI;

namespace LTAI.Agent.Pipeline;

/// <summary>Callback to lazily restore content from a refs reference.</summary>
public sealed record RefsRestoreEntry(
    string RefId,
    string FilePath,
    Func<Task<string?>> RestoreAsync);

/// <summary>Compression fidelity score per message role.</summary>
public sealed record CompressionFidelity
{
    public int TotalMessages { get; init; }
    public int CompactedMessages { get; init; }
    public double OverallFidelity { get; init; }
    public Dictionary<string, double> PerRoleFidelity { get; init; } = [];
    public string CompressionLevel { get; init; } = "";
    public string? ActionTaken { get; init; }
}

/// <summary>
/// Mutable context that flows through the pipeline. Each step reads
/// and/or writes the context. Thread-safe for concurrent step execution.
/// </summary>
public sealed class MessageContext
{
    private readonly ConcurrentDictionary<string, object?> _properties;

    /// <summary>Original user query / task.</summary>
    public string Request { get; set; }

    /// <summary>Accumulated response messages.</summary>
    public List<ChatMessage> Messages { get; } = [];

    /// <summary>Tool calls accumulated during processing.</summary>
    public List<(string Name, string Arguments, string Result)> ToolCalls { get; } = [];

    /// <summary>Execution spans collected.</summary>
    public List<ExecutionSpan> Spans { get; } = [];

    /// <summary>Optional trace ID for correlation.</summary>
    public string? TraceId { get; set; }

    /// <summary>Execution engine (set by RouterStep).</summary>
    public IExecutionEngine? ExecutionEngine { get; set; }

    /// <summary>Execution plan (set by RouterStep).</summary>
    public ExecutionPlan? Plan { get; set; }

    /// <summary>Execution result (set by ToolExecutionStep).</summary>
    public ExecutionResult? Result { get; set; }

    /// <summary>Cancellation support.</summary>
    public CancellationToken CancellationToken { get; set; }

    /// <summary>Lazy restoration entries for refs content.</summary>
    public ConcurrentDictionary<string, RefsRestoreEntry> RefsRestoreMap { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Lock object for thread-safe access to Messages in parallel pipeline groups.</summary>
    public readonly object MessagesLock = new();

    /// <summary>Compression fidelity for the current session.</summary>
    public CompressionFidelity? Fidelity { get; set; }

    /// <summary>User feedback: how many times refs were expanded for each file.</summary>
    public ConcurrentDictionary<string, int> RefsExpandedCount { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Accumulated context pressure (number of times compaction was triggered).
    /// Used to adaptively reduce aggressiveness when user frequently expands refs.
    /// </summary>
    public int CompactionPressure { get; set; }

    /// <summary>
    /// Adaptive aggressiveness multiplier (0.0 = minimal, 1.0 = maximal).
    /// Decreases when user expands refs; increases when compaction triggers.
    /// </summary>
    public double AggressivenessMultiplier { get; set; } = 1.0;

    /// <summary>Record a refs expansion by the user (feedback loop).</summary>
    public void RecordRefExpansion(string refId)
    {
        RefsExpandedCount.AddOrUpdate(refId, 1, (_, c) => c + 1);
        // Reduce aggressiveness proportionally to expansions
        var totalExpansions = RefsExpandedCount.Values.Sum();
        AggressivenessMultiplier = Math.Max(0.3, 1.0 - totalExpansions * 0.05);
    }

    /// <summary>Get a compression adjustment hint based on feedback.</summary>
    public string GetCompressionHint()
    {
        if (AggressivenessMultiplier < 0.5)
            return $"user frequently expanded refs ({RefsExpandedCount.Count} files), reduce compression aggressiveness to {AggressivenessMultiplier:P0}";
        return $"compression aggressiveness at {AggressivenessMultiplier:P0}";
    }

    /// <summary>Register a refs entry for lazy restoration.</summary>
    public void RegisterRefRestore(string refId, string filePath, Func<Task<string?>> restoreAsync)
    {
        RefsRestoreMap.TryAdd(refId, new RefsRestoreEntry(refId, filePath, restoreAsync));
    }

    /// <summary>Try to restore content from a refs reference if registered.</summary>
    public async Task<string?> RestoreRefAsync(string refId)
    {
        if (RefsRestoreMap.TryGetValue(refId, out var entry))
            return await entry.RestoreAsync().ConfigureAwait(false);
        return null;
    }

    /// <summary>Did the pipeline encounter a safety block?</summary>
    public bool SafetyBlocked { get; set; }

    /// <summary>Safety reason if blocked.</summary>
    public string? SafetyReason { get; set; }

    /// <summary>Grammar check failed (GrammarCheckStep).</summary>
    public bool GrammarCheckBlocked { get; set; }

    /// <summary>Anti-pattern check failed (AntiPatternCheckStep).</summary>
    public bool AntiPatternBlocked { get; set; }

    /// <summary>Quality gate not passed (QualityGateStep).</summary>
    public bool QualityGateBlocked { get; set; }

    /// <summary>Definition of Done check failed (DoDCheckStep).</summary>
    public bool DoDBlocked { get; set; }

    /// <summary>Last pipeline step error message.</summary>
    public string? PipelineError { get; set; }

    public MessageContext(string request, CancellationToken ct = default)
    {
        Request = request;
        CancellationToken = ct;
        _properties = new ConcurrentDictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Set a property value.</summary>
    public void Set(string key, object? value) => _properties[key] = value;

    /// <summary>Try to get a property value.</summary>
    public bool TryGet<T>(string key, out T? value)
    {
        if (_properties.TryGetValue(key, out var obj) && obj is T t)
        {
            value = t; return true;
        }
        value = default; return false;
    }

    /// <summary>Snapshot all properties (for logging / DevUI).</summary>
    public IReadOnlyDictionary<string, object?> Snapshot()
        => new Dictionary<string, object?>(_properties, StringComparer.OrdinalIgnoreCase);
}
