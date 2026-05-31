// Copyright (c) LTAI. All rights reserved.

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;

namespace LTAI.Agent;

/// <summary>
/// Agent decorator that intercepts FunctionCallContent before it reaches
/// FunctionInvokingChatClient, executes the tool locally, and injects the result.
/// Workaround for FunctionInvokingChatClient's "tool not found" bug.
/// </summary>
public sealed class LocalToolExecutorAgent : DelegatingAIAgent
{
    private readonly Dictionary<string, Func<string, CancellationToken, Task<string>>> _tools;

    public LocalToolExecutorAgent(
        AIAgent innerAgent,
        Dictionary<string, Func<string, CancellationToken, Task<string>>> tools)
        : base(innerAgent)
    {
        _tools = tools;
    }

    protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return CoreAsync(messages, session, options, cancellationToken);
    }

    private async IAsyncEnumerable<AgentResponseUpdate> CoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var token = ct != default ? ct : cancellationToken;

        await foreach (var update in InnerAgent.RunStreamingAsync(messages, session, options, token).WithCancellation(token))
        {
            yield return update;

            // 检测 FunctionCallContent，本地执行
            if (update.Text == null && update.Contents?.Count > 0)
            {
                foreach (var content in update.Contents)
                {
                    if (content is FunctionCallContent fc && !string.IsNullOrEmpty(fc.Name))
                    {
                        if (_tools.TryGetValue(fc.Name, out var executor))
                        {
                            // 提取参数
                            var args = fc.Arguments;
                            var path = args?.TryGetValue("path", out var p) == true ? p?.ToString() ?? "" : "";
                            if (string.IsNullOrEmpty(path))
                                path = args?.TryGetValue("input", out var i) == true ? i?.ToString() ?? "" : "";

                            // 异步执行
                            var result = await executor(path, token).ConfigureAwait(false);

                            // yield 结果
                            yield return new AgentResponseUpdate(ChatRole.Tool,
                                $"  📄 {fc.Name} 返回: {result}\n");
                        }
                    }
                }
            }
        }
    }
}
