using System.Text;
using System.Text.Json;
using LTAI.Core.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
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
            await context.Response.WriteAsync(JsonSerializer.Serialize(response));
        });

        endpoints.MapPost("/v1/chat/completions", async (HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
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

                var providerEngine = endpoints.ServiceProvider.GetService<IProviderEngine>();
                string responseContent;

                if (providerEngine != null)
                {
                    try
                    {
                        var chatOptions = new LLMChatOptions
                        {
                            Model = model,
                            Temperature = temperature,
                            MaxTokens = maxTokens
                        };
                        responseContent = await providerEngine.ChatAsync(lastUserMsg, chatOptions);
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
                    await context.Response.WriteAsync(JsonSerializer.Serialize(response));
                }
                else
                {
                    context.Response.ContentType = "text/event-stream";
                    context.Response.Headers["Cache-Control"] = "no-cache";

                    if (providerEngine != null)
                    {
                        var chatOptions = new LLMChatOptions
                        {
                            Model = model,
                            Temperature = temperature,
                            MaxTokens = maxTokens
                        };

                        try
                        {
                            await foreach (var chunk in providerEngine.StreamAsync(lastUserMsg, chatOptions))
                            {
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
                                            delta = new { role = "assistant", content = chunk },
                                            finish_reason = (string?)null
                                        }
                                    }
                                };
                                await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(sseChunk)}\n\n");
                                await context.Response.Body.FlushAsync();
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
                            await context.Response.Body.FlushAsync();
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
                        await context.Response.Body.FlushAsync();
                    }

                    await context.Response.WriteAsync("data: [DONE]\n\n");
                    await context.Response.Body.FlushAsync();
                }
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = 500;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
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
