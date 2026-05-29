using LTAI.Core.Models;

namespace LTAI.Core.Interfaces;

/// <summary>
/// Pipeline layer interface — each layer (Input/Context/Routing/L0/L1/L2/Output)
/// implements this to process a Handshake in the governance pipeline.
/// Pipe-and-filter architecture: ProcessAsync receives a Handshake, transforms it,
/// and passes it to the next layer.
/// Callers: LTAI.AI.Governors.GovernorSet, LTAI.AI.Governors.LivingTreeSystem.
/// </summary>
public interface ILayerGovernor
{
    string LayerName { get; }
    LayerStats Stats { get; }

    Task<Handshake> ProcessAsync(Handshake incoming, CancellationToken cancellationToken = default);
}
