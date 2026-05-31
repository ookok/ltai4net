// Copyright (c) LTAI. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;

namespace LTAI.Agent;

/// <summary>
/// Agent decorator that injects tool call lifecycle notifications into the streaming output.
/// Wraps <see cref="DelegatingAIAgent.RunCoreStreamingAsync"/> to detect 
/// <see cref="FunctionCallContent"/> in updates and yield progress messages.
/// </summary>
/// <remarks>
/// Insertion point in the decorator chain (ServiceCollectionExtensions.cs):
/// <code>
/// agent = new ObservableToolAgent(agent);
/// </code>
/// Should be placed AFTER <see cref="ChatClientAgent"/> (innermost) so it sees raw updates,
/// but BEFORE LoggingAgent/ToolApprovalAgent/OpenTelemetryAgent.
/// </remarks>
public sealed class ObservableToolAgent : DelegatingAIAgent
{
    public ObservableToolAgent(AIAgent innerAgent) : base(innerAgent) { }

    protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return CoreStreamingAsync(messages, session, options, cancellationToken);
    }

    private async IAsyncEnumerable<AgentResponseUpdate> CoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var inner = InnerAgent.RunStreamingAsync(messages, session, options, ct);
        var token = ct != default ? ct : cancellationToken;

        await foreach (var update in inner.WithCancellation(token))
        {
            // Yield original update first
            yield return update;

            // Detect tool call: FunctionCallContent in Contents with empty/null Text
            if (update.Text == null && update.Contents?.Count > 0)
            {
                foreach (var content in update.Contents)
                {
                    if (content is FunctionCallContent fc && !string.IsNullOrEmpty(fc.Name))
                    {
                        yield return new AgentResponseUpdate(ChatRole.Assistant, $"⏳ 正在调用 {fc.Name}...\n");
                        LTAI.Core.Configuration.UsageTracker.SetActiveTool(fc.Name);
                        LTAI.Core.Configuration.UsageTracker.StartToolTimer();
                        break;
                    }
                }
            }

            // Detect tool RESULT: FunctionResultContent
            if (update.Contents?.Count > 0)
            {
                foreach (var content in update.Contents)
                {
                    if (content is FunctionResultContent frc && frc.Result != null)
                    {
                        LTAI.Core.Configuration.UsageTracker.StopToolTimer();
                        var resultStr = frc.Result?.ToString() ?? "(null)";
                        if (resultStr.Length > 200)
                            resultStr = resultStr[..200] + "...";
                        yield return new AgentResponseUpdate(ChatRole.Assistant, $"  ✅ 返回: {resultStr}\n");
                        break;
                    }
                }
            }
        }

        // Clear active tool after stream ends
        LTAI.Core.Configuration.UsageTracker.SetActiveTool("");
    }
}
