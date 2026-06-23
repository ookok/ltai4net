// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  ToolExecutionStep — ToolRegistry invocation + tool-call recovery
//
//  Phase 3b: wraps ToolRegistry to dispatch function calls and
//  collect results. Includes DeerFlow-inspired tool-call recovery:
//  when provider interrupts mid-tool-call, injects placeholder results
//  for dangling call_ids to prevent malformed history errors.
// ═══════════════════════════════════════════════════════════════

using System.Text.RegularExpressions;
using LTAI.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Pipeline.Steps;

public sealed class ToolExecutionStep : IPipelineStep
{
    private readonly ILogger<ToolExecutionStep> _logger;
    private readonly IToolRegistry _toolRegistry;

    public string Name => "ToolExecution";

    public ToolExecutionStep(IToolRegistry toolRegistry, ILogger<ToolExecutionStep>? logger = null)
    {
        _toolRegistry = toolRegistry;
        _logger = logger ?? NullLogger<ToolExecutionStep>.Instance;
    }

    public Task<MessageContext> ProcessAsync(MessageContext context)
    {
        // ── Phase 1: Detect and recover dangling tool calls ──
        RecoverDanglingToolCalls(context);

        // ── Phase 2: Scan for tool call/result pairs ──
        foreach (var msg in context.Messages)
        {
            if (msg.Contents == null) continue;

            foreach (var content in msg.Contents)
            {
                if (content is FunctionCallContent fc)
                {
                    var name = fc.Name ?? "";
                    var args = fc.Arguments is Dictionary<string, object?> dict
                        ? string.Join(", ", dict.Select(kv => $"{kv.Key}={kv.Value}"))
                        : "";

                    _logger.LogInformation("ToolExecutionStep: detected tool call '{Name}'", name);
                    context.ToolCalls.Add((name, args, ""));

                    // Pre-register for recovery: if this call never gets a result,
                    // the recovery path will fill it in
                    context.Set($"tool_call_{fc.CallId}", name);
                }

                if (content is FunctionResultContent frc)
                {
                    var resultStr = frc.Result?.ToString() ?? "";
                    _logger.LogDebug("ToolExecutionStep: tool result for '{CallId}', len={Len}",
                        frc.CallId, resultStr.Length);

                    // Mark this call_id as resolved
                    context.Set($"tool_result_{frc.CallId}", true);

                    if (context.ToolCalls.Count > 0)
                    {
                        var last = context.ToolCalls[^1];
                        context.ToolCalls[^1] = (last.Name, last.Arguments, resultStr);
                        var success = !resultStr.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
                            && !resultStr.Contains("\"success\": false");
                        _toolRegistry.RecordCall(last.Name, success, 0);
                    }
                }
            }
        }

        return Task.FromResult(context);
    }

    /// <summary>
    /// DeerFlow-inspired tool-call recovery:
    /// Detect dangling tool calls (provider interrupted mid-loop) and inject
    /// placeholder results so the next model invocation doesn't fail on
    /// invalid tool_call_id references.
    /// </summary>
    private static void RecoverDanglingToolCalls(MessageContext context)
    {
        // Scan messages for "orphaned" FunctionCallContent that lack
        // a corresponding FunctionResultContent in subsequent messages.
        var pendingCallIds = new List<(string CallId, string Name)>();

        for (int i = context.Messages.Count - 1; i >= 0; i--)
        {
            var msg = context.Messages[i];
            if (msg.Contents == null) continue;

            foreach (var content in msg.Contents)
            {
                if (content is FunctionCallContent fc)
                {
                    var callId = fc.CallId ?? "";
                    // Check if this call already has a result
                    if (!HasResultForCallId(context, callId))
                    {
                        pendingCallIds.Add((callId, fc.Name ?? ""));
                    }
                }
            }
        }

        if (pendingCallIds.Count == 0) return;

        // Inject recovery result messages for each dangling call
        var recoveryMsg = new ChatMessage(ChatRole.Tool, "")
        {
            Contents = new List<AIContent>()
        };

        foreach (var (callId, name) in pendingCallIds)
        {
            recoveryMsg.Contents.Add(new FunctionResultContent(callId, $"{{ \"error\": \"provider_interrupted\", \"tool\": \"{name}\" }}")
            {
                Exception = new OperationCanceledException($"Tool call '{name}' ({callId}) was interrupted by provider")
            });
        }

        context.Messages.Add(recoveryMsg);
    }

    private static bool HasResultForCallId(MessageContext context, string callId)
    {
        if (string.IsNullOrEmpty(callId)) return false;

        // Check for direct FunctionResultContent match
        foreach (var msg in context.Messages)
        {
            if (msg.Contents == null) continue;
            foreach (var content in msg.Contents)
            {
                if (content is FunctionResultContent frc && frc.CallId == callId)
                    return true;
            }
        }

        return false;
    }
}
