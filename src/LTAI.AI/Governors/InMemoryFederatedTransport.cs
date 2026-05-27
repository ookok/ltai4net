using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace LTAI.AI.Governors;

public sealed class InMemoryFederatedTransport : IFederatedTransport
{
    private static readonly ConcurrentQueue<(string Type, string Payload, string SourceId)> _messages = new();
    private static readonly ConcurrentDictionary<string, InMemoryFederatedTransport> _peers = new();

    public string PeerId { get; }

    public InMemoryFederatedTransport(string? peerId = null)
    {
        PeerId = peerId ?? Guid.NewGuid().ToString("N")[..8];
        _peers[PeerId] = this;
    }

    public Task SendMessageAsync(string type, string payload, CancellationToken ct = default)
    {
        _messages.Enqueue((type, payload, PeerId));
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<(string Type, string Payload, string SourceId)> ReceiveMessagesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            while (_messages.TryDequeue(out var msg))
                yield return msg;

            await Task.Delay(1000, ct).ConfigureAwait(false);
        }
    }
}
