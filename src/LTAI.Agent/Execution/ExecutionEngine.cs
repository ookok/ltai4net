// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  ExecutionEngine — Plan → Execute → Trace implementation
//
//  Phase 2a: orchestrates multi-agent workflows by wrapping the
//  existing AgentWorkflows (handoff/sequential/concurrent) and
//  DecisionTreeRouter (embedding vector routing) behind the
//  IExecutionEngine interface.
//
//  Three-phase lifecycle:
//    1. PlanAsync — determines greeting fast-path or vector routing
//    2. ExecuteAsync — runs the plan via AgentWorkflows
//    3. OnSpan — telemetry event per step
// ═══════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LTAI.Agent.Workflows;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Execution;

/// <summary>
/// Default IExecutionEngine implementation. Wraps AgentWorkflows
/// (handoff/sequential/concurrent) and DecisionTreeRouter
/// (embedding-based vector routing) into the Plan → Execute cycle.
/// </summary>
public sealed class ExecutionEngine : IExecutionEngine
{
    private readonly AgentWorkflows _agentWorkflows;
    private readonly DecisionTreeRouter? _router;
    private readonly ILogger<ExecutionEngine> _logger;

    /// <summary>Fired for each completed step during execution.</summary>
    public event Action<ExecutionSpan>? OnSpan;

    public ExecutionEngine(
        AgentWorkflows agentWorkflows,
        DecisionTreeRouter? router = null,
        ILogger<ExecutionEngine>? logger = null)
    {
        _agentWorkflows = agentWorkflows;
        _router = router;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ExecutionEngine>.Instance;
    }

    /// <inheritdoc />
    public async Task<ExecutionPlan> PlanAsync(string query, CancellationToken ct = default)
    {
        // Phase 1: Check for greeting fast-path
        // (lightweight check — the actual greeting run is deferred to ExecuteAsync)
        var isLikelyGreeting = query.Trim().Length <= 50 && IsGreetingLike(query);
        if (isLikelyGreeting)
        {
            _logger.LogDebug("PlanAsync: greeting-like query detected, planning fast-path");
            return new ExecutionPlan(
                Steps: [new HandoffStep("__greeting__")],
                Query: query,
                Branch: "GreetingFastPath",
                Confidence: 0.95f);
        }

        // Phase 2: Use DecisionTreeRouter for vector routing
        if (_router != null)
        {
            var allSpecialistNames = _agentWorkflows.GetSpecialistNames();
            var routing = await _router.RouteAsync(query, allSpecialistNames, ct)
                .ConfigureAwait(false);

            if (routing.Candidates.Count == 0)
            {
                _logger.LogWarning("PlanAsync: no candidates from router (branch={Branch})", routing.Branch);
                return new ExecutionPlan(
                    Steps: [],
                    Query: query,
                    Branch: routing.Branch.ToString(),
                    Confidence: routing.TopScore);
            }

            // Build handoff steps for each candidate
            var steps = routing.Candidates
                .Select(name => (WorkflowStep)new HandoffStep(name)
                {
                    Name = $"handoff:{name}"
                })
                .ToList();

            return new ExecutionPlan(
                Steps: steps,
                Query: query,
                Branch: routing.Branch.ToString(),
                Confidence: routing.TopScore);
        }

        // Phase 3: No router — use all specialists
        var allNames = _agentWorkflows.GetSpecialistNames();
        var fallbackSteps = allNames
            .Select(name => (WorkflowStep)new HandoffStep(name)
            {
                Name = $"handoff:{name}"
            })
            .ToList();

        return new ExecutionPlan(
            Steps: fallbackSteps,
            Query: query,
            Branch: "AllSpecialists");
    }

    /// <inheritdoc />
    public async Task<ExecutionResult> ExecuteAsync(ExecutionPlan plan, CancellationToken ct = default)
    {
        var startTime = DateTime.UtcNow;
        var spans = new List<ExecutionSpan>();

        try
        {
            // Handle greeting fast-path
            if (plan.Steps.Count == 1 &&
                plan.Steps[0] is HandoffStep hs &&
                hs.SpecialistName == "__greeting__")
            {
                var span = ExecutionSpan.Start("greeting", "handoff", traceId: plan.TraceId);
                var response = await _agentWorkflows.RunGreetingFastPathAsync(plan.Query, ct)
                    .ConfigureAwait(false);

                var completedSpan = response != null
                    ? span.Complete()
                    : span.Fail("Greeting fast-path returned null");
                spans.Add(completedSpan);
                OnSpan?.Invoke(completedSpan);

                return new ExecutionResult
                {
                    Messages = response != null
                        ? [new Microsoft.Extensions.AI.ChatMessage(
                            Microsoft.Extensions.AI.ChatRole.Assistant, response)]
                        : [],
                    Text = response ?? "",
                    StepOutputs = new Dictionary<string, string>
                    {
                        ["greeting"] = response ?? "(no response)"
                    },
                    Spans = spans,
                    Duration = DateTime.UtcNow - startTime,
                    Success = response != null,
                    WasGreetingFastPath = true,
                };
            }

            // Handle single-step handoff
            if (plan.Steps.Count == 1 && plan.Steps[0] is HandoffStep singleStep)
            {
                var span = ExecutionSpan.Start(singleStep.Name, "handoff",
                    agentName: singleStep.SpecialistName, traceId: plan.TraceId);
                OnSpan?.Invoke(span);

                var response = await _agentWorkflows.RunHandoffAsync(
                    plan.Query, plan.TraceId, ct).ConfigureAwait(false);

                var text = response.Messages?.LastOrDefault()?.Text ?? "";
                var completedSpan = span.Complete();
                spans.Add(completedSpan);
                OnSpan?.Invoke(completedSpan);

                return new ExecutionResult
                {
                    Messages = (IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>?)response.Messages?.ToList().AsReadOnly()
                        ?? Array.Empty<Microsoft.Extensions.AI.ChatMessage>(),
                    Text = text,
                    StepOutputs = new Dictionary<string, string>
                    {
                        [singleStep.Name] = text
                    },
                    Spans = spans,
                    Duration = DateTime.UtcNow - startTime,
                    Success = true,
                };
            }

            // Handle multi-candidate handoff — fan-out with all candidates
            // The existing AgentWorkflows.RunHandoffAsync already handles
            // multi-candidate routing internally via MAF HandoffWorkflowBuilder.
            // If there are multiple HandoffSteps, the router agent delegates
            // to the best one.
            if (plan.Steps.All(s => s is HandoffStep))
            {
                var span = ExecutionSpan.Start("multi-handoff", "handoff",
                    traceId: plan.TraceId);
                OnSpan?.Invoke(span);

                var response = await _agentWorkflows.RunHandoffAsync(
                    plan.Query, plan.TraceId, ct).ConfigureAwait(false);

                var text = response.Messages?.LastOrDefault()?.Text ?? "";
                var completedSpan = span.Complete();
                spans.Add(completedSpan);
                OnSpan?.Invoke(completedSpan);

                return new ExecutionResult
                {
                    Messages = (IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>?)response.Messages?.ToList().AsReadOnly()
                    ?? Array.Empty<Microsoft.Extensions.AI.ChatMessage>(),
                    Text = text,
                    Spans = spans,
                    Duration = DateTime.UtcNow - startTime,
                    Success = true,
                };
            }

            // Handle sequential pipeline
            var sequentialSteps = plan.Steps.OfType<SequentialStep>().FirstOrDefault();
            if (sequentialSteps != null)
            {
                return await ExecuteSequentialAsync(sequentialSteps, plan, startTime, spans, ct)
                    .ConfigureAwait(false);
            }

            // Handle concurrent fan-out
            var concurrentSteps = plan.Steps.OfType<ConcurrentStep>().FirstOrDefault();
            if (concurrentSteps != null)
            {
                return await ExecuteConcurrentAsync(concurrentSteps, plan, startTime, spans, ct)
                    .ConfigureAwait(false);
            }

            // Fallback: treat unknown step types as no-op
            _logger.LogWarning("ExecuteAsync: unhandled step types in plan");
            return new ExecutionResult
            {
                Messages = [],
                Text = "(no handler for plan steps)",
                Spans = spans,
                Duration = DateTime.UtcNow - startTime,
                Success = false,
                ErrorMessage = "No handler registered for plan step types",
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var duration = DateTime.UtcNow - startTime;
            return new ExecutionResult
            {
                Text = "Execution timed out",
                Spans = spans,
                Duration = duration,
                Success = false,
                ErrorMessage = $"Timeout after {duration.TotalSeconds:F1}s",
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ExecuteAsync failed for query: {Query}", plan.Query);
            return new ExecutionResult
            {
                Text = $"Execution failed: {ex.Message}",
                Spans = spans,
                Duration = DateTime.UtcNow - startTime,
                Success = false,
                ErrorMessage = ex.Message,
            };
        }
    }

    private async Task<ExecutionResult> ExecuteSequentialAsync(
        SequentialStep step, ExecutionPlan plan,
        DateTime startTime, List<ExecutionSpan> spans,
        CancellationToken ct)
    {
        var agentNames = step.Steps
            .OfType<HandoffStep>()
            .Select(s => s.SpecialistName)
            .ToArray();

        var span = ExecutionSpan.Start(step.Name, "sequential", traceId: plan.TraceId);
        OnSpan?.Invoke(span);

        var text = await _agentWorkflows.RunSequentialAsync(
            agentNames, plan.Query, plan.TraceId, ct).ConfigureAwait(false);

        var completedSpan = span.Complete();
        spans.Add(completedSpan);
        OnSpan?.Invoke(completedSpan);

        return new ExecutionResult
        {
            Text = text,
            StepOutputs = new Dictionary<string, string> { [step.Name] = text },
            Spans = spans,
            Duration = DateTime.UtcNow - startTime,
            Success = true,
        };
    }

    private async Task<ExecutionResult> ExecuteConcurrentAsync(
        ConcurrentStep step, ExecutionPlan plan,
        DateTime startTime, List<ExecutionSpan> spans,
        CancellationToken ct)
    {
        var agentNames = step.Steps
            .OfType<HandoffStep>()
            .Select(s => s.SpecialistName)
            .ToArray();

        var span = ExecutionSpan.Start(step.Name, "concurrent", traceId: plan.TraceId);
        OnSpan?.Invoke(span);

        var text = await _agentWorkflows.RunConcurrentAsync(
            agentNames, plan.Query, plan.TraceId, ct).ConfigureAwait(false);

        var completedSpan = span.Complete();
        spans.Add(completedSpan);
        OnSpan?.Invoke(completedSpan);

        return new ExecutionResult
        {
            Text = text,
            StepOutputs = new Dictionary<string, string> { [step.Name] = text },
            Spans = spans,
            Duration = DateTime.UtcNow - startTime,
            Success = true,
        };
    }

    private static bool IsGreetingLike(string query)
    {
        var trimmed = query.Trim().ToLowerInvariant();
        return trimmed is "hi" or "hello" or "你好" or "早上好" or "下午好" or "晚上好"
            or "hey" or "good morning" or "good afternoon" or "good evening"
            || trimmed.StartsWith("hi ") || trimmed.StartsWith("hello ")
            || trimmed.StartsWith("你好,") || trimmed.StartsWith("你好 ")
            || trimmed.Length <= 10;
    }
}
