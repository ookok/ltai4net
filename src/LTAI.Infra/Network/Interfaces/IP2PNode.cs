using LTAI.Infra.Network.Models;

namespace LTAI.Infra.Network.Interfaces;

public interface IP2PNode
{
    string PeerId { get; }
    int LocalPort { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task SendMessageAsync(NetworkMessage message, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PeerInfo>> GetKnownPeersAsync(CancellationToken cancellationToken = default);

    IAsyncEnumerable<NetworkMessage> ReceiveMessagesAsync(CancellationToken cancellationToken = default);
}
