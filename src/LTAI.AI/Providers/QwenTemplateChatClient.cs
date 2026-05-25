using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace LTAI.AI.Providers;

/// <summary>
/// Decorates IChatClient to apply Qwen-Fixed-Chat-Templates to messages
/// before sending to Qwen models. Fixes role ordering, tool call formatting,
/// and system message placement issues in Qwen's default templates.
/// </summary>
public sealed class QwenTemplateChatClient : IChatClient
{
    private readonly IChatClient _inner;
    private readonly QwenChatFormatter _formatter;

    public QwenTemplateChatClient(IChatClient inner,
        QwenChatFormatter.QwenModelFamily family = QwenChatFormatter.QwenModelFamily.Qwen2_5)
    {
        _inner = inner;
        _formatter = new QwenChatFormatter(family);
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var fixedMessages = _formatter.FixMessages(messages);
        return await _inner.GetResponseAsync(fixedMessages, options, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var fixedMessages = _formatter.FixMessages(messages);
        await foreach (var update in _inner.GetStreamingResponseAsync(fixedMessages, options, cancellationToken))
            yield return update;
    }

    public void Dispose() => (_inner as IDisposable)?.Dispose();

    object? IChatClient.GetService(Type serviceType, object? serviceKey)
        => _inner.GetService(serviceType, serviceKey);
}
