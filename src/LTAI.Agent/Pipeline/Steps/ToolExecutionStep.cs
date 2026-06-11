// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  ToolExecutionStep — ToolRegistry invocation
//
//  Phase 3b: wraps the static ToolRegistry to dispatch function
//  calls and collect results. Modeled after the ProcessContentsAsync
//  logic from ResponseStreamer (TUI layer).
//
//  Note: ToolRegistry is a static class with SearchTopKAsync for
//  retrieval. Tool execution (FunctionCallContent dispatch) is
//  handled by the MAF pipeline / IChatClient layer. This step
//  provides a pipeline hook for pre/post tool processing.
// ═══════════════════════════════════════════════════════════════

using LTAI.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Pipeline.Steps;

/// <summary>
/// Pipeline step that processes FunctionCallContent items from
/// accumulated messages and records tool execution results.
///
/// This replaces the inline ProcessContentsAsync in ResponseStreamer.
/// It scans the MessageContext for function calls and logs them
/// for post-processing by downstream steps or the final response builder.
/// </summary>
public sealed class ToolExecutionStep : IPipelineStep
{
    private readonly ILogger<ToolExecutionStep> _logger;

    public string Name => "ToolExecution";

    public ToolExecutionStep(ILogger<ToolExecutionStep>? logger = null)
    {
        _logger = logger ?? NullLogger<ToolExecutionStep>.Instance;
    }

    public Task<MessageContext> ProcessAsync(MessageContext context)
    {
        // Scan all accumulated messages for FunctionCallContent
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

                    // Tool execution through ToolRegistry is handled by
                    // the static ToolRegistry.SearchTopKAsync for retrieval,
                    // and by the MAF IChatClient / AIFunction pipeline for
                    // actual function execution.
                }

                if (content is FunctionResultContent frc)
                {
                    var resultStr = frc.Result?.ToString() ?? "";
                    _logger.LogDebug("ToolExecutionStep: tool result for '{CallId}', len={Len}",
                        frc.CallId, resultStr.Length);

                    if (context.ToolCalls.Count > 0)
                    {
                        var last = context.ToolCalls[^1];
                        context.ToolCalls[^1] = (last.Name, last.Arguments, resultStr);
                        // Record tool call metrics
                        var success = !resultStr.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
                            && !resultStr.Contains("\"success\": false");
                        ToolRegistry.RecordCall(last.Name, success, 0);
                    }
                }
            }
        }

        return Task.FromResult(context);
    }
}
