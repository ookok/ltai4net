using System.Runtime.CompilerServices;
using LTAI.AI.Governors;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent;

public sealed class LivingTreeChatClient : IChatClient
{
    private readonly LivingTreeSystem _system;
    private readonly ILogger<LivingTreeChatClient>? _logger;

    public LivingTreeChatClient(LivingTreeSystem system, ILogger<LivingTreeChatClient>? logger = null)
    {
        _system = system;
        _logger = logger;
    }

    public ChatClientMetadata? Metadata =>
        new("LivingTreeSystem", new Uri("https://github.com/ltai-org/ltai4net"));

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var query = ExtractQuery(messages);
        var result = await _system.ChatAsync(query, cancellationToken).ConfigureAwait(false);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, result));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var query = ExtractQuery(messages);
        await foreach (var chunk in _system.StreamChatAsync(query, cancellationToken))
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
        }
    }

    object? IChatClient.GetService(Type serviceType, object? serviceKey) =>
        serviceType == typeof(ChatClientMetadata) ? Metadata : null;

    void IDisposable.Dispose() { }

    private static string ExtractQuery(IEnumerable<ChatMessage> messages)
    {
        var userMessages = messages.Where(m => m.Role == ChatRole.User).Select(m => m.Text ?? "").ToList();
        return userMessages.Count > 0 ? string.Join("\n", userMessages) : messages.LastOrDefault()?.Text ?? "";
    }
}
