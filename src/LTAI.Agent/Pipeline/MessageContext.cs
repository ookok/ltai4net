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

    /// <summary>Did the pipeline encounter a safety block?</summary>
    public bool SafetyBlocked { get; set; }

    /// <summary>Safety reason if blocked.</summary>
    public string? SafetyReason { get; set; }

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
