using LTAI.Core.Interfaces;
using LTAI.Core.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed class EvolutionGovernor : LayerGovernor
{
    private readonly HashSet<string> _antiPatterns = new()
    {
        "circular_dependency",
        "god_module",
        "deep_inheritance",
        "tight_coupling"
    };

    public EvolutionGovernor(ICognitiveMesh mesh, IProviderEngine llm, ILogger<EvolutionGovernor> logger)
        : base("evolution", mesh, llm, logger) { }

    public override async Task<Handshake> ProcessAsync(Handshake incoming, CancellationToken cancellationToken = default)
    {
        var codeChange = incoming.Payload?.GetValueOrDefault("change")?.ToString() ?? "";

        var detectedPatterns = await DetectAntiPatternsAsync(codeChange, cancellationToken);

        return new Handshake
        {
            From = LayerName,
            Action = "evolution_analysis",
            Payload = new Dictionary<string, object?>
            {
                ["patterns_detected"] = detectedPatterns,
                ["suggestion"] = detectedPatterns.Length > 0 ? "Review recommended" : "No issues detected"
            }
        };
    }

    private async Task<string[]> DetectAntiPatternsAsync(string code, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(code))
            return Array.Empty<string>();

        var detected = new List<string>();
        foreach (var pattern in _antiPatterns)
        {
            if (code.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                detected.Add(pattern);
        }
        return detected.ToArray();
    }
}
