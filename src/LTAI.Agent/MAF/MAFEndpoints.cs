using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace LTAI.Agent;

public static class MAFEndpoints
{
    public static void MapMAFEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapDevUIEndpoints();
        endpoints.MapPost("/api/maf/chat", async (
            HttpContext context,
            LTAIAgent agent,
            CancellationToken cancellationToken) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync(cancellationToken);
                var request = JsonSerializer.Deserialize<MAFChatRequest>(body);

                if (request == null || string.IsNullOrWhiteSpace(request.Query))
                    return Results.Json(new { error = "Query is required" }, statusCode: 400);

                var response = await agent.RunAsync(request.Query, null, null, cancellationToken);
                return Results.Json(new { response = response.Text, agent = agent.Name });
            }
            catch (OperationCanceledException)
            {
                return Results.Json(new { error = "Request cancelled" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        endpoints.MapPost("/api/maf/messages", async (
            HttpContext context,
            LTAIAgent agent,
            CancellationToken cancellationToken) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync(cancellationToken);
                var request = JsonSerializer.Deserialize<MAFMessageRequest>(body);

                if (request?.Messages == null || request.Messages.Count == 0)
                    return Results.Json(new { error = "Messages required" }, statusCode: 400);

                var chatMessages = request.Messages.Select(m =>
                    new ChatMessage(
                        m.Role == "user" ? ChatRole.User : ChatRole.Assistant,
                        m.Content ?? ""));

                var response = await agent.RunAsync(chatMessages, null, null, cancellationToken);
                return Results.Json(new { response = response.Text, agent = agent.Name });
            }
            catch (OperationCanceledException)
            {
                return Results.Json(new { error = "Request cancelled" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        endpoints.MapGet("/api/maf/status", (LTAIAgent agent) =>
        {
            return Results.Json(new
            {
                agent = agent.Name,
                description = agent.Description,
                protocol = "MAF-compatible",
                version = "7.0.0-maf-net10"
            });
        });

        endpoints.MapPost("/api/maf/stream", async (
            HttpContext context,
            LTAIAgent agent,
            CancellationToken cancellationToken) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync(cancellationToken);
                var request = JsonSerializer.Deserialize<MAFChatRequest>(body);

                if (request == null || string.IsNullOrWhiteSpace(request.Query))
                {
                    context.Response.StatusCode = 400;
                    return;
                }

                context.Response.ContentType = "text/event-stream";
                context.Response.Headers["Cache-Control"] = "no-cache";
                context.Response.Headers["Connection"] = "keep-alive";

                await foreach (var update in agent.RunStreamingAsync(request.Query, null, null, cancellationToken))
                {
                    if (!string.IsNullOrWhiteSpace(update.Text))
                    {
                        await context.Response.WriteAsync($"data: {System.Text.Json.JsonSerializer.Serialize(new { text = update.Text, role = "assistant" })}\n\n", cancellationToken);
                        await context.Response.Body.FlushAsync(cancellationToken);
                    }
                }

                await context.Response.WriteAsync("data: [DONE]\n\n", cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = 500;
                    await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new { error = ex.Message }));
                }
            }
        });
    }
}

public sealed record MAFChatRequest
{
    public string Query { get; init; } = string.Empty;
}

public sealed record MAFMessageRequest
{
    public List<MAFMessage> Messages { get; init; } = new();
}

public sealed record MAFMessage
{
    public string Role { get; init; } = "user";
    public string? Content { get; init; }
}
