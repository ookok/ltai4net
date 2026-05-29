using LiteDB;
using LTAI.AI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Vector;

/// <summary>
/// Knowledge Base Graph: builds and queries a semantic graph of documents, concepts, and facts.
/// Injects relevant context into agent runs via AIContextProvider.
/// </summary>
public sealed class KnowledgeGraph : AIContextProvider
{
    private readonly GraphStore _store;
    private readonly EmbeddingClient _embedder;
    private readonly ILogger<KnowledgeGraph> _logger;

    public KnowledgeGraph(GraphStore store, EmbeddingClient embedder, ILogger<KnowledgeGraph> logger)
        : base(null, null, null)
    {
        _store = store;
        _embedder = embedder;
        _logger = logger;
    }

    // ─── Building the KB ───

    public async Task<string> IngestDocument(string id, string title, string content)
    {
        // Generate embedding for the document
        var embedding = await EmbedAsync(title + "\n" + content[..Math.Min(content.Length, 2000)]);

        _store.UpsertNode($"doc:{id}", "document", title, embedding,
            new() { ["path"] = id, ["summary"] = title, ["length"] = content.Length });

        // Extract concepts (simple: use title words + key phrases)
        var concepts = ExtractConcepts(title, content);
        foreach (var concept in concepts)
        {
            var cEmbedding = await EmbedAsync(concept);
            _store.UpsertNode($"concept:{concept.ToLowerInvariant().Replace(" ", "_")}",
                "concept", concept, cEmbedding);
            _store.AddEdge($"doc:{id}", $"concept:{concept.ToLowerInvariant().Replace(" ", "_")}", "contains");
        }

        _logger.LogInformation("Ingested document '{Id}' with {Count} concepts", id, concepts.Count);
        return $"Ingested '{title}' with {concepts.Count} concepts";
    }

    public async Task<string> IngestFact(string id, string content, string category = "general",
        string? sourceId = null)
    {
        var embedding = await EmbedAsync(content);
        _store.UpsertNode($"fact:{id}", "fact", content[..Math.Min(content.Length, 100)], embedding,
            new() { ["content"] = content, ["category"] = category });

        if (sourceId != null)
            _store.AddEdge(sourceId, $"fact:{id}", "has_fact");

        return $"Ingested fact '{id}'";
    }

    // ─── Query ───

    public async Task<List<string>> QueryAsync(string query, int topK = 5, bool expandGraph = true)
    {
        var embedding = await EmbedAsync(query);
        var results = _store.SearchNodes(embedding, topK);

        if (expandGraph)
        {
            // BFS expansion: find neighbors of top result
            var expanded = new HashSet<string>();
            foreach (var r in results.Take(3))
            {
                var neighbors = _store.TraverseBfs(r["_id"].AsString, maxDepth: 2, maxNodes: 10);
                foreach (var n in neighbors) expanded.Add(n);
            }
            // Also add from results
            foreach (var r in results) expanded.Add(r["_id"].AsString);

            return expanded.Select(id =>
            {
                var node = _store.GetNode(id);
                if (node == null) return null;
                var type = node["type"].AsString;
                var name = node["name"].AsString;
                var extra = type switch
                {
                    "fact" => node.ContainsKey("content") ? ": " + node["content"].AsString : "",
                    "document" => node.ContainsKey("summary") ? ": " + node["summary"].AsString : "",
                    _ => ""
                };
                return $"[{type}] {name}{extra}";
            }).Where(s => s != null).Cast<string>().ToList();
        }

        return results.Select(r =>
        {
            var type = r["type"].AsString;
            var name = r["name"].AsString;
            return $"[{type}] {name}";
        }).ToList();
    }

    // ─── AIContextProvider ───

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken ct = default)
    {
        var msgs = context.AIContext?.Messages;
        if (msgs == null) return context.AIContext!;

        var userMsg = msgs.LastOrDefault(m => m.Role == ChatRole.User);
        if (userMsg?.Text == null || userMsg.Text.Length < 5)
            return context.AIContext!;

        try
        {
            var results = await QueryAsync(userMsg.Text, topK: 3);
            if (results.Count == 0) return context.AIContext!;

            var contextBlock = "## Relevant Knowledge:\n" + string.Join("\n", results.Select(r => "- " + r));
            _logger.LogInformation("KB Graph context injected: {Count} nodes", results.Count);

            return new AIContext
            {
                Instructions = context.AIContext?.Instructions != null
                    ? context.AIContext.Instructions + "\n\n" + contextBlock
                    : contextBlock,
                Messages = context.AIContext?.Messages,
                Tools = context.AIContext?.Tools,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KB Graph query failed");
            return context.AIContext!;
        }
    }

    // ─── Helpers ───

    private async Task<float[]> EmbedAsync(string text)
        => await _embedder.GenerateAsync(text);

    private static List<string> ExtractConcepts(string title, string content)
    {
        var words = (title + " " + content)
            .Split(new[] { ' ', '\n', '\r', ',', '.', '(', ')', '【', '】' },
                StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();
        return words;
    }

    public void Dispose() => _store.Dispose();
}
