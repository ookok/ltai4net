using System.ClientModel;
using Anthropic;
using Microsoft.Extensions.AI;
using OpenAI;

namespace LTAI.AI;

/// <summary>
/// Factory for creating MAF-compatible <see cref="IChatClient"/> instances
/// against any OpenAI-compatible endpoint (DeepSeek, OpenAI, Groq, SiliconFlow, etc.).
/// </summary>
public static class OpenAIChatClientFactory
{
    public static IChatClient Create(string endpoint, string model, string apiKey)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(endpoint.TrimEnd('/'))
        };
        return new OpenAIClient(new ApiKeyCredential(apiKey), options)
            .GetChatClient(model)
            .AsIChatClient();
    }
}

/// <summary>
/// Factory for creating MAF-compatible <see cref="IChatClient"/> instances
/// against the Anthropic Messages API.
/// </summary>
public static class AnthropicChatClientFactory
{
    public static IChatClient Create(string model, string apiKey, int? defaultMaxTokens = null)
    {
        return new AnthropicClient(new Anthropic.Core.ClientOptions { ApiKey = apiKey })
            .AsIChatClient(model, defaultMaxTokens ?? 4096);
    }
}
