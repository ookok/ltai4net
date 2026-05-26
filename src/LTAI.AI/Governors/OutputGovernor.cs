using LTAI.AI.Utilities;
using LTAI.Core.Interfaces;
using LTAI.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed class OutputGovernor : LayerGovernor
{
    public OutputGovernor(IChatClient llm, ILogger<OutputGovernor> logger)
        : base("output", llm, logger) { }

    public override Task<Handshake> ProcessAsync(Handshake incoming, CancellationToken cancellationToken = default)
    {
        var response = incoming.Payload?.GetValueOrDefault("response")?.ToString() ?? "";

        if (string.IsNullOrEmpty(response))
            return Task.FromResult(new Handshake { From = LayerName, Action = "output_empty" });

        var (isHallucinated, reason) = CheckHallucination(response);

        return Task.FromResult(new Handshake
        {
            From = LayerName,
            Action = "reviewed",
            Payload = new Dictionary<string, object?>
            {
                ["response"] = response,
                ["hallucination_risk"] = isHallucinated,
                ["hallucination_reason"] = reason,
                ["format"] = "markdown",
                ["reviewed_at"] = DateTime.UtcNow
            }
        });
    }

    public async Task<string> SilentSelfCheckAsync(string response, CancellationToken cancellationToken = default)
    {
        try
        {
            var prompt = $"You just said: {response}\n\nIs there anything factually wrong or misleading in your response? Answer with 'OK' if correct, or explain the error briefly.";
            var check = await LLM.CompleteAsync(prompt, new ChatOptions { Temperature = 0.1f, MaxOutputTokens = 200 }, cancellationToken).ConfigureAwait(false);
            return check.Contains("OK", StringComparison.OrdinalIgnoreCase) ? "verified" : $"flagged: {check}";
        }
        catch
        {
            return "unchecked";
        }
    }

    private static (bool IsHallucinated, string Reason) CheckHallucination(string response) =>
        GovernorUtilities.CheckHallucination(response);
}
