using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace LTAI.Tests;

public sealed class FakeChatClient : IChatClient
{
    private readonly Dictionary<string, Func<string, string>> _routes = new();

    public FakeChatClient AddRoute(string trigger, Func<string, string> response)
    {
        _routes[trigger] = response;
        return this;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        var msgList = messages.ToList();
        var lastMsg = msgList.LastOrDefault()?.Text ?? "";
        foreach (var (trigger, handler) in _routes)
        {
            if (lastMsg.Contains(trigger, StringComparison.OrdinalIgnoreCase))
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, handler(lastMsg)));
        }
        await Task.CompletedTask;
        return new ChatResponse(new ChatMessage(ChatRole.Assistant,
            $"FAKE: received '{lastMsg[..Math.Min(lastMsg.Length, 50)]}'"));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var msgList = messages.ToList();
        var lastMsg = msgList.LastOrDefault()?.Text ?? "";
        foreach (var (trigger, handler) in _routes)
        {
            if (lastMsg.Contains(trigger, StringComparison.OrdinalIgnoreCase))
            {
                var text = handler(lastMsg);
                foreach (var chunk in text.Split(' '))
                    yield return new ChatResponseUpdate(ChatRole.Assistant, chunk + " ");
                yield break;
            }
        }
        yield return new ChatResponseUpdate(ChatRole.Assistant, "FAKE");
        await Task.CompletedTask;
    }

    public void Dispose() { }
    public object? GetService(Type t, object? k = null) => null;
}
