using System.Collections.Concurrent;
using LTAI.AI.Interfaces;
using System.Text.Json;
using LTAI.AI.Governors;
using LTAI.Agent.MAF;
using LTAI.Models;
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
            foreach (var (key, task) in _tasks.ToArray())
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
            var system = context.RequestServices.GetService<ILivingTreeSystem>();
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
                if (context.Request.ContentLength > 100_000)
                {
                    context.Response.StatusCode = 413;
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Request body too large (max 100KB)" }));
                    return;
                }

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
                var system = sp.GetService<ILivingTreeSystem>();
                var chatClient = sp.GetService<IChatClient>();

                var loop = sp.GetService<AgenticLoop>();
                if (loop != null)
                {
                    var t = Task.Run(async () =>
                    {
                        task.Status = "running";
                        task.UseAgenticLoop = true;
                        task.SessionId = loop.SessionId;
                        task.PartAssembler = loop.PartAssembler;
                        try
                        {
                            var result = await loop.RunAsync(prompt, CancellationToken.None).ConfigureAwait(false);
                            task.Result = result.FinalOutput;
                            task.Status = result.Completed ? "completed" : "failed";
                            task.Complete();
                        }
                        catch (Exception ex)
                        {
                            task.Error = ex.Message;
                            task.Status = "failed";
                            task.Complete();
                        }
                    });
                    t.ContinueWith(t2 =>
                    {
                        if (t2.Exception != null)
                            Console.Error.WriteLine($"SseAgent loop task faulted: {task.TaskId}: {t2.Exception}");
                    }, TaskContinuationOptions.OnlyOnFaulted);
                }
                else if (system is not null)
                {
                    var t = Task.Run(async () =>
                    {
                        task.Status = "running";
                        try
                        {
                            task.StepsCompleted = 2;
                            var response = await system.ChatAsync(prompt).ConfigureAwait(false);
                            task.Result = response;
                            task.Status = "completed";
                            task.StepsCompleted = Steps.Length;
                            task.Complete();
                        }
                        catch (Exception ex)
                        {
                            task.Error = ex.Message;
                            task.Status = "failed";
                            task.Complete();
                        }
                    });
                    t.ContinueWith(t2 =>
                    {
                        if (t2.Exception != null)
                            Console.Error.WriteLine($"SseAgent system task faulted: {task.TaskId}: {t2.Exception}");
                    }, TaskContinuationOptions.OnlyOnFaulted);
                }
                else if (chatClient is not null)
                {
                    var t = Task.Run(async () =>
                    {
                        task.Status = "running";
                        try
                        {
                            task.StepsCompleted = 2;
                            var response = await chatClient.GetResponseAsync(prompt).ConfigureAwait(false);
                            task.Result = response.Text ?? "";
                            task.Status = "completed";
                            task.StepsCompleted = Steps.Length;
                            task.Complete();
                        }
                        catch (Exception ex)
                        {
                            task.Error = ex.Message;
                            task.Status = "failed";
                            task.Complete();
                        }
                    });
                    t.ContinueWith(t2 =>
                    {
                        if (t2.Exception != null)
                            Console.Error.WriteLine($"SseAgent chatClient task faulted: {task.TaskId}: {t2.Exception}");
                    }, TaskContinuationOptions.OnlyOnFaulted);
                }
                else
                {
                    task.Status = "failed";
                    task.Error = "No backend (ILivingTreeSystem or IChatClient) available";
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
            context.Response.Headers["Connection"] = "keep-alive";

            if (!_tasks.TryGetValue(taskId, out var task))
            {
                await WriteSseData(context, new { type = "error", error = "Task not found" });
                return;
            }

            var ct = context.RequestAborted;

            if (task.SessionId != null)
            {
                var partStore = context.RequestServices.GetService<PartStreamStore>();
                if (partStore != null)
                {
                    var history = await partStore.ReplayAsync(task.SessionId, ct);
                    foreach (var part in history)
                    {
                        var typeName = part switch { TextPart => "text", ReasoningPart => "reasoning", ToolInvocationPart => "tool", FilePart => "file", AgentPart => "agent", _ => "part" };
                        await WriteSseEvent(context, "part:replay", new
                        {
                            task_id = taskId,
                            part_id = part.Id,
                            type = typeName
                        });
                    }
                }
            }

            if (task.UseAgenticLoop && task.PartAssembler != null)
            {
                var assembler = task.PartAssembler;

                foreach (var part in assembler.Snapshot())
                {
                    if (ct.IsCancellationRequested) return;
                    await WriteSseEvent(context, "part:appended", new
                    {
                        task_id = taskId,
                        part_id = part.Id,
                        type = part.GetType().Name
                    });
                }

                var tcs = new TaskCompletionSource();
                Action<Part> onAppended = async (p) =>
                {
                    try
                    {
                        await WriteSseEvent(context, "part:appended", new
                        {
                            task_id = taskId,
                            part_id = p.Id,
                            type = p switch
                            {
                                TextPart => "text",
                                ReasoningPart => "reasoning",
                                ToolInvocationPart => "tool-invocation",
                                FilePart => "file",
                                AgentPart => "agent",
                                _ => "unknown"
                            }
                        });
                    }
                    catch { tcs.TrySetResult(); }
                };

                Action<Part> onUpdated = async (p) =>
                {
                    try
                    {
                        if (p is ToolInvocationPart tip)
                        {
                            await WriteSseEvent(context, "part:updated", new
                            {
                                task_id = taskId,
                                part_id = p.Id,
                                type = "tool-invocation",
                                tool_name = tip.ToolName,
                                state = tip.State.ToString().ToLowerInvariant(),
                                output = tip.Output,
                                error = tip.Error
                            });
                        }
                        else if (p is TextPart tp)
                        {
                            await WriteSseEvent(context, "part:updated", new
                            {
                                task_id = taskId,
                                part_id = p.Id,
                                type = "text",
                                text = tp.Text.Length > 200 ? tp.Text[^200..] : tp.Text
                            });
                        }
                    }
                    catch { tcs.TrySetResult(); }
                };

                assembler.OnPartAppended += onAppended;
                assembler.OnPartUpdated += onUpdated;

                try
                {
                    await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromMinutes(30), ct));
                }
                finally
                {
                    assembler.OnPartAppended -= onAppended;
                    assembler.OnPartUpdated -= onUpdated;
                }

                if (!ct.IsCancellationRequested)
                {
                    await WriteSseEvent(context, "message:finished", new
                    {
                        task_id = taskId,
                        status = task.Status,
                        result = task.Result
                    });
                }

                await context.Response.Body.FlushAsync().ConfigureAwait(false);
                return;
            }

            // Fallback: legacy polling-based streaming
            var lastStep = 0;
            while ((task.Status == "running" || task.Status == "pending")
                   && !ct.IsCancellationRequested)
            {
                if (task.StepsCompleted > lastStep && task.StepsCompleted <= Steps.Length)
                {
                    lastStep = task.StepsCompleted;
                    var stepName = Steps[lastStep - 1];
                    await WriteSseData(context, new
                    {
                        type = "progress",
                        step = stepName,
                        message = $"Executing {stepName.Replace('_', ' ')}..."
                    });
                }

                await Task.WhenAny(task.Completion, Task.Delay(200, ct)).ConfigureAwait(false);
            }

            if (ct.IsCancellationRequested) return;

            if (task.Status == "completed")
                await WriteSseData(context, new { type = "complete", result = task.Result });
            else if (task.Status == "failed")
                await WriteSseData(context, new { type = "error", error = task.Error });

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

    private static async Task WriteSseEvent(HttpContext context, string eventType, object data)
    {
        var json = JsonSerializer.Serialize(data);
        await context.Response.WriteAsync($"event: {eventType}\ndata: {json}\n\n");
        await context.Response.Body.FlushAsync().ConfigureAwait(false);
    }

    private static async Task WriteSseData(HttpContext context, object data)
    {
        var json = JsonSerializer.Serialize(data);
        await context.Response.WriteAsync($"data: {json}\n\n");
        await context.Response.Body.FlushAsync().ConfigureAwait(false);
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

    public bool UseAgenticLoop { get; set; }
    public string? SessionId { get; set; }
    public PartAssembler? PartAssembler { get; set; }

    private readonly TaskCompletionSource _completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Completion => _completionSource.Task;

    public void Complete()
    {
        _completionSource.TrySetResult();
    }
}

public sealed record AgentTaskRequest
{
    public string Prompt { get; init; } = string.Empty;
    public Dictionary<string, object?>? Options { get; init; }
}
