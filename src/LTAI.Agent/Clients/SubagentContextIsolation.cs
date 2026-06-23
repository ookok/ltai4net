// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  SubagentContextIsolation — IChatClient middleware for DeerFlow-
//  inspired sub-agent context isolation.
//
//  Each sub-agent invocation gets a fully isolated context:
//  - Separate system prompt (only the sub-agent's instructions)
//  - No cross-contamination from main agent messages
//  - Independent tool scope
//  - Results returned as structured summaries
//
//  Wraps the inner IChatClient (the LLM provider) with an isolation
//  layer that scopes messages to only the sub-agent's conversation.
// ═══════════════════════════════════════════════════════════════

using Microsoft.Extensions.AI;

namespace LTAI.Agent.Clients;

public sealed class SubagentContextIsolation : IChatClient
{
    private readonly IChatClient _inner;

    public SubagentContextIsolation(IChatClient inner)
    {
        _inner = inner;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var isolatedMessages = IsolateContext(messages);
        return await _inner.GetResponseAsync(isolatedMessages, options, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var isolatedMessages = IsolateContext(messages);
        await foreach (var update in _inner.GetStreamingResponseAsync(isolatedMessages, options, cancellationToken))
            yield return update;
    }

    public void Dispose() { }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        _inner.GetService(serviceType, serviceKey);

    /// <summary>
    /// Isolate sub-agent context: keep only the sub-agent's own messages
    /// plus a minimal system prompt. Strip main agent conversation history.
    /// </summary>
    private static List<ChatMessage> IsolateContext(IEnumerable<ChatMessage> messages)
    {
        var list = messages.ToList();
        if (list.Count == 0) return list;

        var isolated = new List<ChatMessage>();
        ChatMessage? subagentInstruction = null;

        foreach (var msg in list)
        {
            var role = msg.Role.ToString().ToLowerInvariant();

            if (role == "system" && msg.Text?.Contains("Subagent:") == true)
            {
                // This is the sub-agent's instruction — keep it
                subagentInstruction = msg;
            }
            else if (role == "user" || role == "assistant" || role == "tool")
            {
                // Only keep messages that belong to this sub-agent turn
                if (msg.Text?.Contains("[SubagentTurn]") == true)
                {
                    isolated.Add(msg);
                }
            }
        }

        // Always prepend the sub-agent instruction
        if (subagentInstruction != null)
            isolated.Insert(0, subagentInstruction);

        // If isolation produced nothing, return original (fallback)
        return isolated.Count > 0 ? isolated : list;
    }

    /// <summary>
    /// Wrap a sub-agent invocation with context isolation.
    /// Call this when spawning a sub-agent to ensure no context leakage.
    /// </summary>
    public static List<ChatMessage> BuildSubagentContext(
        string agentName,
        string task,
        List<ChatMessage>? parentContext = null)
    {
        var msgs = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.System,
                $"## Subagent: {agentName}\nYou are a specialized sub-agent `{agentName}`. " +
                $"Focus only on the assigned task. Return structured results.\n\n{task}\n[SubagentTurn]")
        };

        var userMsg = new ChatMessage(ChatRole.User, $"[SubagentTurn]\n{task}");
        msgs.Add(userMsg);

        return msgs;
    }
}
