using LTAI.Core.Interfaces;
using LTAI.Core.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed class CommunicationGovernor : LayerGovernor
{
    public CommunicationGovernor(ICognitiveMesh mesh, IProviderEngine llm, ILogger<CommunicationGovernor> logger)
        : base("communication", mesh, llm, logger) { }

    public override Task<Handshake> ProcessAsync(Handshake incoming, CancellationToken cancellationToken = default)
    {
        var channel = incoming.Payload?.GetValueOrDefault("channel")?.ToString() ?? "web";
        var message = incoming.Payload?.GetValueOrDefault("message")?.ToString() ?? "";

        Logger.LogInformation("Routing to channel: {Channel}", channel);

        return Task.FromResult(new Handshake
        {
            From = LayerName,
            Action = "delivered",
            Payload = new Dictionary<string, object?>
            {
                ["channel"] = channel,
                ["message"] = message,
                ["delivered_at"] = DateTime.UtcNow
            }
        });
    }
}
