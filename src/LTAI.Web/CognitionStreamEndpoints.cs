using System.Text.Json;
using LTAI.AI.Interfaces;
using LTAI.AI.Governors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LTAI.Web;

public static class CognitionStreamEndpoints
{
    public static void MapCognitionStreamEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/cognition/stream", async (HttpContext context) =>
        {
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.Connection = "keep-alive";

            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync().ConfigureAwait(false);
            var request = JsonSerializer.Deserialize<CognitionRequest>(body);

            if (request == null || string.IsNullOrWhiteSpace(request.Message))
            {
                await WriteSseEvent(context, "error", new { error = "Message is required" });
                return;
            }

            var cancellationToken = context.RequestAborted;
            var sessionId = $"cog_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

            await WriteSseEvent(context, "cog-start", new
            {
                phase = "start",
                message = request.Message.Length > 200 ? request.Message[..200] : request.Message,
                session_id = sessionId,
                ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0
            });

            try
            {
                var system = context.RequestServices.GetService<ILivingTreeSystem>();
                if (system is not null)
                {
                    var response = await system.ChatAsync(request.Message, cancellationToken).ConfigureAwait(false);

                    await WriteSseEvent(context, "phase", new
                    {
                        phase = "processing",
                        status = "done",
                        label = "LivingTree Processing",
                        summary = "Processed through 5-layer governor pipeline",
                        mode = system.Mode.ToString(),
                        dna_enabled = system.DNAEnabled
                    });

                    var toolRegistry = context.RequestServices.GetService<Core.Messaging.AIToolRegistry>();
                    var toolNames = toolRegistry?.ListTools().Take(5).ToArray() ?? Array.Empty<string>();

                    await WriteSseEvent(context, "phase", new
                    {
                        phase = "tools",
                        status = "done",
                        label = "Tools",
                        tools = toolNames,
                        count = toolNames.Length
                    });

                    await WriteSseEvent(context, "phase", new
                    {
                        phase = "memory",
                        status = "done",
                        label = "Memory",
                        entries = system.TaskPipeline.TotalCompletions,
                        pending = system.TaskPipeline.TotalSubmissions - system.TaskPipeline.TotalCompletions
                    });

                    var taskStats = system.TaskPipeline.GetStats();
                    await WriteSseEvent(context, "phase", new
                    {
                        phase = "planning",
                        status = "done",
                        label = "Task Planning",
                        active_tasks = taskStats.GetValueOrDefault("pending", 0),
                        total_submissions = system.TaskPipeline.TotalSubmissions,
                        total_completions = system.TaskPipeline.TotalCompletions
                    });

                    await WriteSseEvent(context, "phase", new
                    {
                        phase = "agents",
                        status = "done",
                        label = "Agent Mesh",
                        pipeline = "LivingTree + GovernorWorkflow",
                        evolution_phase = system.DNAStatus?.EvolutionPhase.ToString() ?? "disabled"
                    });

                    await WriteSseEvent(context, "cog-complete", new
                    {
                        phase = "complete",
                        session_id = sessionId,
                        response_preview = response.Length > 200 ? response[..200] : response,
                        ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0
                    });
                }
                else
                {
                    var chatClient = context.RequestServices.GetService<IChatClient>();
                    if (chatClient is not null)
                    {
                        var response = await chatClient.GetResponseAsync(
                            new ChatMessage(ChatRole.User, request.Message),
                            cancellationToken: cancellationToken).ConfigureAwait(false);
                        await WriteSseEvent(context, "phase", new
                        {
                            phase = "direct_chat",
                            status = "done",
                            label = "Direct Chat",
                            summary = response.Text?[..Math.Min((response.Text ?? "").Length, 200)] ?? ""
                        });
                    }
                    else
                    {
                        await WriteSseEvent(context, "phase", new
                        {
                            phase = "unavailable",
                            status = "error",
                            label = "No backend available",
                            summary = "LivingTreeSystem and IChatClient are not registered"
                        });
                    }

                    await WriteSseEvent(context, "cog-complete", new
                    {
                        phase = "complete",
                        session_id = sessionId,
                        ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0
                    });
                }
            }
            catch (OperationCanceledException)
            {
                await WriteSseEvent(context, "cog-cancelled", new { session_id = sessionId });
            }
            catch (Exception ex)
            {
                var loggerFactory = context.RequestServices.GetService<ILoggerFactory>();
                loggerFactory?.CreateLogger("CognitionStream").LogError(ex, "Cognition stream error");
                await WriteSseEvent(context, "cog-error", new
                {
                    phase = "error",
                    error = ex.Message.Length > 200 ? ex.Message[..200] : ex.Message,
                    session_id = sessionId
                });
            }

            await context.Response.CompleteAsync().ConfigureAwait(false);
        });
    }

    private static async Task WriteSseEvent(HttpContext context, string eventType, object data)
    {
        var json = JsonSerializer.Serialize(data);
        var sse = $"event: {eventType}\ndata: {json}\n\n";
        await context.Response.WriteAsync(sse).ConfigureAwait(false);
        await context.Response.Body.FlushAsync().ConfigureAwait(false);
    }
}

public sealed class CognitionRequest
{
    public string Message { get; set; } = "";
    public string? SessionId { get; set; }
    public Dictionary<string, object>? Options { get; set; }
}
