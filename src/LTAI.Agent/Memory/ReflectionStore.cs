using LTAI.AI;
using LTAI.Agent.Learning;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Memory;

public sealed class ReflectionStore
{
    private readonly PalaceStore _palace;
    private readonly EmbeddingClient? _embedder;
    private readonly MemoryStore? _memoryStore;
    private readonly ILogger<ReflectionStore> _logger;
    private const string ReflectionWing = "reflection";

    public ReflectionStore(PalaceStore palace,
        EmbeddingClient? embedder = null,
        MemoryStore? memoryStore = null,
        ILogger<ReflectionStore>? logger = null)
    {
        _palace = palace;
        _embedder = embedder;
        _memoryStore = memoryStore;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ReflectionStore>.Instance;
    }

    public async Task StoreReflectionAsync(string agentId, ReflectionResult reflection, CancellationToken ct = default)
    {
        var content = $$"""
## Causal Reflection
{{reflection.CausalReflection}}

## Corrective Strategy
{{reflection.CorrectiveStrategy}}

## Preventive Guideline
{{reflection.PreventiveGuideline}}
""";

        await _palace.StoreAsync(
            wing: ReflectionWing,
            room: agentId,
            content: content,
            importance: 0.9f,
            principal: "system",
            scope: "shared").ConfigureAwait(false);

        _logger.LogInformation("ReflectionStore: stored reflection for agent '{Agent}' ({Len} chars)",
            agentId, content.Length);
    }

    public async Task<List<string>> RetrieveRelevantReflectionsAsync(
        string query, int topK = 3, CancellationToken ct = default)
    {
        try
        {
            var drawers = await _palace.SearchByWingAsync(ReflectionWing, maxCount: topK * 5)
                .ConfigureAwait(false);
            if (drawers.Count == 0) return [];

            float[]? qEmb = null;
            if (_embedder != null)
            {
                try { qEmb = await _embedder.GenerateAsync(query, ct).ConfigureAwait(false); }
                catch { qEmb = EmbeddingClient.FastEmb(query, _embedder.Dimension); }
            }

            var scored = new List<(string Content, float Score)>();
            foreach (var drawer in drawers)
            {
                if (drawer.Content == null) continue;

                float score = 0;
                if (qEmb != null)
                {
                    var dEmb = drawer.Embedding ?? [];
                    score = dEmb.Length == qEmb.Length
                        ? CosineSimilarity(qEmb, dEmb)
                        : 0f;
                }

                if (drawer.Content.Contains(query, StringComparison.OrdinalIgnoreCase))
                    score += 0.3f;

                scored.Add((drawer.Content, score));
            }

            return scored
                .OrderByDescending(s => s.Score)
                .Take(topK)
                .Select(s => s.Content)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ReflectionStore: retrieval failed");
            return [];
        }
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        var mag = Math.Sqrt(na) * Math.Sqrt(nb);
        return (float)(mag > 0 ? dot / mag : 0);
    }
}
