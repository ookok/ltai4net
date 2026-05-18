using LTAI.Core.Models;

namespace LTAI.Core.Interfaces;

public interface ICognitiveMesh
{
    Task RegisterAsync(ILayerGovernor governor, CancellationToken cancellationToken = default);
    Task UnregisterAsync(string layerName, CancellationToken cancellationToken = default);

    Task<Handshake> SendAsync(Handshake handshake, CancellationToken cancellationToken = default);
    Task BroadcastAsync(Handshake handshake, CancellationToken cancellationToken = default);

    Handshake? GetWorldState(string key);
    void SetWorldState(string key, Handshake state);
    bool HasPending(string replyTo);
}
