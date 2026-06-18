using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace LTAI.AI;

/// <summary>
/// Wraps an <see cref="IChatClient"/> to inject thinking/reasoning parameters
/// for models that support it (Qwen3+, DeepSeek R1, etc.).
/// </summary>
public sealed class ThinkingChatClient : IChatClient
{
    private readonly IChatClient _inner;
    private readonly bool _enableThinking;
    private readonly bool _thoughtInContent;

    public ThinkingChatClient(IChatClient inner, bool enableThinking, bool thoughtInContent)
    {
        _inner = inner;
        _enableThinking = enableThinking;
        _thoughtInContent = thoughtInContent;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        InjectThinkingOptions(options);
        return _inner.GetResponseAsync(messages, options, ct);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        InjectThinkingOptions(options);
        await foreach (var update in _inner.GetStreamingResponseAsync(messages, options, ct).ConfigureAwait(false))
            yield return update;
    }

    public void Dispose() => _inner.Dispose();

    private static void InjectThinkingOptions(ChatOptions? options)
    {
        if (options == null) return;
        options.AdditionalProperties ??= [];
        options.AdditionalProperties["enable_thinking"] = true;
        options.AdditionalProperties["thought_in_content"] = true;
    }

    object? IChatClient.GetService(Type serviceType, object? serviceKey) =>
        _inner.GetService(serviceType, serviceKey);
}
