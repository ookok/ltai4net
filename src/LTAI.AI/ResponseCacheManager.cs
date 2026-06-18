using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace LTAI.AI;

/// <summary>
/// In-memory response cache for LLM calls.
/// SHA256-keyed, 5min TTL, 256-entry LRU.
/// </summary>
public sealed class ResponseCacheManager
{
    private readonly MemoryCache _cache;
    private readonly TimeSpan _ttl;
    private readonly int _sizeLimit;
    private readonly ILogger _logger;

    public ResponseCacheManager(
        int? sizeLimit = null,
        TimeSpan? ttl = null,
        ILogger? logger = null)
    {
        _sizeLimit = sizeLimit ?? 256;
        _ttl = ttl ?? TimeSpan.FromMinutes(
            int.TryParse(Environment.GetEnvironmentVariable("LTAI_LLM_CACHE_TTL_MIN"), out var t) ? Math.Max(1, t) : 5);
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        _cache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = _sizeLimit,
            ExpirationScanFrequency = TimeSpan.FromMinutes(1)
        });
    }

    public ResponseCacheManager(int sizeLimit) : this((int?)sizeLimit) { }

    /// <summary>Build a hash key from provider, messages, and options.</summary>
    public static string BuildCacheKey(string provider, IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var hc = new HashCode();
        hc.Add(provider, StringComparer.OrdinalIgnoreCase);
        hc.Add(options?.Temperature);
        hc.Add(options?.MaxOutputTokens);
        foreach (var m in messages)
            hc.Add(m.Text ?? "");
        return hc.ToHashCode().ToString("x8");
    }

    public bool TryGet(string key, out ChatResponse? cached)
    {
        if (_cache.TryGetValue<ChatResponse>(key, out var result))
        {
            cached = result;
            _logger.LogDebug("Cache HIT for key={Key}", key);
            return true;
        }
        cached = null;
        return false;
    }

    public void Set(string key, ChatResponse response)
    {
        _cache.Set(key, response, new MemoryCacheEntryOptions
        {
            Size = 1,
            AbsoluteExpirationRelativeToNow = _ttl
        });
    }
}
