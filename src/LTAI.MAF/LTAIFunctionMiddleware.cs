using LTAI.AI.Governors;
using LTAI.MAF.Governance;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LTAI.MAF;

public static class LTAIFunctionMiddleware
{
    public static AIAgent WithToolGovernance(this AIAgent agent, IServiceProvider services)
    {
        var actionGov = ActionGovernor.Instance;
        var logger = services.GetRequiredService<ILogger<LTAIAgent>>();

        return agent.AsBuilder()
            .Use((agent, context, next, ct) =>
                InterceptToolCallAsync(agent, context, next, actionGov, logger, ct))
            .Build();
    }

    private static async ValueTask<object?> InterceptToolCallAsync(
        AIAgent agent,
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
        ActionGovernor actionGov,
        ILogger logger,
        CancellationToken ct)
    {
        var toolName = context.Function.Name;
        var args = new Dictionary<string, object?>();
        if (context.Arguments != null)
        {
            foreach (var kv in context.Arguments)
                args[kv.Key] = kv.Value;
        }

        var decision = actionGov.Evaluate(AgentAction.ToolCall, args);
        if (!decision.Allowed)
        {
            logger.LogWarning("Tool governance blocked: {Tool}, rule={Rule}, reason={Reason}",
                toolName, decision.Rule, decision.Reason);
            return new { blocked = true, rule = decision.Rule, reason = decision.Reason };
        }

        if (decision.Severity == PolicySeverity.Warn)
        {
            logger.LogWarning("Tool governance warning: {Tool}, warnings={Warnings}",
                toolName, string.Join(", ", decision.Warnings));
        }

        logger.LogDebug("Tool governance: {Tool} allowed (severity={Severity})", toolName, decision.Severity);
        return await next(context, ct);
    }
}
