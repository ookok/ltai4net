using LTAI.AI.Utilities;
using LTAI.Core.Interfaces;
using LTAI.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed class EvolutionGovernor : LayerGovernor
{
    public EvolutionGovernor(ICognitiveMesh mesh, IChatClient llm, ILogger<EvolutionGovernor> logger)
        : base("evolution", mesh, llm, logger) { }

    public override Task<Handshake> ProcessAsync(Handshake incoming, CancellationToken cancellationToken = default)
    {
        var codeChange = incoming.Payload?.GetValueOrDefault("change")?.ToString() ?? "";

        var detectedPatterns = DetectAntiPatterns(codeChange);

        return Task.FromResult(new Handshake
        {
            From = LayerName,
            Action = "evolution_analysis",
            Payload = new Dictionary<string, object?>
            {
                ["patterns_detected"] = detectedPatterns,
                ["suggestion"] = detectedPatterns.Length > 0 ? "Review recommended" : "No issues detected"
            }
        });
    }

    private static string[] DetectAntiPatterns(string code) =>
        GovernorUtilities.DetectAntiPatterns(code);
}
