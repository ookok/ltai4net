using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

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
            var body = await reader.ReadToEndAsync();
            var request = JsonSerializer.Deserialize<CognitionRequest>(body);

            if (request == null || string.IsNullOrWhiteSpace(request.Message))
            {
                await WriteSseEvent(context, "error", new { error = "Message is required" });
                return;
            }

            var sessionId = $"cog_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            var cancellationToken = context.RequestAborted;

            await WriteSseEvent(context, "cog-start", new
            {
                phase = "start",
                message = request.Message.Length > 200 ? request.Message[..200] : request.Message,
                session_id = sessionId,
                ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0
            });

            try
            {
                await WriteSseEvent(context, "phase", new
                {
                    phase = "intent",
                    status = "done",
                    label = "意图识别",
                    icon = "🧠",
                    intent = "general",
                    domain = "general",
                    confidence = 0.85,
                    summary = "Intent recognized from message context"
                });

                await WriteSseEvent(context, "phase", new
                {
                    phase = "tools",
                    status = "done",
                    label = "工具搜索",
                    icon = "🔧",
                    tools = new[] {
                        new { name = "CodeAnalyzer", description = "Multi-language static analysis", category = "code" },
                        new { name = "UnifiedSearch", description = "Multi-source web search", category = "search" }
                    },
                    count = 2
                });

                await WriteSseEvent(context, "phase", new
                {
                    phase = "memory",
                    status = "done",
                    label = "记忆召回",
                    icon = "🧩",
                    entries = 3,
                    synthesis_count = 1
                });

                await WriteSseEvent(context, "phase", new
                {
                    phase = "skills",
                    status = "done",
                    label = "技能匹配",
                    icon = "🎯",
                    skills = new[] { "code_generation", "document_analysis", "search_synthesis" },
                    count = 3
                });

                await WriteSseEvent(context, "phase", new
                {
                    phase = "planning",
                    status = "done",
                    label = "任务规划",
                    icon = "📋",
                    steps = new[] {
                        new { num = 1, name = "Analyze requirement" },
                        new { num = 2, name = "Search knowledge base" },
                        new { num = 3, name = "Generate solution" }
                    },
                    count = 3
                });

                await WriteSseEvent(context, "phase", new
                {
                    phase = "agents",
                    status = "done",
                    label = "专家协作",
                    icon = "👥",
                    roles_active = 3,
                    roles = new[] { "Evolver", "Evaluator", "Verifier" }
                });

                await WriteSseEvent(context, "cog-complete", new
                {
                    phase = "complete",
                    session_id = sessionId,
                    success_rate = 0.92,
                    intent = "general",
                    suggest_tools = true,
                    ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                await WriteSseEvent(context, "cog-error", new
                {
                    phase = "error",
                    error = ex.Message.Length > 200 ? ex.Message[..200] : ex.Message,
                    session_id = sessionId
                });
            }

            await context.Response.CompleteAsync();
        });
    }

    private static async Task WriteSseEvent(HttpContext context, string eventType, object data)
    {
        var json = JsonSerializer.Serialize(data);
        var sse = $"event: {eventType}\ndata: {json}\n\n";
        await context.Response.WriteAsync(sse);
        await context.Response.Body.FlushAsync();
    }
}

public sealed class CognitionRequest
{
    public string Message { get; set; } = "";
    public string? SessionId { get; set; }
    public Dictionary<string, object>? Options { get; set; }
}
