// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  TaskQueueTool — LLM-callable wrapper over TaskQueue (P14.13).
//
//  Why: TaskQueue is process-internal infrastructure (P5.4) that
//  schedules async work via Channel<T> consumer loops. The original
//  EnqueueAsync takes a Func<CancellationToken, Task<string>> which
//  is not serializable across the LLM tool boundary, so the queue
//  sat unused. TaskQueueTool solves that with a name-based handler
//  registry: handlers are registered at DI startup, and the LLM
//  invokes them by (name, json-payload).
//
//  Scope:
//    - Read side: List, Get, WaitAsync, Cancel (full visibility)
//    - Write side: Enqueue dispatches via name -> registered handler
//    - Built-in handlers: "echo", "sleep", "agent_delegate"
//
//  NOT in scope:
//    - Persistent / cross-process state (use MAF DurableTask for that)
//    - LLM call dispatch (handlers do typed work; the agent already
//      does its own LLM loop synchronously)
// ═══════════════════════════════════════════════════════════════

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using LTAI.Agent.Tasks;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Tools;

public sealed class TaskQueueTool
{
    private readonly TaskQueue _queue;
    private readonly ILogger<TaskQueueTool>? _logger;
    private readonly ConcurrentDictionary<string, Func<JsonElement, CancellationToken, Task<string>>> _handlers
        = new(StringComparer.OrdinalIgnoreCase);

    public TaskQueueTool(TaskQueue queue, ILogger<TaskQueueTool>? logger = null)
    {
        _queue = queue;
        _logger = logger;

        // Default handlers (P14.13):
        //   "echo"  — diagnostic, returns the payload verbatim
        //   "sleep" — diagnostic, takes { "seconds": int }
        RegisterHandler("echo", (payload, _) =>
            Task.FromResult(payload.ValueKind == JsonValueKind.Undefined
                ? "(empty payload)"
                : payload.GetRawText()));
        RegisterHandler("sleep", async (payload, ct) =>
        {
            var seconds = payload.ValueKind == JsonValueKind.Object
                          && payload.TryGetProperty("seconds", out var s)
                          && s.TryGetInt32(out var n)
                ? n
                : 1;
            seconds = Math.Clamp(seconds, 1, 30);
            await Task.Delay(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
            return $"slept {seconds}s";
        });
    }

    /// <summary>Register a named handler. Idempotent — last write wins.</summary>
    public void RegisterHandler(
        string name,
        Func<JsonElement, CancellationToken, Task<string>> handler)
    {
        _handlers[name] = handler;
        _logger?.LogInformation("TaskQueueTool: registered handler '{Name}'", name);
    }

    /// <summary>Inspect registered handler names (for diagnostics / tool description).</summary>
    public IReadOnlyCollection<string> RegisteredHandlers => _handlers.Keys.ToList();

    [Description("提交一个命名异步任务到 TaskQueue。任务会被后台 consumer 拉起执行。\n" +
        "可用 name 列表：echo / sleep / 自定义注册名。payload 是 JSON 参数。\n" +
        "返回 task ID；用 ListTasks / GetTask / WaitForTask 监控。")]
    public async Task<string> EnqueueTask(
        [Description("任务名 (handler 注册的 key)")] string name,
        [Description("任务参数 (JSON 字符串或 object)")] string? payloadJson = null,
        [Description("可选说明")] string? description = null)
    {
        if (!_handlers.TryGetValue(name, out var handler))
            return $"未知 task name '{name}'。已注册: {string.Join(", ", _handlers.Keys)}";

        JsonElement payload;
        try
        {
            payload = string.IsNullOrWhiteSpace(payloadJson)
                ? JsonDocument.Parse("null").RootElement
                : JsonDocument.Parse(payloadJson).RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return $"payload 不是合法 JSON: {ex.Message}";
        }

        var item = await _queue.EnqueueAsync(name, async ct =>
        {
            try { return await handler(payload, ct).ConfigureAwait(false); }
            catch (Exception ex) { return $"ERROR: {ex.Message}"; }
        }, description).ConfigureAwait(false);
        return $"Task #{item.Id} '{item.Name}' enqueued.";
    }

    [Description("列出所有 TaskQueue 中的任务及其状态 (Pending/Running/Completed/Failed/Cancelled)")]
    public string ListTasks()
    {
        var items = _queue.List();
        if (items.Count == 0) return "No queued tasks.";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## TaskQueue Tasks\n");
        sb.AppendLine("| ID | Name | Status | Started | Duration |");
        sb.AppendLine("|----|------|--------|---------|----------|");
        foreach (var i in items)
        {
            var status = i.Status switch
            {
                LTAI.Agent.Tasks.TaskStatus.Pending => "⏳ Pending",
                LTAI.Agent.Tasks.TaskStatus.Running => "▶️ Running",
                LTAI.Agent.Tasks.TaskStatus.Completed => "✅ Completed",
                LTAI.Agent.Tasks.TaskStatus.Failed => "❌ Failed",
                LTAI.Agent.Tasks.TaskStatus.Cancelled => "🚫 Cancelled",
                _ => i.Status.ToString(),
            };
            var started = i.StartedAt?.ToString("HH:mm:ss") ?? "—";
            var dur = i.CompletedAt is { } c && i.StartedAt is { } s
                ? (c - s).TotalSeconds.ToString("F1") + "s"
                : "—";
            sb.AppendLine($"| {i.Id[..8]} | {Markup.Escape(i.Name)} | {status} | {started} | {dur} |");
        }
        return sb.ToString();
    }

    [Description("按 ID 获取 TaskQueue 任务详情（含 result/error）")]
    public string GetTask(
        [Description("任务 ID (8+ 字符前缀即可)")] string taskId)
    {
        var item = ResolveId(taskId);
        if (item is null) return $"Task '{taskId}' not found.";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## Task {item.Id}");
        sb.AppendLine($"- Name: {item.Name}");
        sb.AppendLine($"- Description: {item.Description ?? "—"}");
        sb.AppendLine($"- Status: {item.Status}");
        sb.AppendLine($"- EnqueuedAt: {item.EnqueuedAt:yyyy-MM-dd HH:mm:ss}Z");
        sb.AppendLine($"- StartedAt: {item.StartedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—"}Z");
        sb.AppendLine($"- CompletedAt: {item.CompletedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—"}Z");
        sb.AppendLine($"- Attempt: {item.Attempt}");
        if (!string.IsNullOrEmpty(item.Result))
            sb.AppendLine($"\n### Result\n{item.Result}");
        if (!string.IsNullOrEmpty(item.Error))
            sb.AppendLine($"\n### Error\n{item.Error}");
        return sb.ToString();
    }

    [Description("阻塞等待 TaskQueue 任务完成，返回 result。timeout 秒后超时。")]
    public async Task<string> WaitForTask(
        [Description("任务 ID (8+ 字符前缀即可)")] string taskId,
        [Description("超时秒数 (1-600)")] int timeoutSec = 60)
    {
        var item = ResolveId(taskId);
        if (item is null) return $"Task '{taskId}' not found.";
        var result = await _queue.WaitAsync(item.Id, TimeSpan.FromSeconds(Math.Clamp(timeoutSec, 1, 600)))
            .ConfigureAwait(false);
        return result ?? $"Task '{taskId}' did not complete within {timeoutSec}s.";
    }

    [Description("取消 Pending/Running 的 TaskQueue 任务")]
    public string CancelTask(
        [Description("任务 ID (8+ 字符前缀即可)")] string taskId)
    {
        var item = ResolveId(taskId);
        if (item is null) return $"Task '{taskId}' not found.";
        if (item.Status is LTAI.Agent.Tasks.TaskStatus.Completed
                          or LTAI.Agent.Tasks.TaskStatus.Failed
                          or LTAI.Agent.Tasks.TaskStatus.Cancelled)
            return $"Task '{taskId}' already {item.Status}.";
        item.Status = LTAI.Agent.Tasks.TaskStatus.Cancelled;
        item.Error = "Cancelled by tool";
        return $"Task '{taskId}' cancelled.";
    }

    /// <summary>Prefix-match (8 chars) for resilience to copy-paste truncation.</summary>
    private TaskItem? ResolveId(string idOrPrefix)
    {
        var items = _queue.List();
        if (items.Count == 0) return null;
        return items.FirstOrDefault(i => i.Id == idOrPrefix)
            ?? items.FirstOrDefault(i => i.Id.StartsWith(idOrPrefix, StringComparison.OrdinalIgnoreCase));
    }

    private static class Markup
    {
        public static string Escape(string s) => s.Replace("|", "\\|").Replace("\n", " ");
    }
}
