using LTAI.AI.Utilities;
using LTAI.Core.Interfaces;
using LTAI.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed class InputGovernor : LayerGovernor
{
    private static readonly string[] SpinalCommands = { "/help", "/status", "/pause", "/resume", "/restart" };

    public InputGovernor(ICognitiveMesh mesh, IChatClient llm, ILogger<InputGovernor> logger)
        : base("input", mesh, llm, logger) { }

    public override Task<Handshake> ProcessAsync(Handshake incoming, CancellationToken cancellationToken = default)
    {
        var query = incoming.Payload?.GetValueOrDefault("query")?.ToString() ?? "";

        if (IsSpinalReflex(query, out var command))
        {
            Logger.LogInformation("Spinal reflex: {Command}", command);
            return Task.FromResult(new Handshake
            {
                From = LayerName,
                Action = "reflex",
                Payload = new Dictionary<string, object?>
                {
                    ["command"] = command,
                    ["original_query"] = query
                }
            });
        }

        var (complexity, label) = ClassifyIntent(query);
        var emotion = DetectEmotion(query);

        return Task.FromResult(new Handshake
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
        });
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

    private static (float complexity, string label) ClassifyIntent(string query) =>
        GovernorUtilities.ClassifyIntent(query);

    private static string DetectEmotion(string query) =>
        GovernorUtilities.DetectEmotion(query);
}
