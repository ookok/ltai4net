// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  WorkflowStep — step definition for IExecutionEngine plans
//
//  Phase 2a: a single step in an ExecutionPlan. Can be:
//    - HandoffStep:  router delegates to one specialist
//    - SequentialStep: sub-steps execute in order
//    - ConcurrentStep: sub-steps execute in parallel
//    - ConditionalStep: branch based on a predicate
//    - RetryStep: wrap another step with retry logic
// ═══════════════════════════════════════════════════════════════

namespace LTAI.Agent.Execution;

/// <summary>
/// A single step in an execution plan. Steps form a tree:
/// composite steps (Sequential, Concurrent, Conditional) contain
/// sub-steps; leaf steps (Handoff) invoke a single agent.
/// </summary>
public abstract record WorkflowStep
{
    /// <summary>Display name for this step (used in telemetry).</summary>
    public string Name { get; init; } = "";

    /// <summary>Step type discriminator for serialization and routing.</summary>
    public abstract string StepType { get; }
}

/// <summary>
/// Handoff to a single specialist agent. The router (LTAI-Chat or
/// LTAI-Chat-Pro) delegates the task via MAF function-call handoff.
/// </summary>
/// <param name="SpecialistName">Name of the target agent (e.g. "LTAI-Code").</param>
public sealed record HandoffStep(string SpecialistName) : WorkflowStep
{
    public override string StepType => "handoff";
}

/// <summary>
/// Sequential pipeline: sub-steps execute in order. Each step receives
/// the previous step's output as its input.
/// </summary>
/// <param name="Steps">Ordered sub-steps.</param>
public sealed record SequentialStep(IReadOnlyList<WorkflowStep> Steps) : WorkflowStep
{
    public override string StepType => "sequential";

    public int Count => Steps.Count;
}

/// <summary>
/// Concurrent fan-out: all sub-steps execute in parallel with the
/// same input. Results are aggregated.
/// </summary>
/// <param name="Steps">Steps to execute concurrently.</param>
public sealed record ConcurrentStep(IReadOnlyList<WorkflowStep> Steps) : WorkflowStep
{
    public override string StepType => "concurrent";

    public int Count => Steps.Count;
}

/// <summary>
/// Conditional branch: choose one of two sub-steps based on a predicate.
/// </summary>
/// <param name="Condition">Description of the condition (for telemetry).</param>
/// <param name="TrueStep">Step to execute if condition is true.</param>
/// <param name="FalseStep">Step to execute if condition is false.</param>
public sealed record ConditionalStep(
    string Condition,
    WorkflowStep TrueStep,
    WorkflowStep FalseStep) : WorkflowStep
{
    public override string StepType => "conditional";
}

/// <summary>
/// Retry wrapper: retries the inner step on failure.
/// </summary>
/// <param name="Inner">The step to retry.</param>
/// <param name="MaxRetries">Maximum retry attempts.</param>
/// <param name="BackoffMs">Delay between retries (milliseconds).</param>
public sealed record RetryStep(
    WorkflowStep Inner,
    int MaxRetries = 3,
    int BackoffMs = 1000) : WorkflowStep
{
    public override string StepType => "retry";
}

/// <summary>
/// A no-op or debugging step (e.g. log, delay, or custom action).
/// </summary>
/// <param name="Action">Description of the action.</param>
public sealed record NoopStep(string Action) : WorkflowStep
{
    public override string StepType => "noop";
}
