using LTAI.Core.Caching;
using LTAI.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.AI;

/// <summary>
/// In-memory response cache for LLM calls.
/// SHA256-keyed, 5min TTL, backed by unified <see cref="LTAICache{TKey,TValue}"/>.
/// </summary>
public sealed class ResponseCacheManager
{
    private readonly LTAICache<string, ChatResponse> _cache;
    private readonly ILogger _logger;

    public ResponseCacheManager(
        LTAICache<string, ChatResponse> cache,
        ILogger? logger = null)
    {
        _cache = cache;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    public ResponseCacheManager(
        int? sizeLimit = null,
        TimeSpan? ttl = null,
        ILogger? logger = null)
        : this(new LTAICache<string, ChatResponse>(new LTAICacheOptions
        {
            MaxEntries = sizeLimit ?? 256,
            DefaultTtl = ttl ?? TimeSpan.FromMinutes(EnvironmentConfig.LlmCacheTtlMin)
        }), logger)
    {
    }

    public ResponseCacheManager(int sizeLimit) : this((int?)sizeLimit) { }

    public LTAICacheMetrics Metrics => _cache.Metrics;

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
        if (_cache.TryGet(key, out cached))
        {
            _logger.LogDebug("Cache HIT for key={Key}", key);
            return true;
        }
        return false;
    }

    public void Set(string key, ChatResponse response)
    {
        _cache.Set(key, response);
    }
}
