// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  RouterStep — IExecutionEngine routing with /skill-name activation
//
//  Phase 3b: wraps IExecutionEngine (Plan → Execute) as a pipeline step.
//  After execution, the result is stored in MessageContext.Result.
//
//  DeerFlow-inspired: /skill-name prefix activates per-turn skill routing.
//  If user starts request with /agent-name, RouterStep extracts the agent
//  name and routes to the corresponding specialized agent.
// ═══════════════════════════════════════════════════════════════

using LTAI.Agent.Execution;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Pipeline.Steps;

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
            context.Messages.Add(new ChatMessage(ChatRole.Assistant,
                "I cannot process this request due to safety restrictions."));
            return context;
        }

        if (context.GrammarCheckBlocked)
        {
            var reason = context.TryGet<string>("GrammarCheckReason", out var r) ? r : "语法错误";
            _logger.LogInformation("RouterStep: waiting for grammar fix: {Reason}", reason);
            return context;
        }

        // ── DeerFlow-style /skill-name activation ──
        var (activatedAgent, cleanRequest) = ParseSkillActivation(context.Request);
        if (activatedAgent != null)
        {
            _logger.LogInformation("RouterStep: /{Agent} activated for single turn", activatedAgent);
            context.Set("ActivatedAgent", activatedAgent);
            context.Request = cleanRequest;

            // Inject activation instruction
            context.Messages.Add(new ChatMessage(ChatRole.System,
                $"## Single-Turn Agent Activation\nActive agent: `{activatedAgent}`. Complete this turn as `{activatedAgent}`. Reset to default after this turn."));
        }

        _logger.LogInformation("RouterStep: planning execution for: {Request}",
            context.Request[..Math.Min(context.Request.Length, 80)]);

        var plan = await _engine.PlanAsync(context.Request, context.CancellationToken)
            .ConfigureAwait(false);
        context.Plan = plan;

        if (plan.Steps.Count == 0)
        {
            _logger.LogWarning("RouterStep: plan has no steps (branch={Branch})", plan.Branch);
            context.Messages.Add(new ChatMessage(ChatRole.Assistant,
                "I couldn't determine how to process this request."));
            return context;
        }

        _logger.LogInformation("RouterStep: executing plan with {StepCount} step(s), branch={Branch}",
            plan.Steps.Count, plan.Branch);

        var result = await _engine.ExecuteAsync(plan, context.CancellationToken)
            .ConfigureAwait(false);
        context.Result = result;

        if (result.Spans.Count > 0)
            context.Spans.AddRange(result.Spans);

        if (result.Messages.Count > 0)
            context.Messages.AddRange(result.Messages);

        _logger.LogInformation("RouterStep: execution completed, success={Success}, textLen={Len}",
            result.Success, result.Text.Length);

        return context;
    }

    /// <summary>
    /// Parse /agent-name prefix from request. Returns (agentName, cleanRequest).
    /// Supports patterns: /code write a test, /data analyze this file.
    /// </summary>
    internal static (string? AgentName, string CleanRequest) ParseSkillActivation(string request)
    {
        if (string.IsNullOrEmpty(request) || request[0] != '/') return (null, request);

        var spaceIdx = request.IndexOf(' ');
        string agentName;
        string rest;

        if (spaceIdx < 0)
        {
            agentName = request[1..].Trim();
            rest = "";
        }
        else
        {
            agentName = request[1..spaceIdx].Trim();
            rest = request[(spaceIdx + 1)..].Trim();
        }

        if (string.IsNullOrEmpty(agentName)) return (null, request);

        // Known agent name prefixes
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "code", "chat", "data", "sql", "frontend", "writer", "api",
            "arch", "test", "review", "debug", "security", "devops",
            "explore", "office", "math", "llm", "system", "dci"
        };

        return known.Contains(agentName) ? (agentName, rest) : (null, request);
    }
}
