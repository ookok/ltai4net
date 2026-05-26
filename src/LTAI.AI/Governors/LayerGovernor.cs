using LTAI.Core.Interfaces;
using LTAI.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public abstract class LayerGovernor : ILayerGovernor
{
    protected readonly IChatClient LLM;
    protected readonly ILogger Logger;
    private LayerStats _stats;

    public string LayerName { get; }
    public LayerStats Stats => _stats;

    protected LayerGovernor(string layerName, IChatClient llm, ILogger logger)
    {
        LayerName = layerName;
        LLM = llm;
        Logger = logger;
        _stats = new LayerStats { LayerName = layerName };
    }

    public abstract Task<Handshake> ProcessAsync(Handshake incoming, CancellationToken cancellationToken = default);

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
