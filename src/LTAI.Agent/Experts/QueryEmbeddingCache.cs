using System.Collections.Concurrent;

namespace LTAI.Agent.Experts;

/// <summary>
/// Request-scoped query→embedding cache that eliminates duplicate ONNX
/// embedding calls within a single conversation turn.
///
/// On a knowledge query turn, the same query text is embedded twice:
///   1. ExpertRegistry.SelectTopKAsync (for expert routing)
///   2. ToolFilteringChatClient → ToolRegistry.SearchTopKAsync (for tool filtering)
///
/// This cache makes the second call return instantly (dictionary lookup).
/// Bounded at 64 entries to prevent unbounded growth in long sessions.
/// </summary>
public sealed class QueryEmbeddingCache
{
    private readonly ConcurrentDictionary<string, float[]> _store = new(StringComparer.Ordinal);
    private readonly int _maxEntries;

    public QueryEmbeddingCache(int maxEntries = 64)
    {
        _maxEntries = maxEntries;
    }

    public float[]? Get(string query)
    {
        _store.TryGetValue(query, out var emb);
        return emb;
    }

    public void Set(string query, float[] embedding)
    {
        if (_store.Count >= _maxEntries)
        {
            // Drop a random entry to keep the cache bounded
            foreach (var key in _store.Keys)
            {
                _store.TryRemove(key, out _);
                break;
            }
        }
        _store[query] = embedding;
    }

    public int Count => _store.Count;
}
