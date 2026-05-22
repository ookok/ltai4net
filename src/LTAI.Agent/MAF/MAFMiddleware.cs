using LTAI.AI.Governors;
using LTAI.AI.Utilities;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace LTAI.Agent;

public static class LTAIMiddleware
{
    public static AIAgent WithLTAIGovernance(this AIAgent agent, IServiceProvider services)
        => agent.AsBuilder().WithLTAIGovernance(services).Build();

    public static AIAgentBuilder WithLTAIGovernance(this AIAgentBuilder builder, IServiceProvider services)
    {
        var logger = services.GetRequiredService<ILogger<LTAIAgent>>();
        return builder.Use(
            runFunc: (messages, session, options, innerAgent, ct) =>
                AgentRunAsync(messages, session, options, innerAgent, logger, ct),
            runStreamingFunc: (messages, session, options, innerAgent, ct) =>
                AgentRunStreamingAsync(messages, session, options, innerAgent, logger, ct));
    }

    private static async Task<AgentResponse> AgentRunAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
        AIAgent innerAgent, ILogger logger, CancellationToken ct)
    {
        ClassifyAndLog(messages, logger);
        return await innerAgent.RunAsync(messages, session, options, ct);
    }

    private static async IAsyncEnumerable<AgentResponseUpdate> AgentRunStreamingAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
        AIAgent innerAgent, ILogger logger,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        ClassifyAndLog(messages, logger);
        await foreach (var update in innerAgent.RunStreamingAsync(messages, session, options, ct))
            yield return update;
    }

    private static void ClassifyAndLog(IEnumerable<ChatMessage> messages, ILogger logger)
    {
        var msgList = messages.ToList();
        var userMessages = msgList.Where(m => m.Role == ChatRole.User).ToList();
        if (userMessages.Count == 0) return;

        var query = string.Join("\n", userMessages.Select(m => m.Text ?? ""));
        var (_, label) = GovernorUtilities.ClassifyIntent(query);
        var emotion = GovernorUtilities.DetectEmotion(query);

        logger.LogDebug("MAF middleware: label={Label} emotion={Emotion} queryLen={Len}",
            label, emotion, query.Length);
    }
}
