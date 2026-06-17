using Microsoft.Extensions.Caching.Memory;

namespace LTAI.Core.Safety;

internal static class VerdictCache
{
    private static readonly MemoryCache _cache = new(new MemoryCacheOptions
    {
        SizeLimit = 2000,
        ExpirationScanFrequency = TimeSpan.FromSeconds(30)
    });

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    public static (bool safe, string reason)? Get(string text, string direction = "")
    {
        if (text.Length > 500) return null;
        var key = direction.Length > 0 ? $"{direction}:{text}" : text;
        if (_cache.TryGetValue(key, out (bool safe, string reason) cached))
            return cached;
        return null;
    }

    public static void Set(string text, bool safe, string reason, string direction = "")
    {
        if (text.Length > 500) return;
        var key = direction.Length > 0 ? $"{direction}:{text}" : text;
        _cache.Set(key, (safe, reason), new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = CacheTtl,
            Size = 1
        });
    }
}
