using LTAI.Core.Models;

namespace LTAI.Core.Interfaces;

public interface ILayerGovernor
{
    string LayerName { get; }
    LayerStats Stats { get; }

    Task<Handshake> ProcessAsync(Handshake incoming, CancellationToken cancellationToken = default);
    Task SendAsync(Handshake handshake, CancellationToken cancellationToken = default);
}
