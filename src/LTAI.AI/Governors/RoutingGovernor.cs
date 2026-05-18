using LTAI.Core.Configuration;
using LTAI.Core.Interfaces;
using LTAI.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.AI.Governors;

public sealed class RoutingGovernor : LayerGovernor
{
    private readonly IOptions<LTAIOptions> _options;

    public RoutingGovernor(ICognitiveMesh mesh, IChatClient llm, ILogger<RoutingGovernor> logger, IOptions<LTAIOptions> options)
        : base("routing", mesh, llm, logger)
    {
        _options = options;
    }

    public override Task<Handshake> ProcessAsync(Handshake incoming, CancellationToken cancellationToken = default)
    {
        var label = incoming.Payload?.GetValueOrDefault("label")?.ToString() ?? "deep";
        var aiConfig = _options.Value.AI;

        string model;
        float temperature;

        if (label == "reflex")
        {
            return Task.FromResult(new Handshake
            {
                From = LayerName,
                Action = "provider_selected",
                Payload = new Dictionary<string, object?> { ["provider"] = "none" }
            });
        }

        if (label == "fast")
        {
            model = aiConfig.FastModel;
            temperature = 0.3f;
        }
        else
        {
            model = aiConfig.DeepModel;
            temperature = 0.3f;
        }

        Logger.LogInformation("Provider elected: {Model} for label: {Label}", model, label);

        return Task.FromResult(new Handshake
        {
            From = LayerName,
            Action = "provider_selected",
            Payload = new Dictionary<string, object?>
            {
                ["model"] = model,
                ["temperature"] = temperature
            }
        });
    }
}
