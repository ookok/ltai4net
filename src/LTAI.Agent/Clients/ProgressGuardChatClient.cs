using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace LTAI.Agent.Clients;

/// <summary>
/// IChatClient decorator that detects repeated tool calls (same tool 3+ consecutive times
/// with no progress) and injects a strategy-change suggestion into the conversation.
/// </summary>
public sealed class ProgressGuardChatClient : IChatClient
{
    private readonly IChatClient _inner;

    public ProgressGuardChatClient(IChatClient inner)
    {
        _inner = inner;
    }

    public void Dispose() => _inner.Dispose();

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var messages = chatMessages as List<ChatMessage> ?? chatMessages.ToList();
        var guardMessage = BuildGuardMessage(messages);
        if (guardMessage != null)
            messages.Add(guardMessage);

        return await _inner.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messages = chatMessages as List<ChatMessage> ?? chatMessages.ToList();
        var guardMessage = BuildGuardMessage(messages);
        if (guardMessage != null)
            messages.Add(guardMessage);

        await foreach (var update in _inner.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
            yield return update;
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

            // Same tool consecutively >= 3 times?
            if (string.Equals(fc.Name, lastTool, StringComparison.OrdinalIgnoreCase))
            {
                consecutive++;
                if (consecutive >= 3)
                {
                    return new ChatMessage(ChatRole.System,
                        $"[System: You have called '{fc.Name}' {consecutive + 1} times consecutively " +
                        $"with no apparent progress. Try a different approach or tool instead.]");
                }
            }
            else
            {
                consecutive = 0;
                lastTool = fc.Name;
            }

            // Alternating pattern (A > B > A > B)?
            if (recentTools.Count == 8 && recentTools[0] == recentTools[2] && recentTools[2] == recentTools[4] && recentTools[4] == recentTools[6]
                && recentTools[1] == recentTools[3] && recentTools[3] == recentTools[5] && recentTools[5] == recentTools[7]
                && recentTools[0] != recentTools[1])
            {
                return new ChatMessage(ChatRole.System,
                    $"[System: You are alternating between '{recentTools[0]}' and '{recentTools[1]}' " +
                    $"with no progress. Try a different approach.]");
            }

            // Cumulative threshold: same tool with different args called 10+ times total
            var toolCount = recentTools.Count(n => string.Equals(n, fc.Name, StringComparison.OrdinalIgnoreCase));
            if (toolCount >= 10)
            {
                return new ChatMessage(ChatRole.System,
                    $"[System: You have called '{fc.Name}' {toolCount} times in the last 8 calls. " +
                    $"Try a different approach.]");
            }
        }

        return null;
    }

    object? IChatClient.GetService(Type serviceType, object? serviceKey)
        => serviceKey is null && serviceType?.IsInstanceOfType(this) == true ? this : null;
}
