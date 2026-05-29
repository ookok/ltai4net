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
        var response = await client.GetResponseAsync(prompt, options, cancellationToken).ConfigureAwait(false);
        return response.Text ?? string.Empty;
    }

    public static ChatOptions ToChatOptions(this LLMChatOptions llmOptions)
    {
        var options = new ChatOptions
        {
            ModelId = llmOptions.Model,
            Temperature = llmOptions.Temperature,
            MaxOutputTokens = llmOptions.MaxTokens,
        };

        // Forward structured output schema if set
        if (!string.IsNullOrEmpty(llmOptions.StructuredSchemaJson))
        {
            options.AdditionalProperties = [];
            options.AdditionalProperties["structured_schema"] = llmOptions.StructuredSchemaJson;
        }

        return options;
    }
}
