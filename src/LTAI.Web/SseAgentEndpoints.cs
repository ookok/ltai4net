using System.Collections.Concurrent;
using System.Text.Json;
using LTAI.Core.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace LTAI.Web;

public static class SseAgentEndpoints
{
    private static readonly ConcurrentDictionary<string, SseTask> _tasks = new();

    private static readonly string[] Steps =
    {
        "intent_recognition", "tool_discovery", "memory_recall", "skill_match",
        "planning", "execution", "reflection", "quality_check"
    };

    public static void MapSseAgentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/agent/health", () =>
        {
            return Results.Json(new { status = "ok", version = "5.5" });
        });

        endpoints.MapPost("/api/agent/tasks", async (HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
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
                _tasks.TryAdd(taskId, task);

                var providerEngine = endpoints.ServiceProvider.GetService<IProviderEngine>();

                if (providerEngine != null)
                {
                    _ = Task.Run(async () =>
                    {
                        task.Status = "running";
                        try
                        {
                            var result = await providerEngine.ChatAsync(prompt);
                            task.Result = result;
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
                    _ = Task.Run(async () =>
                    {
                        task.Status = "running";
                        try
                        {
                            for (var i = 0; i < Steps.Length; i++)
                            {
                                await Task.Delay(200);
                                task.StepsCompleted = i + 1;
                            }
                            task.Result = $"[Simulated response for: {prompt}]";
                            task.Status = "completed";
                        }
                        catch (Exception ex)
                        {
                            task.Status = "failed";
                            task.Error = ex.Message;
                        }
                    });
                }

                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    task_id = taskId,
                    status = "created"
                }));
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = 500;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
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
            }));
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
            while (task.Status == "running" || task.Status == "pending")
            {
                if (task.StepsCompleted > lastStep && task.StepsCompleted <= Steps.Length)
                {
                    lastStep = task.StepsCompleted;
                    var stepName = Steps[lastStep - 1];
                    var msg = $"Executing {stepName.Replace('_', ' ')}...";
                    var sseData = JsonSerializer.Serialize(new { type = "progress", step = stepName, message = msg });
                    await context.Response.WriteAsync($"data: {sseData}\n\n");
                    await context.Response.Body.FlushAsync();
                }
                await Task.Delay(100);
            }

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

            await context.Response.Body.FlushAsync();
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
            await context.Response.WriteAsync(JsonSerializer.Serialize(tasks));
        });
    }
}

public sealed class SseTask
{
    public string TaskId { get; init; } = string.Empty;
    public string Status { get; set; } = "pending";
    public string Prompt { get; init; } = string.Empty;
    public string? Result { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; init; }
    public int StepsCompleted { get; set; }
}

public sealed record AgentTaskRequest
{
    public string Prompt { get; init; } = string.Empty;
    public Dictionary<string, object?>? Options { get; init; }
}
