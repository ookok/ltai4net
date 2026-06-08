// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  ExecutionPlan — the output of IExecutionEngine.PlanAsync
//
//  Phase 2a: a plan describes what the engine will execute.
//  It contains a list of top-level steps, each of which may be
//  sequential, concurrent, conditional, or a retry wrapper.
//
//  Adaptive Plan Constraints (AdaPlanBench 2606.05622):
//  Plans can carry a set of constraints that are progressively
//  relaxed. As the execution engine attempts steps and encounters
//  failures, constraints are loosened until a feasible path is found.
// ═══════════════════════════════════════════════════════════════

namespace LTAI.Agent.Execution;

/// <summary>
/// The difficulty level of a plan constraint.
/// When a constraint fails, the engine loosens to the next level.
/// </summary>
public enum ConstraintLevel
{
    /// <summary>Strictest: greeting fast-path, top confidence routing only.</summary>
    Strict,

    /// <summary>Moderate: allow routing to top-3 specialists.</summary>
    Moderate,

    /// <summary>Relaxed: allow all specialists, enable tool calls.</summary>
    Relaxed,

    /// <summary>Fallback: use direct LLM response, no routing.</summary>
    Fallback,
}

/// <summary>
/// A named constraint on plan execution. Each constraint has a
/// description (for telemetry) and a level at which it applies.
/// When a plan fails at its current level, constraints at that
/// level are removed and the next level is tried.
/// </summary>
/// <param name="Name">Constraint name (e.g. "TopConfidence", "GreetingOnly").</param>
/// <param name="Description">What this constraint means.</param>
/// <param name="Level">The difficulty level at which this constraint is active.</param>
public sealed record PlanConstraint(
    string Name,
    string Description,
    ConstraintLevel Level);

/// <summary>
/// An execution plan produced by <see cref="IExecutionEngine.PlanAsync"/>.
/// Contains the ordered list of steps to execute and optional metadata.
/// </summary>
/// <param name="Steps">Top-level workflow steps.</param>
/// <param name="Query">The original user query.</param>
/// <param name="TraceId">Optional trace identifier for correlation.</param>
/// <param name="Branch">The routing branch that was taken (from DecisionTreeRouter).</param>
/// <param name="Confidence">Confidence score of the routing decision (0-1).</param>
/// <param name="Constraints">
/// Optional list of plan constraints. When execution fails at the current
/// <see cref="CurrentLevel"/>, constraints at that level are removed and
/// the plan is retried at the next level. Default: empty (no constraints).
/// </param>
/// <param name="CurrentLevel">
/// The current constraint level being applied. Starts at <see cref="ConstraintLevel.Strict"/>.
/// </param>
public sealed record ExecutionPlan(
    IReadOnlyList<WorkflowStep> Steps,
    string Query,
    string? TraceId = null,
    string? Branch = null,
    float Confidence = 1.0f,
    IReadOnlyList<PlanConstraint>? Constraints = null,
    ConstraintLevel CurrentLevel = ConstraintLevel.Strict)
{
    /// <summary>
    /// Get constraints that are active at the current level.
    /// </summary>
    public IReadOnlyList<PlanConstraint> ActiveConstraints =>
        Constraints?.Where(c => c.Level == CurrentLevel).ToList().AsReadOnly()
        ?? [];

    /// <summary>
    /// Check if a constraint with the given name exists at any level.
    /// </summary>
    public bool HasConstraint(string name) =>
        Constraints?.Any(c => c.Name == name) ?? false;

    /// <summary>
    /// Try to loosen constraints to the next level.
    /// Returns the new plan with the constraint level increased, or
    /// null if already at the maximum level (Fallback).
    /// </summary>
    public ExecutionPlan? TryLoosenConstraints()
    {
        if (CurrentLevel >= ConstraintLevel.Fallback)
            return null;

        var nextLevel = CurrentLevel switch
        {
            ConstraintLevel.Strict => ConstraintLevel.Moderate,
            ConstraintLevel.Moderate => ConstraintLevel.Relaxed,
            ConstraintLevel.Relaxed => ConstraintLevel.Fallback,
            _ => ConstraintLevel.Fallback,
        };

        return this with { CurrentLevel = nextLevel };
    }
}
