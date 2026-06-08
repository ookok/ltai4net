// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  RouterStep — IExecutionEngine routing
//
//  Phase 3b: wraps IExecutionEngine (Plan → Execute) as a pipeline step.
//  After execution, the result is stored in MessageContext.Result.
// ═══════════════════════════════════════════════════════════════

using LTAI.Agent.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Pipeline.Steps;

/// <summary>
/// Pipeline step that routes the request through the IExecutionEngine.
/// Calls PlanAsync to decide the execution strategy, then ExecuteAsync
/// to run it. Stores the result in MessageContext.Result.
/// </summary>
public sealed class RouterStep : IPipelineStep
{
    private readonly IExecutionEngine _engine;
    private readonly ILogger<RouterStep> _logger;

    public string Name => "Router";

    public RouterStep(
        IExecutionEngine engine,
        ILogger<RouterStep>? logger = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _logger = logger ?? NullLogger<RouterStep>.Instance;
    }

    public async Task<MessageContext> ProcessAsync(MessageContext context)
    {
        if (context.SafetyBlocked)
        {
            _logger.LogInformation("RouterStep: skipping (safety blocked)");
            context.Messages.Add(new Microsoft.Extensions.AI.ChatMessage(
                Microsoft.Extensions.AI.ChatRole.Assistant,
                "I cannot process this request due to safety restrictions."));
            return context;
        }

        // ── 语法检查阻断：不执行新任务，等待 Agent 修复语法错误 ──
        if (context.TryGet<bool>("GrammarCheckBlocked", out var blocked) && blocked)
        {
            var reason = context.TryGet<string>("GrammarCheckReason", out var r) ? r : "语法错误";
            _logger.LogInformation("RouterStep: waiting for grammar fix: {Reason}", reason);
            return context;
        }

        _logger.LogInformation("RouterStep: planning execution for: {Request}",
            context.Request[..Math.Min(context.Request.Length, 80)]);

        // Phase 1: Plan
        var plan = await _engine.PlanAsync(context.Request, context.CancellationToken)
            .ConfigureAwait(false);
        context.Plan = plan;

        if (plan.Steps.Count == 0)
        {
            _logger.LogWarning("RouterStep: plan has no steps (branch={Branch})", plan.Branch);
            context.Messages.Add(new Microsoft.Extensions.AI.ChatMessage(
                Microsoft.Extensions.AI.ChatRole.Assistant,
                "I couldn't determine how to process this request."));
            return context;
        }

        // Phase 2: Execute
        _logger.LogInformation("RouterStep: executing plan with {StepCount} step(s), branch={Branch}",
            plan.Steps.Count, plan.Branch);

        var result = await _engine.ExecuteAsync(plan, context.CancellationToken)
            .ConfigureAwait(false);
        context.Result = result;

        // Collect spans
        if (result.Spans.Count > 0)
            context.Spans.AddRange(result.Spans);

        // Store messages
        if (result.Messages.Count > 0)
            context.Messages.AddRange(result.Messages);

        _logger.LogInformation("RouterStep: execution completed, success={Success}, textLen={Len}",
            result.Success, result.Text.Length);

        return context;
    }
}
