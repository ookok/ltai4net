using LTAI.Core.Models;
using LTAI.Knowledge.Vector.Interfaces;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed class ContextGovernor : LayerGovernor
{
    private readonly List<(string prompt, string response)> _turnHistory = new();
    private readonly IVectorStore _vectorStore;

    public ContextGovernor(
        IChatClient llm,
        ILogger<ContextGovernor> logger,
        IVectorStore vectorStore)
        : base("context", llm, logger)
    {
        _vectorStore = vectorStore;
    }

    public override async Task<Handshake> ProcessAsync(Handshake incoming, CancellationToken cancellationToken = default)
    {
        var query = incoming.Payload?.GetValueOrDefault("query")?.ToString() ?? "";

        var relevantContext = await PreloadKnowledgeAsync(query, cancellationToken).ConfigureAwait(false);

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
        return TieredCompressHistory();
    }

    public string TieredCompressHistory(int recentFull = 2, int summaryRange = 4)
    {
        if (_turnHistory.Count == 0) return "";

        var turns = _turnHistory.ToList();
        var parts = new List<string>();

        for (int i = 0; i < turns.Count; i++)
        {
            var distanceFromEnd = turns.Count - 1 - i;
            var (prompt, response) = turns[i];

            if (distanceFromEnd < recentFull)
            {
                parts.Add($"Q: {prompt[..Math.Min(prompt.Length, 200)]}\nA: {response[..Math.Min(response.Length, 200)]}");
            }
            else if (distanceFromEnd < recentFull + summaryRange)
            {
                var qSummary = prompt.Length > 80 ? prompt[..77] + "..." : prompt;
                var rSummary = response.Length > 80 ? response[..77] + "..." : response;
                parts.Add($"[summary] Q: {qSummary} A: {rSummary}");
            }
        }

        return string.Join("\n", parts);
    }

    public int GetHistoryTurnCount() => _turnHistory.Count;

    private async Task<string> PreloadKnowledgeAsync(string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "";

        try
        {
            var queryVec = await _vectorStore.EmbedAsync(query, cancellationToken).ConfigureAwait(false);
            var results = await _vectorStore.SearchSimilarAsync(queryVec, topK: 3, cancellationToken).ConfigureAwait(false);

            if (results.Count == 0)
                return "";

            var contextParts = new List<string>();
            foreach (var r in results)
            {
                if (!string.IsNullOrWhiteSpace(r.Text))
                    contextParts.Add($"[知识 {r.Score:F2}] {r.Text}");
            }

            return string.Join("\n", contextParts);
        }
        catch
        {
            return "";
        }
    }
}
