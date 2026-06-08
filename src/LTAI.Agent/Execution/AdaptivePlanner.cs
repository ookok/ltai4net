// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  AdaptivePlanner — progressive constraint relaxation
//
//  Inspiration: AdaPlanBench (arXiv 2606.05622)
//
//  Wraps IExecutionEngine.PlanAsync to produce plans with
//  constraints. When execution fails, the planner loosens
//  constraints and produces a simpler plan.
//
//  Constraint relaxation progression:
//    Strict  → top-1 specialist, greeting-only, NO tool calls
//    Moderate → top-3 specialists, tool calls allowed
//    Relaxed → all specialists, tool calls, concurrent fan-out
//    Fallback → direct LLM response, no routing
// ═══════════════════════════════════════════════════════════════

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Execution;

/// <summary>
/// Adaptive planner that wraps IExecutionEngine and adds constraint-based
/// plan generation with progressive relaxation on failure.
///
/// Usage:
///   var adapter = new AdaptivePlanner(innerEngine);
///   var plan = await adapter.PlanAsync(query);
///   var result = await adapter.ExecuteAsync(plan);
///   if (!result.Success) {
///       var relaxedPlan = adapter.TryLoosen(plan);
///       result = await adapter.ExecuteAsync(relaxedPlan);
///   }
/// </summary>
public sealed class AdaptivePlanner
{
    private readonly IExecutionEngine _inner;
    private readonly ILogger<AdaptivePlanner> _logger;
    private readonly Random _rng = new();

    /// <summary>Maximum retry attempts across all constraint levels.</summary>
    public int MaxRetries { get; set; } = 4;

    public AdaptivePlanner(
        IExecutionEngine inner,
        ILogger<AdaptivePlanner>? logger = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _logger = logger ?? NullLogger<AdaptivePlanner>.Instance;
    }

    /// <summary>
    /// Plan with adaptive constraints. Produces an ExecutionPlan with
    /// constraints at the Strict level and progressively relaxed fallbacks.
    /// </summary>
    public async Task<ExecutionPlan> PlanAsync(string query, CancellationToken ct = default)
    {
        // Get base plan from inner engine
        var basePlan = await _inner.PlanAsync(query, ct).ConfigureAwait(false);

        // Attach constraints based on query characteristics
        var constraints = BuildConstraints(basePlan, query);

        // Attach fallback steps at Relaxed and Fallback levels
        var fallbackSteps = BuildFallbackSteps(basePlan, query);

        // Merge constraints and fallback steps into the plan
        return basePlan with
        {
            Constraints = constraints,
            Steps = fallbackSteps.Count > basePlan.Steps.Count
                ? fallbackSteps
                : basePlan.Steps,
            CurrentLevel = ConstraintLevel.Strict,
        };
    }

    /// <summary>
    /// Execute with automatic constraint relaxation on failure.
    /// Tries each constraint level in order until success or all levels exhausted.
    /// </summary>
    public async Task<ExecutionResult> ExecuteWithFallbackAsync(
        ExecutionPlan plan, CancellationToken ct = default)
    {
        var currentPlan = plan;
        var attempt = 0;

        while (currentPlan != null && attempt < MaxRetries)
        {
            attempt++;
            _logger.LogInformation(
                "AdaptivePlanner: attempt {Attempt}/{MaxRetries} at level {Level}",
                attempt, MaxRetries, currentPlan.CurrentLevel);

            var result = await _inner.ExecuteAsync(currentPlan, ct).ConfigureAwait(false);

            if (result.Success)
            {
                _logger.LogInformation(
                    "AdaptivePlanner: success on attempt {Attempt} at level {Level}",
                    attempt, currentPlan.CurrentLevel);
                return result;
            }

            _logger.LogWarning(
                "AdaptivePlanner: attempt {Attempt} failed at level {Level}: {Error}",
                attempt, currentPlan.CurrentLevel, result.ErrorMessage);

            // Try to loosen constraints
            currentPlan = currentPlan.TryLoosenConstraints();

            if (currentPlan != null)
            {
                // Progressively simplify the plan as constraints loosen
                currentPlan = SimplifyPlanForLevel(currentPlan);

                _logger.LogInformation(
                    "AdaptivePlanner: loosened to level {Level} with {StepCount} step(s)",
                    currentPlan.CurrentLevel, currentPlan.Steps.Count);
            }
        }

        // All attempts failed — return last result
        _logger.LogError("AdaptivePlanner: all {MaxRetries} attempts exhausted", MaxRetries);
        return new ExecutionResult
        {
            Text = "Unable to process request after multiple attempts.",
            Success = false,
            ErrorMessage = $"AdaptivePlanner: exhausted {MaxRetries} attempts",
        };
    }

    /// <summary>
    /// Build constraints for a plan based on query characteristics.
    /// </summary>
    private static IReadOnlyList<PlanConstraint> BuildConstraints(
        ExecutionPlan basePlan, string query)
    {
        var constraints = new List<PlanConstraint>();
        var lowerQuery = query.ToLowerInvariant();

        // Strict level: greedy constraints
        if (basePlan.Confidence > 0.8f)
        {
            constraints.Add(new PlanConstraint(
                "TopConfidence", "Only route when confidence > 0.8", ConstraintLevel.Strict));
        }

        if (query.Length <= 50)
        {
            constraints.Add(new PlanConstraint(
                "GreetingFastPath", "Try greeting fast-path first", ConstraintLevel.Strict));
        }

        // Moderate level: moderate constraints
        constraints.Add(new PlanConstraint(
            "TopK", "Route to top-3 specialists only", ConstraintLevel.Moderate));

        constraints.Add(new PlanConstraint(
            "SequentialOnly", "Use sequential steps only, no concurrency", ConstraintLevel.Moderate));

        // Relaxed level: few constraints
        constraints.Add(new PlanConstraint(
            "AllowConcurrent", "Allow concurrent execution", ConstraintLevel.Relaxed));

        constraints.Add(new PlanConstraint(
            "AllowTools", "Allow tool execution", ConstraintLevel.Relaxed));

        // Fallback level: no constraints (just LLM)

        return constraints.AsReadOnly();
    }

    /// <summary>
    /// Build fallback plan steps for each constraint level.
    /// As constraints loosen, the plan becomes more permissive.
    /// </summary>
    private static IReadOnlyList<WorkflowStep> BuildFallbackSteps(
        ExecutionPlan basePlan, string query)
    {
        // Strict: handoff to top-1 (or greeting)
        if (basePlan.Steps.Count > 0)
            return basePlan.Steps;

        // No steps in base plan — provide fallback
        return new List<WorkflowStep>
        {
            new HandoffStep("LTAI-Chat") { Name = "handoff:LTAI-Chat" },
        };
    }

    /// <summary>
    /// Simplify the plan further as constraints loosen.
    /// At Fallback level, just use direct handoff.
    /// </summary>
    private static ExecutionPlan SimplifyPlanForLevel(ExecutionPlan plan)
    {
        return plan.CurrentLevel switch
        {
            ConstraintLevel.Relaxed => plan with
            {
                Steps = plan.Steps.Count > 3
                    ? plan.Steps.Take(3).ToList().AsReadOnly()
                    : plan.Steps,
                Confidence = Math.Max(plan.Confidence, 0.5f),
            },

            ConstraintLevel.Fallback => plan with
            {
                Steps = new List<WorkflowStep>
                {
                    new HandoffStep("LTAI-Chat") { Name = "handoff:fallback" },
                }.AsReadOnly(),
                Confidence = 1.0f,
            },

            _ => plan,
        };
    }
}
