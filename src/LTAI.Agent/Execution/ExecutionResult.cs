// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  ExecutionResult — the output of IExecutionEngine.ExecuteAsync
//
//  Phase 2a: contains the final response, per-step outputs, and
//  execution trace information.
// ═══════════════════════════════════════════════════════════════

using Microsoft.Extensions.AI;

namespace LTAI.Agent.Execution;

/// <summary>
/// The result of executing an <see cref="ExecutionPlan"/>.
/// Contains the final response as well as per-step intermediate results.
/// </summary>
public sealed class ExecutionResult
{
    /// <summary>Final response messages (typically a single assistant message).</summary>
    public IReadOnlyList<ChatMessage> Messages { get; init; } = [];

    /// <summary>The full text of the final response.</summary>
    public string Text { get; init; } = "";

    /// <summary>Per-step outputs (keyed by step name or index).</summary>
    public IReadOnlyDictionary<string, string> StepOutputs { get; init; }
        = new Dictionary<string, string>();

    /// <summary>Execution spans collected during execution.</summary>
    public IReadOnlyList<ExecutionSpan> Spans { get; init; } = [];

    /// <summary>True if the execution completed successfully.</summary>
    public bool Success { get; init; } = true;

    /// <summary>Error message if execution failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Total execution duration.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Did the greeting fast-path handle the request?</summary>
    public bool WasGreetingFastPath { get; init; }
}
