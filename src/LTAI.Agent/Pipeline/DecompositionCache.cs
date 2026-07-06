using System.Collections.Concurrent;

namespace LTAI.Agent.Pipeline;

/// <summary>
/// Simple in-memory cache for decomposition results.
/// Keyed by normalized query text, 30-minute TTL.
/// Avoids redundant LLM decomposition calls for similar queries.
/// </summary>
public static class DecompositionCache
{
    private static readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

    private sealed record CacheEntry(List<string> Tasks, DateTime CreatedAt);

    public static bool TryGet(string query, out List<string>? tasks)
    {
        if (_cache.TryGetValue(Normalize(query), out var entry) &&
            DateTime.UtcNow - entry.CreatedAt < CacheTtl)
        {
            tasks = [.. entry.Tasks];
            return true;
        }
        tasks = null;
        return false;
    }

    public static void Set(string query, List<string> tasks)
    {
        _cache[Normalize(query)] = new CacheEntry([.. tasks], DateTime.UtcNow);
    }

    public static void Invalidate() => _cache.Clear();

    private static string Normalize(string query) => query.Trim().ToLowerInvariant();
}
