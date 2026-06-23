using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Pipeline.Steps;

/// <summary>
/// Detects repeated tool calls (same tool 3+ consecutive times
/// or alternating patterns) and injects a strategy-change suggestion.
/// Formerly an IChatClient wrapper (ProgressGuardChatClient); now an IPipelineStep.
/// </summary>
public sealed class ProgressGuardStep : IPipelineStep
{
    private readonly ILogger<ProgressGuardStep> _logger;

    public string Name => "ProgressGuard";

    public ProgressGuardStep(ILogger<ProgressGuardStep>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ProgressGuardStep>.Instance;
    }

    public Task<MessageContext> ProcessAsync(MessageContext context)
    {
        var guardMessage = BuildGuardMessage(context.Messages);
        if (guardMessage != null)
        {
            context.Messages.Add(guardMessage);
            _logger.LogDebug("ProgressGuard: injected {Msg}", guardMessage.Text);
        }
        return Task.FromResult(context);
    }

    private static ChatMessage? BuildGuardMessage(IReadOnlyList<ChatMessage> messages)
    {
        var consecutive = 0;
        string? lastTool = null;
        var totalCalls = 0;
        var recentTools = new List<string>(8);

        var functionCalls = messages
            .SelectMany(m => m.Contents?.OfType<FunctionCallContent>() ?? [])
            .ToList();

        foreach (var fc in functionCalls)
        {
            totalCalls++;
            recentTools.Add(fc.Name ?? "");
            if (recentTools.Count > 8) recentTools.RemoveAt(0);

            if (string.Equals(fc.Name, lastTool, StringComparison.OrdinalIgnoreCase))
            {
                consecutive++;
                if (consecutive >= 3)
                {
                    return new ChatMessage(ChatRole.System,
                        $"[System: You have called '{fc.Name}' {consecutive + 1} times consecutively " +
                        "with no apparent progress. Try a different approach or tool instead.]");
                }
            }
            else
            {
                consecutive = 0;
                lastTool = fc.Name;
            }

            if (recentTools.Count == 8 && recentTools[0] == recentTools[2] && recentTools[2] == recentTools[4] && recentTools[4] == recentTools[6]
                && recentTools[1] == recentTools[3] && recentTools[3] == recentTools[5] && recentTools[5] == recentTools[7]
                && recentTools[0] != recentTools[1])
            {
                return new ChatMessage(ChatRole.System,
                    $"[System: You are alternating between '{recentTools[0]}' and '{recentTools[1]}' " +
                    "with no progress. Try a different approach.]");
            }

            var toolCount = recentTools.Count(n => string.Equals(n, fc.Name, StringComparison.OrdinalIgnoreCase));
            if (toolCount >= 10)
            {
                return new ChatMessage(ChatRole.System,
                    $"[System: You have called '{fc.Name}' {toolCount} times in the last 8 calls. " +
                    "Try a different approach.]");
            }
        }

        return null;
    }
}
