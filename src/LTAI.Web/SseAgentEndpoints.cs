using System.Collections.Concurrent;
using System.Text.Json;
using LTAI.AI.Governors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace LTAI.Web;

public static class SseAgentEndpoints
{
    private static readonly ConcurrentDictionary<string, SseTask> _tasks = new();
    private static readonly Timer _cleanupTimer;

    static SseAgentEndpoints()
    {
        _cleanupTimer = new Timer(CleanupOldTasks, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    private static void CleanupOldTasks(object? state)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddHours(-1);
            foreach (var (key, task) in _tasks)
            {
                if (task.Status is "completed" or "failed" && task.CreatedAt < cutoff)
                    _tasks.TryRemove(key, out _);
            }
        }
        catch { /* timer callback must not throw */ }
    }

    private static readonly string[] Steps =
    {
        "intent_recognition", "tool_discovery", "memory_recall", "skill_match",
        "planning", "execution", "reflection", "quality_check"
    };

    public static void MapSseAgentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/agent/health", (HttpContext context) =>
        {
            var system = context.RequestServices.GetService<LivingTreeSystem>();
            return Results.Json(new
            {
                status = system is not null ? "ok" : "degraded",
                version = "5.5",
                mode = system?.Mode.ToString() ?? "unknown",
                dna_enabled = system?.DNAEnabled ?? false
            });
        });

        endpoints.MapPost("/api/agent/tasks", async (HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync().ConfigureAwait(false);
                var request = JsonSerializer.Deserialize<AgentTaskRequest>(body);

                var prompt = request?.Prompt ?? "";
                var taskId = $"task_{Guid.NewGuid():N}"[..13];

                var task = new SseTask
                {
                    TaskId = taskId,
                    Status = "pending",
                    Prompt = prompt,
                    CreatedAt = DateTime.UtcNow
                };
                if (_tasks.Count >= 1000)
                {
                    var oldest = _tasks.Values
                        .Where(t => t.Status is "completed" or "failed")
                        .MinBy(t => t.CreatedAt);
                    if (oldest != null)
                        _tasks.TryRemove(oldest.TaskId, out _);
                }
                _tasks.TryAdd(taskId, task);

                var sp = context.RequestServices;
                var system = sp.GetService<LivingTreeSystem>();
                var chatClient = sp.GetService<IChatClient>();

                if (system is not null)
                {
                    _ = Task.Run(async () =>
                    {
                        task.Status = "running";
                        try
                        {
                            task.StepsCompleted = 2;
                            var response = await system.ChatAsync(prompt).ConfigureAwait(false);
                            task.Result = response;
                            task.Status = "completed";
                            task.StepsCompleted = Steps.Length;
                        }
                        catch (Exception ex)
                        {
                            task.Status = "failed";
                            task.Error = ex.Message;
                        }
                    });
                }
                else if (chatClient is not null)
                {
                    _ = Task.Run(async () =>
                    {
                        task.Status = "running";
                        try
                        {
                            task.StepsCompleted = 2;
                            var response = await chatClient.GetResponseAsync(prompt).ConfigureAwait(false);
                            task.Result = response.Text ?? "";
                            task.Status = "completed";
                            task.StepsCompleted = Steps.Length;
                        }
                        catch (Exception ex)
                        {
                            task.Status = "failed";
                            task.Error = ex.Message;
                        }
                    });
                }
                else
                {
                    task.Status = "failed";
                    task.Error = "No backend (LivingTreeSystem or IChatClient) available";
                }

                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    task_id = taskId,
                    status = task.Status == "failed" ? "failed" : "created"
                }));
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = 500;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message })).ConfigureAwait(false);
            }
        });

        endpoints.MapGet("/api/agent/tasks/{taskId}", async (HttpContext context, string taskId) =>
        {
            context.Response.ContentType = "application/json";

            if (!_tasks.TryGetValue(taskId, out var task))
            {
                context.Response.StatusCode = 404;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Task not found" }));
                return;
            }

            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                task_id = task.TaskId,
                status = task.Status,
                result = task.Result,
                error = task.Error
            })).ConfigureAwait(false);
        });

        endpoints.MapGet("/api/agent/tasks/{taskId}/stream", async (HttpContext context, string taskId) =>
        {
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers["Cache-Control"] = "no-cache";

            if (!_tasks.TryGetValue(taskId, out var task))
            {
                await context.Response.WriteAsync("data: {\"error\":\"Task not found\"}\n\n");
                return;
            }

            var lastStep = 0;
            while ((task.Status == "running" || task.Status == "pending")
                   && !context.RequestAborted.IsCancellationRequested)
            {
                if (task.StepsCompleted > lastStep && task.StepsCompleted <= Steps.Length)
                {
                    lastStep = task.StepsCompleted;
                    var stepName = Steps[lastStep - 1];
                    var msg = $"Executing {stepName.Replace('_', ' ')}...";
                    var sseData = JsonSerializer.Serialize(new { type = "progress", step = stepName, message = msg });
                    await context.Response.WriteAsync($"data: {sseData}\n\n");
                    await context.Response.Body.FlushAsync().ConfigureAwait(false);
                }
                await Task.Delay(100, context.RequestAborted).ConfigureAwait(false);
            }

            if (context.RequestAborted.IsCancellationRequested)
                return;

            if (task.Status == "completed")
            {
                var completeData = JsonSerializer.Serialize(new { type = "complete", result = task.Result });
                await context.Response.WriteAsync($"data: {completeData}\n\n");
            }
            else if (task.Status == "failed")
            {
                var errorData = JsonSerializer.Serialize(new { type = "error", error = task.Error });
                await context.Response.WriteAsync($"data: {errorData}\n\n");
            }

            await context.Response.Body.FlushAsync().ConfigureAwait(false);
        });

        endpoints.MapGet("/api/agent/tasks", async (HttpContext context) =>
        {
            context.Response.ContentType = "application/json";
            var tasks = _tasks.Values.Select(t => new
            {
                task_id = t.TaskId,
                status = t.Status,
                result = t.Result,
                error = t.Error
            });
            await context.Response.WriteAsync(JsonSerializer.Serialize(tasks)).ConfigureAwait(false);
        });
    }
}

public sealed class SseTask
{
    private readonly object _lock = new();
    private string _status = "pending";
    private string? _result;
    private string? _error;
    private int _stepsCompleted;

    public string TaskId { get; init; } = string.Empty;
    public string Prompt { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }

    public string Status
    {
        get { lock (_lock) return _status; }
        set { lock (_lock) _status = value; }
    }

    public string? Result
    {
        get { lock (_lock) return _result; }
        set { lock (_lock) _result = value; }
    }

    public string? Error
    {
        get { lock (_lock) return _error; }
        set { lock (_lock) _error = value; }
    }

    public int StepsCompleted
    {
        get { lock (_lock) return _stepsCompleted; }
        set { lock (_lock) _stepsCompleted = value; }
    }
}

public sealed record AgentTaskRequest
{
    public string Prompt { get; init; } = string.Empty;
    public Dictionary<string, object?>? Options { get; init; }
}
