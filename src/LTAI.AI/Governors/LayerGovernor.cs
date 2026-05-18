using LTAI.Core.Interfaces;
using LTAI.Core.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public abstract class LayerGovernor : ILayerGovernor
{
    protected readonly ICognitiveMesh Mesh;
    protected readonly IProviderEngine LLM;
    protected readonly ILogger Logger;
    private LayerStats _stats;

    public string LayerName { get; }
    public LayerStats Stats => _stats;

    protected LayerGovernor(string layerName, ICognitiveMesh mesh, IProviderEngine llm, ILogger logger)
    {
        LayerName = layerName;
        Mesh = mesh;
        LLM = llm;
        Logger = logger;
        _stats = new LayerStats { LayerName = layerName };
    }

    public abstract Task<Handshake> ProcessAsync(Handshake incoming, CancellationToken cancellationToken = default);

    public async Task SendAsync(Handshake handshake, CancellationToken cancellationToken = default)
    {
        handshake = handshake with { From = LayerName };
        await Mesh.SendAsync(handshake, cancellationToken);
        _stats.MessagesSent++;
        _stats.LastActive = DateTime.UtcNow;
    }

    protected Handshake CreateHandshake(string to, string action, Dictionary<string, object?>? payload = null,
        HandshakePriority priority = HandshakePriority.Normal)
    {
        return new Handshake
        {
            From = LayerName,
            To = to,
            Action = action,
            Payload = payload,
            Priority = priority
        };
    }

    protected Handshake ErrorResponse(string error)
    {
        return new Handshake
        {
            From = LayerName,
            Action = "error",
            Payload = new Dictionary<string, object?> { ["error"] = error }
        };
    }
}
