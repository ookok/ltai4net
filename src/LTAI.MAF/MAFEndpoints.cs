using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LTAI.MAF;

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

                var result = await agent.ChatAsync(request.Query, cancellationToken);
                return Results.Json(new { response = result, agent = agent.Name });
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
                    new Microsoft.Extensions.AI.ChatMessage(
                        m.Role == "user" ? Microsoft.Extensions.AI.ChatRole.User : Microsoft.Extensions.AI.ChatRole.Assistant,
                        m.Content ?? ""));

                var response = await agent.GetResponseAsync(chatMessages, cancellationToken);
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
                version = "5.5.0-maf-net10"
            });
        });

        endpoints.MapPost("/api/a2a/message", async (
            HttpContext context,
            A2AHost a2aHost,
            CancellationToken cancellationToken) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync(cancellationToken);
                var request = JsonSerializer.Deserialize<A2ARequest>(body);

                if (request == null)
                    return Results.Json(new { error = "Invalid request" }, statusCode: 400);

                var response = await a2aHost.ProcessAgentMessageAsync(request, cancellationToken);
                return Results.Json(response);
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        endpoints.MapGet("/api/a2a/sessions", (A2AHost a2aHost) =>
        {
            var sessions = a2aHost.GetActiveSessions();
            return Results.Json(new { count = sessions.Count, sessions });
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
