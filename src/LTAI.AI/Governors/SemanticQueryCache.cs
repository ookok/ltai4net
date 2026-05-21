using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed record CacheEntry
{
    public string Query { get; init; } = "";
    public string NormalizedQuery { get; init; } = "";
    public string Response { get; init; } = "";
    public float Confidence { get; init; }
    public int HitCount { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime LastHit { get; init; }
    public DateTime ExpiresAt { get; init; }
    public string Route { get; init; } = "";
    public string Domain { get; init; } = "";
}

public sealed record SemanticCacheResult
{
    public bool Hit { get; init; }
    public string Response { get; init; } = "";
    public float Similarity { get; init; }
    public string CacheKey { get; init; } = "";
}

public sealed class SemanticQueryCache
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly ILogger<SemanticQueryCache> _logger;
    private readonly int _maxEntries;
    private readonly float _minSimilarity;
    private readonly object _statsLock = new();
    private long _totalHits;
    private long _totalMisses;

    public SemanticQueryCache(
        int maxEntries = 500,
        float minSimilarity = 0.85f,
        ILogger<SemanticQueryCache>? logger = null)
    {
        _maxEntries = maxEntries;
        _minSimilarity = minSimilarity;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SemanticQueryCache>.Instance;
    }

    public SemanticCacheResult Lookup(string query)
    {
        var normalized = NormalizeQuery(query);
        var cacheKey = ComputeCacheKey(normalized);

        if (_cache.TryGetValue(cacheKey, out var exactEntry))
        {
            if (DateTime.UtcNow < exactEntry.ExpiresAt)
            {
                _cache[cacheKey] = exactEntry with
                {
                    HitCount = exactEntry.HitCount + 1,
                    LastHit = DateTime.UtcNow
                };

                lock (_statsLock) { _totalHits++; }

                return new SemanticCacheResult
                {
                    Hit = true,
                    Response = exactEntry.Response,
                    Similarity = 1.0f,
                    CacheKey = cacheKey
                };
            }
            else
            {
                _cache.TryRemove(cacheKey, out _);
            }
        }

        foreach (var (key, entry) in _cache)
        {
            if (DateTime.UtcNow >= entry.ExpiresAt)
            {
                _cache.TryRemove(key, out _);
                continue;
            }

            var similarity = ComputeSemanticSimilarity(normalized, entry.NormalizedQuery);
            if (similarity >= _minSimilarity)
            {
                _cache[key] = entry with
                {
                    HitCount = entry.HitCount + 1,
                    LastHit = DateTime.UtcNow
                };

                lock (_statsLock) { _totalHits++; }

                return new SemanticCacheResult
                {
                    Hit = true,
                    Response = entry.Response,
                    Similarity = similarity,
                    CacheKey = key
                };
            }
        }

        lock (_statsLock) { _totalMisses++; }

        return new SemanticCacheResult { Hit = false };
    }

    public void Store(string query, string response, string route, string domain, float confidence, TimeSpan? ttl = null)
    {
        var normalized = NormalizeQuery(query);
        var cacheKey = ComputeCacheKey(normalized);
        var effectiveTtl = ttl ?? GetDefaultTtl(route);

        var entry = new CacheEntry
        {
            Query = query,
            NormalizedQuery = normalized,
            Response = response,
            Confidence = confidence,
            HitCount = 0,
            CreatedAt = DateTime.UtcNow,
            LastHit = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow + effectiveTtl,
            Route = route,
            Domain = domain
        };

        if (_cache.Count >= _maxEntries)
        {
            EvictLeastValuable();
        }

        _cache[cacheKey] = entry;
        _logger.LogDebug("Cache stored: key={Key}, route={Route}, ttl={Ttl}", cacheKey[..8], route, effectiveTtl);
    }

    public Dictionary<string, object> GetStats()
    {
        var totalRequests = _totalHits + _totalMisses;
        var hitRate = totalRequests > 0 ? (float)_totalHits / totalRequests : 0f;

        var byRoute = _cache.Values
            .GroupBy(e => e.Route)
            .ToDictionary(g => g.Key, g => new { Count = g.Count(), TotalHits = g.Sum(e => e.HitCount) });

        return new Dictionary<string, object>
        {
            ["total_entries"] = _cache.Count,
            ["max_entries"] = _maxEntries,
            ["total_hits"] = _totalHits,
            ["total_misses"] = _totalMisses,
            ["hit_rate"] = hitRate,
            ["by_route"] = byRoute,
            ["memory_estimate_kb"] = EstimateMemoryKb()
        };
    }

    public int CleanupExpired()
    {
        var now = DateTime.UtcNow;
        var removed = 0;

        foreach (var (key, entry) in _cache)
        {
            if (now >= entry.ExpiresAt)
            {
                _cache.TryRemove(key, out _);
                removed++;
            }
        }

        if (removed > 0)
            _logger.LogInformation("Cache cleanup: removed {Count} expired entries", removed);

        return removed;
    }

    private void EvictLeastValuable()
    {
        var toEvict = _cache.Values
            .OrderBy(e => ComputeEntryValue(e))
            .Take(Math.Max(1, _maxEntries / 10))
            .Select(e => ComputeCacheKey(e.NormalizedQuery))
            .ToList();

        foreach (var key in toEvict)
        {
            _cache.TryRemove(key, out _);
        }

        _logger.LogDebug("Cache evicted {Count} least valuable entries", toEvict.Count);
    }

    private static float ComputeEntryValue(CacheEntry entry)
    {
        var recency = (float)(DateTime.UtcNow - entry.LastHit).TotalMinutes;
        var hitBonus = entry.HitCount * 10f;
        var agePenalty = (float)(DateTime.UtcNow - entry.CreatedAt).TotalHours;

        return recency - hitBonus + agePenalty;
    }

    private static string NormalizeQuery(string query)
    {
        var normalized = query.ToLowerInvariant().Trim();
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\s+", " ");
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"[^\w\s\u4e00-\u9fff]", "");
        return normalized;
    }

    private static string ComputeCacheKey(string normalizedQuery)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hash = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(normalizedQuery));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static float ComputeSemanticSimilarity(string a, string b)
    {
        if (a == b) return 1.0f;
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0f;

        var wordsA = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var wordsB = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

        if (wordsA.Count == 0 || wordsB.Count == 0) return 0f;

        var intersection = wordsA.Intersect(wordsB).Count();
        var union = wordsA.Union(wordsB).Count();

        var jaccard = union > 0 ? (float)intersection / union : 0f;

        var longer = wordsA.Count > wordsB.Count ? wordsA : wordsB;
        var shorter = wordsA.Count > wordsB.Count ? wordsB : wordsA;
        var containment = (float)shorter.Count(w => longer.Contains(w)) / shorter.Count;

        return jaccard * 0.6f + containment * 0.4f;
    }

    private static TimeSpan GetDefaultTtl(string route)
    {
        return route switch
        {
            "reflex" => TimeSpan.FromHours(24),
            "graph_knowledge" => TimeSpan.FromHours(12),
            "cell_greeting" => TimeSpan.FromHours(24),
            "cell_code" => TimeSpan.FromHours(6),
            "cell_math" => TimeSpan.FromHours(12),
            "cell_science" => TimeSpan.FromHours(12),
            "cell_language" => TimeSpan.FromHours(6),
            "cell_system" => TimeSpan.FromHours(6),
            "cell_creative" => TimeSpan.FromHours(2),
            "delegate_l2" => TimeSpan.FromHours(4),
            _ => TimeSpan.FromHours(1)
        };
    }

    private long EstimateMemoryKb()
    {
        var serialized = JsonSerializer.Serialize(_cache.Values);
        return serialized.Length / 1024;
    }
}
