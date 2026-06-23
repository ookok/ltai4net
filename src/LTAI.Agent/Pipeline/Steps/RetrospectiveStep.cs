using System.Text;
using LTAI.Agent.Memory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Pipeline.Steps;

public sealed record RetrospectiveRecord
{
    public string AgentName { get; init; } = "";
    public string TaskId { get; init; } = "";
    public string TaskDescription { get; init; } = "";
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public int ToolCallCount { get; init; }
    public int TokenEstimate { get; init; }
    public string? Outcome { get; init; }
    public string? Lesson { get; init; }
    public string? Improvement { get; init; }
    public bool HadErrors { get; init; }
    public TimeSpan Duration { get; init; }
}

public sealed class RetrospectiveStep : IPipelineStep
{
    private readonly ILogger<RetrospectiveStep> _logger;
    private readonly PalaceStore? _palaceStore;
    private readonly int _maxRetrospectives;

    public string Name => "Retrospective";

    public RetrospectiveStep(ILogger<RetrospectiveStep>? logger = null,
        PalaceStore? palaceStore = null, int maxRetrospectives = 500)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RetrospectiveStep>.Instance;
        _palaceStore = palaceStore;
        _maxRetrospectives = maxRetrospectives;
    }

    public async Task<MessageContext> ProcessAsync(MessageContext context)
    {
        var request = context.Request;
        if (string.IsNullOrEmpty(request) || request.Length < 10)
            return context;

        var toolCallCount = context.ToolCalls.Count;
        var hadErrors = context.Spans.Any(s => s.Status == Execution.SpanStatus.Failure);
        var firstSpan = context.Spans.Count > 0 ? context.Spans.MinBy(s => s.StartTimeUtc) : null;
        var lastSpan = context.Spans.Count > 0 ? context.Spans.MaxBy(s => s.EndTimeUtc) : null;
        var duration = firstSpan != null && lastSpan != null
            ? lastSpan.EndTimeUtc - firstSpan.StartTimeUtc
            : TimeSpan.Zero;

        var sb = new StringBuilder();
        sb.AppendLine("### 执行回顾");
        sb.AppendLine();
        sb.AppendLine("**任务**: " + request.TruncateForRetro(200));
        sb.AppendLine($"**工具调用**: {toolCallCount} 次");
        sb.AppendLine($"**耗时**: {duration.TotalSeconds:F1}s");
        sb.AppendLine($"**是否出错**: {(hadErrors ? "是" : "否")}");

        if (hadErrors)
        {
            var errors = context.Spans
                .Where(s => s.Status == Execution.SpanStatus.Failure && s.Error != null)
                .Select(s => $"- {s.StepName}: {s.Error}");
            sb.AppendLine("**错误**:");
            sb.AppendLine(string.Join("\n", errors));
        }

        var tokenEstimate = context.TryGet<int>("TokenEstimate", out var tokens) ? tokens : 0;
        sb.AppendLine($"**Token 预估**: {tokenEstimate}");

        var record = new RetrospectiveRecord
        {
            AgentName = context.TryGet<string>("AgentName", out var agent) ? agent ?? "" : "",
            TaskId = context.TraceId ?? Guid.NewGuid().ToString("N")[..12],
            TaskDescription = request.TruncateForRetro(200),
            ToolCallCount = toolCallCount,
            TokenEstimate = tokenEstimate,
            Outcome = hadErrors ? "failed" : "success",
            HadErrors = hadErrors,
            Duration = duration
        };

        if (_palaceStore != null)
        {
            try
            {
                var wing = "retrospective";
                var room = record.AgentName;
                var content = System.Text.Json.JsonSerializer.Serialize(record);
                await _palaceStore.StoreAsync(wing, room, content,
                    importance: hadErrors ? 0.8 : 0.4,
                    agentId: record.AgentName).ConfigureAwait(false);
            }
            catch
            {
                _logger.LogWarning("Failed to store retrospective record");
            }
        }

        context.Set("RetrospectiveRecord", record);
        lock (context.MessagesLock) context.Messages.Add(new ChatMessage(ChatRole.System, sb.ToString()));

        _logger.LogInformation("Retrospective: {Agent} | {Tools} tools | {Duration:F1}s | {Outcome}",
            record.AgentName, toolCallCount, duration.TotalSeconds, record.Outcome);
        return context;
    }
}

file static class StringExt
{
    public static string TruncateForRetro(this string s, int maxLen)
        => s.Length <= maxLen ? s : s[..maxLen] + "...";
}
