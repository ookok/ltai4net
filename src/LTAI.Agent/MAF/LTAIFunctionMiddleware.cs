using LTAI.AI.Governors;
using LTAI.Agent.Governance;
using LTAI.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent;

public static class LTAIFunctionMiddleware
{
    private static readonly HashSet<string> SandboxedToolCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "shell", "code_exec", "code_run", "execute", "bash", "cmd"
    };

    public static AIAgent WithToolGovernance(this AIAgent agent, IServiceProvider services)
    {
        return agent.AsBuilder().WithToolGovernance(services).Build();
    }

    public static AIAgentBuilder WithToolGovernance(this AIAgentBuilder builder, IServiceProvider services)
    {
        var actionGov = ActionGovernor.Instance;
        var logger = services.GetRequiredService<ILogger<LTAIAgent>>();

        return builder.Use((agent, context, next, ct) =>
            InterceptToolCallAsync(agent, context, next, actionGov, logger, services, ct));
    }

    private static async ValueTask<object?> InterceptToolCallAsync(
        AIAgent agent,
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
        ActionGovernor actionGov,
        ILogger logger,
        IServiceProvider services,
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

        if (ShouldUseSandbox(toolName, args))
        {
            var sandbox = services.GetService<ISandboxExecutor>();
            if (sandbox is not null)
            {
                logger.LogInformation("Tool governance: routing '{Tool}' through sandbox", toolName);
                return await ExecuteInSandboxAsync(toolName, args, sandbox, logger, ct).ConfigureAwait(false);
            }
            else
            {
                logger.LogWarning("Tool governance: sandbox not available for '{Tool}', executing directly (fallback)", toolName);
            }
        }

        logger.LogDebug("Tool governance: {Tool} allowed (severity={Severity})", toolName, decision.Severity);
        return await next(context, ct).ConfigureAwait(false);
    }

    private static bool ShouldUseSandbox(string toolName, Dictionary<string, object?> args)
    {
        if (SandboxedToolCategories.Contains(toolName))
            return true;

        foreach (var category in SandboxedToolCategories)
        {
            if (toolName.Contains(category, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        var command = args.GetValueOrDefault("command")?.ToString()
            ?? args.GetValueOrDefault("cmd")?.ToString()
            ?? args.GetValueOrDefault("script")?.ToString();

        return command is not null && (
            command.Contains("sudo", StringComparison.OrdinalIgnoreCase) ||
            command.Contains("rm -rf", StringComparison.OrdinalIgnoreCase) ||
            command.Contains("del /f", StringComparison.OrdinalIgnoreCase) ||
            command.Contains("format", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<object?> ExecuteInSandboxAsync(
        string toolName,
        Dictionary<string, object?> args,
        ISandboxExecutor sandbox,
        ILogger logger,
        CancellationToken ct)
    {
        var command = args.GetValueOrDefault("command")?.ToString()
            ?? args.GetValueOrDefault("cmd")?.ToString()
            ?? "";

        if (string.IsNullOrWhiteSpace(command))
            return new { error = "No command provided for sandbox execution." };

        try
        {
            var result = await sandbox.ExecuteCommandAsync(
                command,
                timeoutSeconds: 30,
                memoryMb: 256,
                allowNetwork: false,
                cancellationToken: ct).ConfigureAwait(false);

            return new
            {
                sandboxed = result.Sandboxed,
                result.Success,
                result.Stdout,
                result.Stderr,
                result.ExitCode,
                result.ExecutionTimeMs,
                result.PeakMemoryKb,
                result.TimedOut,
                result.Error
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sandbox execution failed for {Tool}", toolName);
            return new { error = $"Sandbox execution failed: {ex.Message}", sandboxed = false };
        }
    }
}
