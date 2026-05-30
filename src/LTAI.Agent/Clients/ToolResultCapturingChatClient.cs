// Copyright (c) LTAI. All rights reserved.

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace LTAI.Agent;

/// <summary>
/// IChatClient decorator that intercepts tool execution results (FunctionResultContent)
/// from request messages and yields them as streaming updates.
/// Placed BETWEEN FunctionInvokingChatClient and the leaf chat client.
/// </summary>
public sealed class ToolResultCapturingChatClient : DelegatingChatClient
{
    private static bool _logged;

    public ToolResultCapturingChatClient(IChatClient inner) : base(inner) { }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        return CoreAsync(messages, options, cancellationToken);
    }

    private async IAsyncEnumerable<ChatResponseUpdate> CoreAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var token = ct != default ? ct : cancellationToken;

        // 首调用时打印所有可用工具名（诊断用）
        if (!_logged && options?.Tools != null)
        {
            _logged = true;
            var toolNames = options.Tools.Select(t => t.Name).Where(n => n != null).ToList();
            System.Diagnostics.Debug.WriteLine($"[ToolCapture] Available tools ({toolNames.Count}):");
            foreach (var name in toolNames)
                System.Diagnostics.Debug.WriteLine($"  {name}");
        }

        // 检查请求消息中的 FunctionResultContent（来自 FunctionInvokingChatClient 的工具执行结果）
        foreach (var msg in messages)
        {
            if (msg.Contents == null) continue;
            foreach (var content in msg.Contents)
            {
                if (content is FunctionResultContent frc && frc.Result != null)
                {
                    var resultStr = frc.Result?.ToString() ?? "";
                    if (resultStr.Length > 500)
                        resultStr = resultStr[..500] + "...";
                    // 直接 yield 工具结果通知给 TUI
                    yield return new ChatResponseUpdate(ChatRole.Assistant, $"  📄 工具返回: {resultStr}\n");
                }
            }
        }

        // 正常转发到叶子客户端
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, token).WithCancellation(token))
        {
            yield return update;
        }
    }
}
