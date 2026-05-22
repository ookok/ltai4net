using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using MEAIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MEAIChatRole = Microsoft.Extensions.AI.ChatRole;
using OAIChatMessage = OpenAI.Chat.ChatMessage;
using OAIChatRole = OpenAI.Chat.ChatMessageRole;
using OAIChatCompletionOptions = OpenAI.Chat.ChatCompletionOptions;

namespace LTAI.AI.Providers;

public sealed class OpenAIProviderChatClient : IChatClient
{
    private readonly OpenAI.Chat.ChatClient _chatClient;
    private readonly string _model;

    public OpenAIProviderChatClient(OpenAI.Chat.ChatClient chatClient)
    {
        _chatClient = chatClient;
        _model = typeof(OpenAI.Chat.ChatClient).GetProperty("Model")?.GetValue(chatClient)?.ToString() ?? "";
    }

    public ChatClientMetadata? Metadata => new(_model);

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<MEAIChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken)
    {
        var openAiMessages = ToOpenAIMessages(messages);
        var openAiOptions = ToOpenAIOptions(options);

        var result = await _chatClient.CompleteChatAsync(openAiMessages, openAiOptions, cancellationToken);
        var content = string.Join("", result.Value.Content.Select(c => c.Text ?? ""));
        var usage = result.Value.Usage;

        return new ChatResponse(new MEAIChatMessage(MEAIChatRole.Assistant, content))
        {
            ResponseId = result.Value.Id,
            Usage = usage != null ? new UsageDetails
            {
                InputTokenCount = usage.InputTokenCount,
                OutputTokenCount = usage.OutputTokenCount,
                TotalTokenCount = usage.TotalTokenCount
            } : null
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<MEAIChatMessage> messages, ChatOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var openAiMessages = ToOpenAIMessages(messages);
        var openAiOptions = ToOpenAIOptions(options);

        var updates = _chatClient.CompleteChatStreamingAsync(openAiMessages, openAiOptions, cancellationToken);
        await foreach (var update in updates.WithCancellation(cancellationToken))
        {
            foreach (var part in update.ContentUpdate)
            {
                if (!string.IsNullOrEmpty(part.Text))
                    yield return new ChatResponseUpdate(MEAIChatRole.Assistant, part.Text)
                    {
                        ResponseId = update.CompletionId
                    };
            }
        }
    }

    object? IChatClient.GetService(Type serviceType, object? serviceKey) =>
        serviceType == typeof(ChatClientMetadata) ? Metadata : null;

    void IDisposable.Dispose() { }

    private static IEnumerable<OAIChatMessage> ToOpenAIMessages(IEnumerable<MEAIChatMessage> messages)
    {
        foreach (var m in messages)
        {
            yield return m.Role == MEAIChatRole.User
                ? OAIChatMessage.CreateUserMessage(m.Text ?? "")
                : m.Role == MEAIChatRole.Assistant
                    ? OAIChatMessage.CreateAssistantMessage(m.Text ?? "")
                    : m.Role == MEAIChatRole.System
                        ? OAIChatMessage.CreateSystemMessage(m.Text ?? "")
                        : OAIChatMessage.CreateUserMessage(m.Text ?? "");
        }
    }

    private static OAIChatCompletionOptions ToOpenAIOptions(ChatOptions? options)
    {
        return new OAIChatCompletionOptions
        {
            Temperature = options?.Temperature,
            MaxOutputTokenCount = options?.MaxOutputTokens
        };
    }
}
