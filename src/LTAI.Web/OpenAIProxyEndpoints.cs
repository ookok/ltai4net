using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace LTAI.Web;

public static class OpenAIProxyEndpoints
{
    public static void MapOpenAIProxyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/models", async (HttpContext context) =>
        {
            context.Response.ContentType = "application/json";
            var response = new
            {
                @object = "list",
                data = new[]
                {
                    new { id = "ltai-fast", @object = "model", owned_by = "ltai" },
                    new { id = "ltai-deep", @object = "model", owned_by = "ltai" },
                    new { id = "ltai-reasoning", @object = "model", owned_by = "ltai" }
                }
            };
            await context.Response.WriteAsync(JsonSerializer.Serialize(response)).ConfigureAwait(false);
        });

        endpoints.MapPost("/v1/chat/completions", async (HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync().ConfigureAwait(false);
                var request = JsonSerializer.Deserialize<ChatCompletionRequest>(body);

                if (request == null || request.Messages.Count == 0)
                {
                    context.Response.StatusCode = 400;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "messages are required" }));
                    return;
                }

                var lastUserMsg = request.Messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";
                var model = request.Model ?? "ltai-fast";
                var temperature = request.Temperature ?? 0.7f;
                var maxTokens = request.MaxTokens ?? 4096;
                var stream = request.Stream;

                var chatClient = endpoints.ServiceProvider.GetService<IChatClient>();
                string responseContent;

                if (chatClient != null)
                {
                    try
                    {
                        var chatOptions = new ChatOptions
                        {
                            ModelId = model,
                            Temperature = temperature,
                            MaxOutputTokens = maxTokens
                        };
                        var messages = new List<ChatMessage> { new(ChatRole.User, lastUserMsg) };
                        var response = await chatClient.GetResponseAsync(messages, chatOptions).ConfigureAwait(false);
                        responseContent = response.Text ?? "";
                    }
                    catch
                    {
                        responseContent = $"[LTAI response: {lastUserMsg}]";
                    }
                }
                else
                {
                    responseContent = $"[LTAI response: {lastUserMsg}]";
                }

                var responseId = $"chatcmpl-{Guid.NewGuid():N}";
                var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                if (!stream)
                {
                    var response = new
                    {
                        id = responseId,
                        @object = "chat.completion",
                        created,
                        model,
                        choices = new[]
                        {
                            new
                            {
                                index = 0,
                                message = new { role = "assistant", content = responseContent },
                                finish_reason = "stop"
                            }
                        }
                    };

                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(response)).ConfigureAwait(false);
                }
                else
                {
                    context.Response.ContentType = "text/event-stream";
                    context.Response.Headers["Cache-Control"] = "no-cache";

                    if (chatClient != null)
                    {
                        var chatOptions = new ChatOptions
                        {
                            ModelId = model,
                            Temperature = temperature,
                            MaxOutputTokens = maxTokens
                        };
                        var messages = new List<ChatMessage> { new(ChatRole.User, lastUserMsg) };

                        try
                        {
                            await foreach (var update in chatClient.GetStreamingResponseAsync(messages, chatOptions))
                            {
                                var chunkText = update.Text ?? "";
                                var sseChunk = new
                                {
                                    id = responseId,
                                    @object = "chat.completion.chunk",
                                    created,
                                    model,
                                    choices = new[]
                                    {
                                        new
                                        {
                                            index = 0,
                                            delta = new { role = "assistant", content = chunkText },
                                            finish_reason = (string?)null
                                        }
                                    }
                                };
                                await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(sseChunk)}\n\n");
                                await context.Response.Body.FlushAsync().ConfigureAwait(false);
                            }
                        }
                        catch
                        {
                            var fallbackChunk = new
                            {
                                id = responseId,
                                @object = "chat.completion.chunk",
                                created,
                                model,
                                choices = new[]
                                {
                                    new
                                    {
                                        index = 0,
                                        delta = new { role = "assistant", content = responseContent },
                                        finish_reason = "stop"
                                    }
                                }
                            };
                            await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(fallbackChunk)}\n\n");
                            await context.Response.Body.FlushAsync().ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        var echoChunk = new
                        {
                            id = responseId,
                            @object = "chat.completion.chunk",
                            created,
                            model,
                            choices = new[]
                            {
                                new
                                {
                                    index = 0,
                                    delta = new { role = "assistant", content = responseContent },
                                    finish_reason = "stop"
                                }
                            }
                        };
                        await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(echoChunk)}\n\n");
                        await context.Response.Body.FlushAsync().ConfigureAwait(false);
                    }

                    await context.Response.WriteAsync("data: [DONE]\n\n");
                    await context.Response.Body.FlushAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = 500;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message })).ConfigureAwait(false);
            }
        });
    }
}

public sealed record ChatCompletionRequest
{
    public string? Model { get; init; }
    public List<ChatMsg> Messages { get; init; } = new();
    public float? Temperature { get; init; } = 0.7f;
    public int? MaxTokens { get; init; } = 4096;
    public bool Stream { get; init; }
}

public sealed record ChatMsg
{
    public string Role { get; init; } = "user";
    public string Content { get; init; } = string.Empty;
}
