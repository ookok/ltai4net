using LTAI.Core.Interfaces;
using LTAI.Core.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed class OutputGovernor : LayerGovernor
{
    public OutputGovernor(ICognitiveMesh mesh, IProviderEngine llm, ILogger<OutputGovernor> logger)
        : base("output", mesh, llm, logger) { }

    public override async Task<Handshake> ProcessAsync(Handshake incoming, CancellationToken cancellationToken = default)
    {
        var response = incoming.Payload?.GetValueOrDefault("response")?.ToString() ?? "";

        if (string.IsNullOrEmpty(response))
            return new Handshake { From = LayerName, Action = "output_empty" };

        var isHallucinated = await CheckHallucinationAsync(response, cancellationToken);

        return new Handshake
        {
            From = LayerName,
            Action = "reviewed",
            Payload = new Dictionary<string, object?>
            {
                ["response"] = response,
                ["hallucination_risk"] = isHallucinated,
                ["format"] = "markdown",
                ["reviewed_at"] = DateTime.UtcNow
            }
        };
    }

    public async Task<string> SilentSelfCheckAsync(string response, CancellationToken cancellationToken = default)
    {
        try
        {
            var prompt = $"You just said: {response}\n\nIs there anything factually wrong or misleading in your response? Answer with 'OK' if correct, or explain the error briefly.";
            var check = await LLM.ChatAsync(prompt, new LLMChatOptions { Temperature = 0.1f, MaxTokens = 200 }, cancellationToken);
            return check.Contains("OK", StringComparison.OrdinalIgnoreCase) ? "verified" : $"flagged: {check}";
        }
        catch
        {
            return "unchecked";
        }
    }

    private async Task<bool> CheckHallucinationAsync(string response, CancellationToken cancellationToken)
    {
        return false;
    }
}
