using LTAI.Core.Configuration;
using LTAI.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.AI.Governors;

public sealed class RoutingGovernor : LayerGovernor
{
    private readonly IOptions<LTAIOptions> _options;

    public RoutingGovernor(IChatClient llm, ILogger<RoutingGovernor> logger, IOptions<LTAIOptions> options)
        : base("routing", llm, logger)
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
            var l1 = aiConfig.L1;
            model = l1.Model;
            temperature = l1.Temperature ?? aiConfig.DefaultTemperature;
        }
        else
        {
            var l2 = aiConfig.L2;
            model = l2.Model;
            temperature = l2.Temperature ?? aiConfig.DefaultTemperature;
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
