// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  IExecutionEngine — orchestration engine abstraction
//
//  Phase 2a of refactor-plan-v2.md: Plan → Execute → Trace cycle.
//
//  Extracted from AgentWorkflows.cs (handoff/sequential/concurrent
//  routing) and DecisionTreeRouter.cs (threshold-based vector routing).
//
//  Three-phase lifecycle:
//    1. PlanAsync — analyze query → produce an ExecutionPlan (steps + branches)
//    2. ExecuteAsync — execute the plan and return results
//    3. OnSpan — telemetry: each step emits an ExecutionSpan
// ═══════════════════════════════════════════════════════════════

namespace LTAI.Agent.Execution;

/// <summary>
/// Execution engine that plans and executes multi-step agent workflows.
/// Supports greeting fast-path, embedding-based vector routing, handoff,
/// sequential, and concurrent orchestration patterns.
/// </summary>
public interface IExecutionEngine
{
    /// <summary>
    /// Analyze a user query and produce an execution plan.
    /// This is the "plan" phase — no LLM calls happen here unless the
    /// configuration requires steer-model re-ranking.
    /// </summary>
    /// <param name="query">The user's query or task description.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An execution plan describing the steps to execute.</returns>
    Task<ExecutionPlan> PlanAsync(string query, CancellationToken ct = default);

    /// <summary>
    /// Execute a previously planned workflow and produce results.
    /// This is the "execute" phase — the engine walks the plan steps
    /// and orchestrates agent invocations.
    /// </summary>
    /// <param name="plan">The plan produced by <see cref="PlanAsync"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The execution result with outputs and trace information.</returns>
    Task<ExecutionResult> ExecuteAsync(ExecutionPlan plan, CancellationToken ct = default);

    /// <summary>
    /// Fired for each step completion during execution. Used for
    /// OTel-compatible execution tracing, DevUI rendering, and
    /// circuit-breaker monitoring.
    /// </summary>
    event Action<ExecutionSpan>? OnSpan;
}
