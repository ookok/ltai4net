using LTAI.Knowledge.Vector.Interfaces;
using LTAI.Tools.Tools;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Tools;

public sealed class ToolDefResult
{
    public ToolDef Definition { get; init; } = null!;
    public float Score { get; init; }
}

public sealed class ToolRetriever
{
    private readonly IVectorStore _vectorStore;
    private readonly ILogger<ToolRetriever> _logger;
    private readonly Dictionary<string, (ToolDef Tool, float[] Embedding)> _toolIndex = new();
    private bool _initialized;

    private static readonly string[] CoreTools =
    {
        "vfs:read", "vfs:write", "vfs:list", "shell:exec", "http:get"
    };

    public ToolRetriever(IVectorStore vectorStore, ILogger<ToolRetriever> logger)
    {
        _vectorStore = vectorStore;
        _logger = logger;
    }

    public async Task IndexAllToolsAsync(CancellationToken ct = default)
    {
        if (_initialized) return;

        foreach (var tool in LTAIToolRegistry.AllTools.Where(t => t.Handler != null))
        {
            try
            {
                var desc = $"{tool.Name}: {tool.Description}";
                var emb = await _vectorStore.EmbedAsync(desc, ct).ConfigureAwait(false);
                _toolIndex[tool.Name] = (tool, emb);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ToolRetriever: failed to index {Tool}", tool.Name);
            }
        }

        _initialized = true;
        _logger.LogInformation("ToolRetriever: indexed {Count} tools", _toolIndex.Count);
    }

    public async Task<IReadOnlyList<ToolDefResult>> RetrieveToolsAsync(
        string intent, string query, int topK = 12, CancellationToken ct = default)
    {
        if (!_initialized)
            return CoreTools.Select(n => new ToolDefResult
            {
                Definition = new ToolDef(n, n, "core", null), Score = 0.5f
            }).ToList();

        try
        {
            var queryText = $"{intent}: {query}";
            var queryEmbedding = await _vectorStore.EmbedAsync(queryText, ct).ConfigureAwait(false);

            var scored = _toolIndex.Values
                .Select(kv => new ToolDefResult
                {
                    Definition = kv.Tool,
                    Score = CosineSimilarity(queryEmbedding, kv.Embedding)
                })
                .OrderByDescending(x => x.Score)
                .Take(topK)
                .ToList();

            foreach (var core in CoreTools)
            {
                if (_toolIndex.TryGetValue(core, out var t) &&
                    !scored.Any(s => s.Definition.Name == core))
                    scored.Add(new ToolDefResult { Definition = t.Tool, Score = 0.5f });
            }

            _logger.LogDebug("ToolRetriever: recalled {Count} tools for intent={Intent}", scored.Count, intent);
            return scored;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ToolRetriever: retrieval failed, falling back to core tools");
            return CoreTools.Select(n => new ToolDefResult
            {
                Definition = new ToolDef(n, n, "core", null), Score = 0.2f
            }).ToList();
        }
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        float dot = 0, normA = 0, normB = 0;
        int len = Math.Min(a.Length, b.Length);
        for (int i = 0; i < len; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        return normA > 0 && normB > 0 ? (float)(dot / (Math.Sqrt(normA) * Math.Sqrt(normB))) : 0;
    }
}
