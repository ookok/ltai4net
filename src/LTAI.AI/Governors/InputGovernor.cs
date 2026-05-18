using LTAI.Core.Interfaces;
using LTAI.Core.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed class InputGovernor : LayerGovernor
{
    private static readonly string[] SpinalCommands = { "/help", "/status", "/pause", "/resume", "/restart" };

    public InputGovernor(ICognitiveMesh mesh, IProviderEngine llm, ILogger<InputGovernor> logger)
        : base("input", mesh, llm, logger) { }

    public override async Task<Handshake> ProcessAsync(Handshake incoming, CancellationToken cancellationToken = default)
    {
        var query = incoming.Payload?.GetValueOrDefault("query")?.ToString() ?? "";

        if (IsSpinalReflex(query, out var command))
        {
            Logger.LogInformation("Spinal reflex: {Command}", command);
            return new Handshake
            {
                From = LayerName,
                Action = "reflex",
                Payload = new Dictionary<string, object?>
                {
                    ["command"] = command,
                    ["original_query"] = query
                }
            };
        }

        var (complexity, label) = await ClassifyIntentAsync(query, cancellationToken);
        var emotion = await DetectEmotionAsync(query, cancellationToken);

        return new Handshake
        {
            From = LayerName,
            Action = "classified",
            Payload = new Dictionary<string, object?>
            {
                ["query"] = query,
                ["complexity"] = complexity,
                ["label"] = label,
                ["emotion"] = emotion,
                ["query_length"] = query.Length
            }
        };
    }

    private static bool IsSpinalReflex(string query, out string command)
    {
        foreach (var cmd in SpinalCommands)
        {
            if (query.Trim().StartsWith(cmd, StringComparison.OrdinalIgnoreCase))
            {
                command = cmd;
                return true;
            }
        }
        command = "";
        return false;
    }

    private async Task<(float complexity, string label)> ClassifyIntentAsync(string query, CancellationToken cancellationToken)
    {
        if (query.Length < 20)
            return (0.2f, "fast");

        if (query.Length > 200)
            return (0.8f, "deep");

        var prompt = $"Classify this query complexity (0.0-1.0) and type (fast/deep): {query[..Math.Min(query.Length, 500)]}";
        var result = await LLM.ChatAsync(prompt, new LLMChatOptions { MaxTokens = 50 }, cancellationToken);
        return (0.5f, "deep");
    }

    private async Task<string> DetectEmotionAsync(string query, CancellationToken cancellationToken)
    {
        return "neutral";
    }
}
