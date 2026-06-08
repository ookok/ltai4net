// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  ExecutionSpan — OTel-compatible step execution trace
//
//  Phase 2a: each workflow step produces one or more ExecutionSpans.
//  Collected by IExecutionEngine.OnSpan, exported to DevUI via
//  DevUISpanCollector and to OTel when configured.
// ═══════════════════════════════════════════════════════════════

namespace LTAI.Agent.Execution;

/// <summary>
/// A span of execution for a single workflow step, compatible with
/// OTel tracing semantics. Contains timing, status, and metadata.
/// </summary>
/// <param name="StepName">Name of the workflow step.</param>
/// <param name="StepType">Type: handoff, sequential, concurrent, conditional, retry.</param>
/// <param name="AgentName">Agent name, if applicable.</param>
/// <param name="StartTimeUtc">When the step started.</param>
/// <param name="EndTimeUtc">When the step ended.</param>
/// <param name="Duration">Step duration.</param>
/// <param name="Status">Status: success, failure, skipped.</param>
/// <param name="Error">Error message, if failed.</param>
/// <param name="InputTokens">Approximate input tokens.</param>
/// <param name="OutputTokens">Approximate output tokens.</param>
/// <param name="TraceId">Correlation trace ID.</param>
/// <param name="SpanId">Span ID.</param>
/// <param name="ParentSpanId">Parent span ID for nesting.</param>
/// <param name="Metadata">Additional key-value metadata.</param>
public sealed record ExecutionSpan(
    string StepName,
    string StepType,
    string? AgentName = null,
    DateTime StartTimeUtc = default,
    DateTime EndTimeUtc = default,
    TimeSpan Duration = default,
    SpanStatus Status = SpanStatus.Success,
    string? Error = null,
    int InputTokens = 0,
    int OutputTokens = 0,
    string? TraceId = null,
    string? SpanId = null,
    string? ParentSpanId = null,
    IReadOnlyDictionary<string, string>? Metadata = null)
{
    /// <summary>Create a span that starts now.</summary>
    public static ExecutionSpan Start(
        string stepName, string stepType, string? agentName = null,
        string? traceId = null, string? parentSpanId = null)
        => new(stepName, stepType, agentName,
               StartTimeUtc: DateTime.UtcNow, TraceId: traceId, ParentSpanId: parentSpanId);

    /// <summary>Complete the span with success status.</summary>
    public ExecutionSpan Complete(int inputTokens = 0, int outputTokens = 0)
        => this with
        {
            EndTimeUtc = DateTime.UtcNow,
            Duration = DateTime.UtcNow - StartTimeUtc,
            Status = SpanStatus.Success,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
        };

    /// <summary>Complete the span with failure status.</summary>
    public ExecutionSpan Fail(string error, int inputTokens = 0, int outputTokens = 0)
        => this with
        {
            EndTimeUtc = DateTime.UtcNow,
            Duration = DateTime.UtcNow - StartTimeUtc,
            Status = SpanStatus.Failure,
            Error = error,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
        };
}

/// <summary>Execution span status.</summary>
public enum SpanStatus
{
    Success,
    Failure,
    Skipped,
}
