using System.Diagnostics;
using LTAI.AI.Governors;
using LTAI.Core.Models;
using Microsoft.Agents.AI.Workflows;

namespace LTAI.Agent;

public sealed class GovernorQuery
{
    public string Text { get; init; } = "";
    public string? TraceId { get; init; }
}

public sealed class GovernorResult
{
    public string Response { get; init; } = "";
    public string TraceId { get; init; } = "";
    public string? Label { get; init; }
    public string? ModelUsed { get; init; }
    public bool IsBlocked { get; init; }
    public string? BlockReason { get; init; }
}

public sealed class ClassifiedQuery
{
    public string Text { get; init; } = "";
    public string Label { get; init; } = "deep";
    public float Complexity { get; init; }
    public string? Emotion { get; init; }
    public string TraceId { get; init; } = "";
}

internal sealed partial class PreProcessExecutor(InputGovernor input, ContextGovernor context, RoutingGovernor routing) : Executor("PreProcess")
{
    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder builder) => builder;

    [MessageHandler]
    private async ValueTask<ClassifiedQuery> HandleAsync(GovernorQuery query, IWorkflowContext ctx, CancellationToken ct)
    {
        var traceId = query.TraceId ?? Guid.NewGuid().ToString("N");
        var inputResult = await input.ProcessAsync(new Handshake
        {
            To = "input", Action = "process",
            Payload = new Dictionary<string, object?> { ["query"] = query.Text },
            ReplyTo = traceId
        }, ct);

        if (inputResult.Action == "reflex")
        {
            var command = inputResult.Payload?.GetValueOrDefault("command")?.ToString() ?? "/help";
            var reflexResponse = command switch
            {
                "/help" => "LivingTree AI Agent v5.5 (.NET 10). Commands: /help /status /pause /resume",
                "/status" => "System is operational.",
                _ => $"Unknown command: {command}"
            };
            await ctx.YieldOutputAsync(new GovernorResult { Response = reflexResponse, TraceId = traceId, Label = "reflex" });
            return new ClassifiedQuery { Text = query.Text, Label = "reflex", TraceId = traceId };
        }

        var contextResult = await context.ProcessAsync(new Handshake
        {
            To = "context", Action = "preload",
            Payload = inputResult.Payload, ReplyTo = traceId
        }, ct);

        var routingResult = await routing.ProcessAsync(new Handshake
        {
            To = "routing", Action = "select_provider",
            Payload = inputResult.Payload, ReplyTo = traceId
        }, ct);

        var label = inputResult.Payload?.GetValueOrDefault("label")?.ToString() ?? "deep";
        var complexity = inputResult.Payload?.GetValueOrDefault("complexity") is float c ? c : 0.5f;
        var emotion = inputResult.Payload?.GetValueOrDefault("emotion")?.ToString();

        await ctx.AddEventAsync(new ProgressEvent($"Intent: {label}, complexity: {complexity:F2}"));

        return new ClassifiedQuery { Text = query.Text, Label = label, Complexity = complexity, Emotion = emotion, TraceId = traceId };
    }
}

internal sealed partial class PipelineExecutor(LivingTreeSystem system) : Executor("Pipeline")
{
    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder builder) => builder;

    [MessageHandler]
    private async ValueTask HandleAsync(ClassifiedQuery query, IWorkflowContext ctx, CancellationToken ct)
    {
        if (query.Label == "reflex") return;

        await ctx.AddEventAsync(new ProgressEvent("Processing through governor pipeline"));
        var result = await system.ProcessTypedAsync(
            new AI.Governors.GovernorInput { Query = query.Text, TraceId = query.TraceId },
            ct);

        await ctx.YieldOutputAsync(new GovernorResult
        {
            Response = result.Response,
            TraceId = result.TraceId,
            Label = query.Label,
            IsBlocked = result.IsBlocked,
            BlockReason = result.BlockReason
        });
    }
}

internal sealed class ProgressEvent(string message) : WorkflowEvent(message);

public static class GovernorWorkflow
{
    private static readonly ActivitySource ActivitySource = new("LTAI.Agent.Mesh");

    public static Workflow BuildGovernorWorkflow(LivingTreeSystem system)
    {
        var preExec = new PreProcessExecutor(system.InputGovernor, system.ContextGovernor, system.RoutingGovernor);
        var pipelineExec = new PipelineExecutor(system);

        var builder = new WorkflowBuilder(preExec);
        builder.AddEdge(preExec, pipelineExec);
        builder.WithOutputFrom(pipelineExec);
        return builder.Build();
    }

    public static async IAsyncEnumerable<WorkflowEvent> ExecuteWorkflowStreamingAsync(
        LivingTreeSystem system,
        string query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        using var activity = ActivitySource.StartActivity("governor.workflow", ActivityKind.Server);
        activity?.SetTag("governor.query_length", query.Length);

        var workflow = BuildGovernorWorkflow(system);
        var input = new GovernorQuery { Text = query, TraceId = activity?.TraceId.ToString() ?? Guid.NewGuid().ToString("N") };
        await using var run = await InProcessExecution.RunStreamingAsync(workflow, input);

        await foreach (var evt in run.WatchStreamAsync().WithCancellation(ct))
            yield return evt;
    }

    public static async Task<GovernorResult> ExecuteWorkflowAsync(
        LivingTreeSystem system,
        string query,
        CancellationToken ct = default)
    {
        GovernorResult? result = null;
        Exception? caughtException = null;

        await foreach (var evt in ExecuteWorkflowStreamingAsync(system, query, ct))
        {
            switch (evt)
            {
                case WorkflowOutputEvent output when output.Data is GovernorResult gr:
                    result = gr;
                    break;
                case WorkflowErrorEvent err:
                    caughtException = err.Exception ?? new InvalidOperationException(err.Exception?.Message ?? "Unknown workflow error");
                    break;
            }
        }

        if (caughtException != null)
            throw new InvalidOperationException($"GovernorWorkflow failed: {caughtException.Message}", caughtException);

        return result ?? throw new InvalidOperationException("GovernorWorkflow produced no output");
    }
}
