// Copyright (c) LTAI. All rights reserved.

using System.Runtime.CompilerServices;
using System.Text.Json;
using LTAI.Agent.Tools;
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
    private SafeToolExecutionMiddleware? _middleware;
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(30);

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

        // 懒初始化（首次拿到工具列表时构建）
        _middleware ??= options?.Tools != null ? new SafeToolExecutionMiddleware(options.Tools) : null;

        // 预建立 CallId → (Name, Arguments) 索引
        var callMap = BuildCallMap(messages);

        // 检查请求消息中的 FunctionResultContent（来自 FunctionInvokingChatClient 的工具执行结果）
        foreach (var msg in messages)
        {
            if (msg.Contents == null) continue;
            foreach (var content in msg.Contents)
            {
                if (content is FunctionResultContent frc && frc.Result != null)
                {
                    // [Fix 1] BeforeToolCall: loop detection + fuzzy matching
                    if (_middleware != null)
                    {
                        var callId = frc.CallId ?? "";
                        var (name, args) = callMap.TryGetValue(callId, out var info)
                            ? info
                            : (callId, "");
                        var (shouldSuppress, message) = _middleware.BeforeToolCall(name, args);
                        if (shouldSuppress)
                        {
                            yield return new ChatResponseUpdate(ChatRole.Assistant, $"  ⚠️ {message}\n");
                            continue;
                        }
                    }

                    var resultStr = frc.Result?.ToString() ?? "";
                    if (resultStr.Length > 500)
                        resultStr = resultStr[..500] + "...";
                    yield return new ChatResponseUpdate(ChatRole.Assistant, $"  📄 工具返回: {resultStr}\n");
                }
            }
        }

        // [Fix 2] Per-tool timeout: 用 CancellationToken 实现 30s 超时
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(ToolTimeout);
        var timeoutToken = timeoutCts.Token;

        await foreach (var update in base.GetStreamingResponseAsync(messages, options, timeoutToken)
            .WithCancellation(timeoutToken))
        {
            yield return update;
        }
    }

    private static Dictionary<string, (string name, string args)> BuildCallMap(IEnumerable<ChatMessage> messages)
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var m in messages)
        {
            if (m.Contents == null) continue;
            foreach (var c in m.Contents)
            {
                if (c is FunctionCallContent fcc && fcc.CallId != null)
                {
                    var args = fcc.Arguments != null ? JsonSerializer.Serialize(fcc.Arguments) : "";
                    map[fcc.CallId] = (fcc.Name ?? "", args);
                }
            }
        }
        return map;
    }
}
