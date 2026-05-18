using LTAI.Core.Interfaces;
using LTAI.Core.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed class ContextGovernor : LayerGovernor
{
    private readonly List<(string prompt, string response)> _turnHistory = new();

    public ContextGovernor(ICognitiveMesh mesh, IProviderEngine llm, ILogger<ContextGovernor> logger)
        : base("context", mesh, llm, logger) { }

    public override async Task<Handshake> ProcessAsync(Handshake incoming, CancellationToken cancellationToken = default)
    {
        var query = incoming.Payload?.GetValueOrDefault("query")?.ToString() ?? "";

        var relevantContext = await PreloadKnowledgeAsync(query, cancellationToken);

        return new Handshake
        {
            From = LayerName,
            Action = "context_loaded",
            Payload = new Dictionary<string, object?>
            {
                ["query"] = query,
                ["context"] = relevantContext,
                ["turn_count"] = _turnHistory.Count
            }
        };
    }

    public void AddTurn(string prompt, string response)
    {
        _turnHistory.Add((prompt, response));
        if (_turnHistory.Count > 100)
            _turnHistory.RemoveAt(0);
    }

    public string CompressHistory()
    {
        if (_turnHistory.Count == 0) return "";
        var summary = string.Join("\n", _turnHistory.Select(t => $"Q: {t.prompt[..Math.Min(t.prompt.Length, 100)]}\nA: {t.response[..Math.Min(t.response.Length, 100)]}"));
        return summary;
    }

    private async Task<string> PreloadKnowledgeAsync(string query, CancellationToken cancellationToken)
    {
        return "";
    }
}
