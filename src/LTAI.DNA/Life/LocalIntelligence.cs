using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using LTAI.DNA.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.DNA.Life;

public sealed class LocalIQ
{
    public int TotalQueries { get; set; }
    public int DirectHits { get; set; }
    public int LocalHits { get; set; }
    public int RemoteHits { get; set; }
    public double DirectPct => TotalQueries > 0 ? (double)DirectHits / TotalQueries : 0;
    public double LocalCachePct => TotalQueries > 0 ? (double)(DirectHits + LocalHits) / TotalQueries : 0;
    public double RemotePct => TotalQueries > 0 ? (double)RemoteHits / TotalQueries : 0;
}

public sealed class LocalIntelligence
{
    private readonly ILogger<LocalIntelligence> _logger;
    private readonly ConcurrentDictionary<string, CachedPattern> _cache = new();
    private readonly LocalIQ _iq = new();
    private const int MaxCacheEntries = 1000;

    public LocalIntelligence(ILogger<LocalIntelligence>? logger = null)
    {
        _logger = logger ?? NullLogger<LocalIntelligence>.Instance;
    }

    public TierResponse Respond(string query, string domain = "general")
    {
        _iq.TotalQueries++;

        var direct = MatchCache(query);
        if (direct != null)
        {
            _iq.DirectHits++;
            return new TierResponse
            {
                Content = direct.Response,
                Tier = IntelligenceTier.Direct,
                Confidence = direct.Confidence,
                LatencyMs = 1,
                SourceDetail = $"cache:{direct.Id}",
            };
        }

        var pattern = MatchPattern(query, domain);
        if (pattern != null)
        {
            _iq.LocalHits++;
            return new TierResponse
            {
                Content = pattern.Response,
                Tier = IntelligenceTier.Local,
                Confidence = pattern.Confidence,
                LatencyMs = 5,
                SourceDetail = $"pattern:{pattern.Id}",
            };
        }

        _iq.RemoteHits++;
        return new TierResponse
        {
            Content = "",
            Tier = IntelligenceTier.Remote,
            Confidence = 0.5,
            LatencyMs = 0,
            SourceDetail = "requires_remote",
        };
    }

    public void LearnFromLlm(string query, string response, string domain)
    {
        var hash = ComputeHash(query);
        if (_cache.Count >= MaxCacheEntries)
        {
            var toRemove = _cache.OrderBy(kv => kv.Value.Effectiveness).First();
            _cache.TryRemove(toRemove.Key, out _);
        }

        _cache[hash] = new CachedPattern
        {
            Id = hash,
            Pattern = query,
            Response = response,
            Domain = domain,
            Confidence = 0.8,
            HitCount = 1,
            Source = "llm_distilled",
        };
    }

    private CachedPattern? MatchCache(string query)
    {
        var hash = ComputeHash(query);
        if (_cache.TryGetValue(hash, out var exact))
        {
            exact.HitCount++;
            exact.LastHit = DateTime.UtcNow;
            return exact;
        }

        foreach (var (_, pattern) in _cache)
        {
            if (JaccardSimilarity(query, pattern.Pattern) >= 0.8)
            {
                pattern.HitCount++;
                pattern.LastHit = DateTime.UtcNow;
                return pattern;
            }
        }
        return null;
    }

    private CachedPattern? MatchPattern(string query, string domain)
    {
        foreach (var (_, pattern) in _cache)
        {
            if (pattern.Domain == domain && JaccardSimilarity(query, pattern.Pattern) >= 0.5)
                return pattern;
        }
        return null;
    }

    private static string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes)[..16];
    }

    private static double JaccardSimilarity(string a, string b)
    {
        var wordsA = new HashSet<string>(a.ToLower().Split(' ', '\n', '，', '。', '、', ',', '.', ';'));
        var wordsB = new HashSet<string>(b.ToLower().Split(' ', '\n', '，', '。', '、', ',', '.', ';'));
        if (wordsA.Count == 0 && wordsB.Count == 0) return 1;
        var intersection = wordsA.Intersect(wordsB).Count();
        var union = wordsA.Union(wordsB).Count();
        return union == 0 ? 0 : (double)intersection / union;
    }

    public Dictionary<string, object> GetStats()
    {
        return new Dictionary<string, object>
        {
            ["total_queries"] = _iq.TotalQueries,
            ["direct_hits"] = _iq.DirectHits,
            ["local_hits"] = _iq.LocalHits,
            ["remote_hits"] = _iq.RemoteHits,
            ["direct_pct"] = Math.Round(_iq.DirectPct, 3),
            ["local_pct"] = Math.Round(_iq.LocalCachePct, 3),
            ["cache_entries"] = _cache.Count,
        };
    }
}
