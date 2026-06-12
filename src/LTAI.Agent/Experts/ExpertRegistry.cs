using System.Collections.Concurrent;
using LTAI.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Experts;

/// <summary>
/// Central registry for <see cref="IExpertModule"/> discovery and embedding-based
/// candidate ranking. Used by the ExpertRouter for deterministic top-K pre-selection
/// before the LLM-driven final selection.
/// 
/// Pattern follows <see cref="AgentRegistry"/>: capability descriptions are embedded
/// via ONNX (with ToolEmbeddingCache persistence) and ranked by cosine similarity.
/// </summary>
public sealed class ExpertRegistry
{
    private readonly List<ExpertEntry> _entries = [];
    private readonly EmbeddingClient _embedder;
    private readonly ToolEmbeddingCache? _cache;
    private readonly QueryEmbeddingCache? _queryCache;
    private readonly ILogger<ExpertRegistry>? _logger;

    public sealed record ExpertEntry(
        IExpertModule Expert,
        string CapabilityText,
        float[]? Embedding = null
    );

    public ExpertRegistry(
        IEnumerable<IExpertModule> experts,
        EmbeddingClient embedder,
        ToolEmbeddingCache? cache = null,
        QueryEmbeddingCache? queryCache = null,
        ILogger<ExpertRegistry>? logger = null)
    {
        _embedder = embedder;
        _cache = cache;
        _queryCache = queryCache;
        _logger = logger;
        foreach (var e in experts)
            _entries.Add(new ExpertEntry(e, e.CapabilityDescription));
    }

    public IReadOnlyList<ExpertEntry> Entries => _entries;

    public async Task EnsureEmbeddingsAsync(CancellationToken ct = default)
    {
        var pending = _entries
            .Where(x => x.Embedding == null)
            .ToList();
        if (pending.Count == 0) return;

        if (_cache != null)
        {
            var items = pending
                .Select(x => (x.Expert.ExpertId, x.CapabilityText))
                .ToList();
            try
            {
                var vectors = await _cache.GetOrComputeAllAsync(items, ct).ConfigureAwait(false);
                for (int i = 0; i < _entries.Count; i++)
                {
                    if (vectors.TryGetValue(_entries[i].Expert.ExpertId, out var v) && v != null)
                        _entries[i] = _entries[i] with { Embedding = v };
                }
                return;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "ExpertRegistry: cache embedding failed, falling back to per-expert compute");
            }
        }

        await ComputeEmbeddingsAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<(IExpertModule Expert, float Score)>> SelectTopKAsync(
        string query, int k = 5, CancellationToken ct = default)
    {
        await EnsureEmbeddingsAsync(ct).ConfigureAwait(false);

        if (_entries.Count == 0) return [];

        var anyEmbedding = false;
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].Embedding != null) { anyEmbedding = true; break; }
        }

        if (!anyEmbedding)
        {
            return _entries.Take(k).Select(e => (e.Expert, 0f)).ToList();
        }

        float[] taskEmb;
        var cached = _queryCache?.Get(query);
        if (cached != null)
        {
            taskEmb = cached;
        }
        else
        {
            try
            {
                taskEmb = await _embedder.GenerateAsync(query, ct).ConfigureAwait(false);
            }
            catch
            {
                taskEmb = EmbeddingClient.FastEmb(query);
            }
            _queryCache?.Set(query, taskEmb);
        }

        var scored = new List<(IExpertModule Expert, float Score)>(_entries.Count);
        for (int i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            if (entry.Embedding == null) continue;
            var score = CosineSimilarity(taskEmb, entry.Embedding);
            scored.Add((entry.Expert, score));
        }

        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        return scored.Take(k).ToList();
    }

    private async Task ComputeEmbeddingsAsync(CancellationToken ct)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].Embedding != null) continue;
            try
            {
                var emb = await _embedder.GenerateAsync(_entries[i].CapabilityText, ct).ConfigureAwait(false);
                _entries[i] = _entries[i] with { Embedding = emb };
            }
            catch
            {
                _entries[i] = _entries[i] with { Embedding = EmbeddingClient.FastEmb(_entries[i].CapabilityText) };
            }
        }
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0f;
        float dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        var denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom < 1e-9f ? 0f : dot / denom;
    }
}
