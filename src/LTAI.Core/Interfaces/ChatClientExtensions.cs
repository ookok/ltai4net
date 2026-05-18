using Microsoft.Extensions.AI;

namespace LTAI.Core.Interfaces;

public static class ChatClientExtensions
{
    public static async Task<string> CompleteAsync(
        this IChatClient client,
        string prompt,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await client.GetResponseAsync(prompt, options, cancellationToken);
        return response.Text ?? string.Empty;
    }

    public static ChatOptions ToChatOptions(this LLMChatOptions llmOptions)
    {
        return new ChatOptions
        {
            ModelId = llmOptions.Model,
            Temperature = llmOptions.Temperature,
            MaxOutputTokens = llmOptions.MaxTokens,
        };
    }
}
