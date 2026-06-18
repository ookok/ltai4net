using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace LTAI.Agent.Utils;

/// <summary>
/// Bounded, thread-safe Regex cache shared across the process.
/// Replaces scattered static ConcurrentDictionary instances in GlobUtils,
/// ReviewRuleEngine, ReviewTools, and similar utility classes.
/// LRU eviction at capacity limit.
/// </summary>
public static class RegexCache
{
    private static readonly ConcurrentDictionary<string, Regex> _cache = new(4, MaxCapacity, StringComparer.Ordinal);
    private static readonly ConcurrentQueue<string> _order = new();
    private const int MaxCapacity = 512;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(
        int.TryParse(Environment.GetEnvironmentVariable("LTAI_REGEX_TIMEOUT_MS"), out var rt) ? Math.Max(100, rt) : 1000);

    /// <summary>Get or create a compiled regex. Cache key = pattern + options.</summary>
    public static Regex GetOrAdd(string pattern, RegexOptions options, TimeSpan? timeout = null)
    {
        var key = options == RegexOptions.None ? pattern : $"{pattern}:{(int)options}";
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        var ttl = timeout ?? DefaultTimeout;
        var regex = new Regex(pattern, options | RegexOptions.Compiled, ttl);
        AddToCache(key, regex);
        return regex;
    }

    /// <summary>Get or create a regex from a glob pattern.</summary>
    public static Regex GetOrAddGlob(string glob, TimeSpan? timeout = null)
    {
        var key = $"glob:{glob}";
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        var ttl = timeout ?? DefaultTimeout;
        var escaped = Regex.Escape(glob)
            .Replace(@"\*\*", ".*")
            .Replace(@"\*", "[^/]*")
            .Replace(@"\?", ".")
            .Replace(@"{", "(?:")
            .Replace(@",", "|")
            .Replace(@"}", ")");
        var regex = new Regex($"^{escaped}$", RegexOptions.IgnoreCase | RegexOptions.Compiled, ttl);
        AddToCache(key, regex);
        return regex;
    }

    /// <summary>Get or create a regex using a custom factory function.</summary>
    public static Regex GetOrAddFactory(string key, Func<Regex> factory)
    {
        if (_cache.TryGetValue(key, out var cached))
            return cached;
        var regex = factory();
        AddToCache(key, regex);
        return regex;
    }

    private static void AddToCache(string key, Regex regex)
    {
        if (_cache.TryAdd(key, regex))
        {
            _order.Enqueue(key);
            while (_order.Count > MaxCapacity && _order.TryDequeue(out var old))
                _cache.TryRemove(old, out _);
        }
    }

    /// <summary>Clear all cached regexes. Useful for testing.</summary>
    public static void Clear()
    {
        _cache.Clear();
        while (_order.TryDequeue(out _)) { }
    }

    public static int Count => _cache.Count;
}
