using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using MEAIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MEAIChatRole = Microsoft.Extensions.AI.ChatRole;
using OAIChatMessage = OpenAI.Chat.ChatMessage;
using OAIChatCompletionOptions = OpenAI.Chat.ChatCompletionOptions;
using OAIChatTool = OpenAI.Chat.ChatTool;
using OAIChatToolCall = OpenAI.Chat.ChatToolCall;

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
        var openAiMessages = ToOpenAIMessages(messages).ToList();
        var openAiOptions = ToOpenAIOptions(options);
        var result = await _chatClient.CompleteChatAsync(openAiMessages, openAiOptions, cancellationToken);
        var content = string.Join("", result.Value.Content.Select(c => c.Text ?? ""));
        var toolCalls = result.Value.ToolCalls?.Select(tc =>
        {
            var args = new Dictionary<string, object?>();
            if (tc.FunctionArguments != null)
            {
                try { args = tc.FunctionArguments.ToObjectFromJson<Dictionary<string, object?>>() ?? new(); }
                catch { }
            }
            return new FunctionCallContent(tc.Id, tc.FunctionName, args);
        }).ToList();
        var contents = new List<AIContent>();
        if (!string.IsNullOrEmpty(content)) contents.Add(new TextContent(content));
        if (toolCalls != null) contents.AddRange(toolCalls.Cast<AIContent>());
        var msg = new MEAIChatMessage(MEAIChatRole.Assistant, contents);
        var usage = result.Value.Usage;
        return new ChatResponse(msg)
        {
            ResponseId = result.Value.Id,
            Usage = usage != null ? new UsageDetails { InputTokenCount = usage.InputTokenCount, OutputTokenCount = usage.OutputTokenCount, TotalTokenCount = usage.TotalTokenCount } : null
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<MEAIChatMessage> messages, ChatOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var openAiMessages = ToOpenAIMessages(messages).ToList();
        var openAiOptions = ToOpenAIOptions(options);
        var updates = _chatClient.CompleteChatStreamingAsync(openAiMessages, openAiOptions, cancellationToken);

        await foreach (var update in updates.WithCancellation(cancellationToken))
        {
            if (TryExtractReasoning(update, out var reasoning))
            {
                yield return new ChatResponseUpdate(MEAIChatRole.Assistant,
                    contents: new List<AIContent> { new TextReasoningContent(reasoning) })
                { ResponseId = update.CompletionId };
            }

            foreach (var part in update.ContentUpdate)
            {
                if (!string.IsNullOrEmpty(part.Text))
                    yield return new ChatResponseUpdate(MEAIChatRole.Assistant, part.Text)
                    { ResponseId = update.CompletionId };
            }

            foreach (var toolCall in update.ToolCallUpdates)
            {
                var args = new Dictionary<string, object?>();
                if (toolCall.FunctionArgumentsUpdate != null)
                {
                    try { args = toolCall.FunctionArgumentsUpdate.ToObjectFromJson<Dictionary<string, object?>>() ?? new(); }
                    catch { }
                }
                yield return new ChatResponseUpdate(MEAIChatRole.Assistant,
                    contents: new List<AIContent> { new FunctionCallContent(toolCall.ToolCallId, toolCall.FunctionName, args) })
                { ResponseId = update.CompletionId };
            }
        }
    }

    private static bool TryExtractReasoning(OpenAI.Chat.StreamingChatCompletionUpdate update, out string reasoning)
    {
        reasoning = "";
        try
        {
            var json = System.BinaryData.FromObjectAsJson(update).ToString();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("Choices", out var choices) && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("Delta", out var delta)
                && delta.TryGetProperty("reasoning_content", out var rc)
                && rc.ValueKind == JsonValueKind.String)
            {
                reasoning = rc.GetString() ?? "";
                return reasoning.Length > 0;
            }
        }
        catch { }
        return false;
    }

    private static System.Collections.Generic.IDictionary<string, System.BinaryData>? GetRawData(object obj)
    {
        var prop = obj.GetType().GetProperty("SerializedAdditionalRawData",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return prop?.GetValue(obj) as System.Collections.Generic.IDictionary<string, System.BinaryData>;
    }

    object? IChatClient.GetService(Type serviceType, object? serviceKey) =>
        serviceType == typeof(ChatClientMetadata) ? Metadata : null;

    void IDisposable.Dispose() { }

    private static IEnumerable<OAIChatMessage> ToOpenAIMessages(IEnumerable<MEAIChatMessage> messages)
    {
        foreach (var m in messages)
        {
            if (m.Role == MEAIChatRole.User)
            {
                var funcResults = m.Contents.OfType<FunctionResultContent>().ToList();
                if (funcResults.Count > 0)
                {
                    foreach (var fr in funcResults)
                        yield return OAIChatMessage.CreateToolMessage(fr.CallId, fr.Result?.ToString() ?? "");
                }
                else
                {
                    yield return OAIChatMessage.CreateUserMessage(m.Text ?? "");
                }
            }
            else if (m.Role == MEAIChatRole.Assistant)
            {
                var toolCalls = m.Contents.OfType<FunctionCallContent>().Select(fc =>
                    OAIChatToolCall.CreateFunctionToolCall(fc.CallId, fc.Name,
                        System.BinaryData.FromObjectAsJson(fc.Arguments ?? new Dictionary<string, object?>()))).ToArray();
                if (toolCalls.Length > 0)
                    yield return OAIChatMessage.CreateAssistantMessage(toolCalls);
                else
                    yield return OAIChatMessage.CreateAssistantMessage(m.Text ?? "");
            }
            else if (m.Role == MEAIChatRole.System)
            {
                yield return OAIChatMessage.CreateSystemMessage(m.Text ?? "");
            }
            else
            {
                yield return OAIChatMessage.CreateUserMessage(m.Text ?? "");
            }
        }
    }

    private static OAIChatCompletionOptions ToOpenAIOptions(ChatOptions? options)
    {
        var oaiOptions = new OAIChatCompletionOptions
        {
            Temperature = options?.Temperature,
            MaxOutputTokenCount = options?.MaxOutputTokens
        };

        if (options?.Tools is { Count: > 0 })
        {
            foreach (var t in options.Tools)
            {
                var schemaData = t is AIFunction f && f.JsonSchema is { } s
                    ? System.BinaryData.FromObjectAsJson(s)
                    : System.BinaryData.FromString("{}");
                oaiOptions.Tools.Add(OAIChatTool.CreateFunctionTool(t.Name, t.Description ?? "", schemaData));
            }
        }

        if (options?.Reasoning is { Effort: not ReasoningEffort.None })
        {
            var raw = GetRawData(oaiOptions);
            if (raw != null)
                raw["thinking"] = System.BinaryData.FromString(JsonSerializer.Serialize(new { type = "enabled" }));
        }

        return oaiOptions;
    }
}
