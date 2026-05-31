using Microsoft.Extensions.Caching.Memory;

namespace LTAI.Core.Safety;

internal static class VerdictCache
{
    private static readonly MemoryCache _cache = new(new MemoryCacheOptions
    {
        SizeLimit = 1000,
        ExpirationScanFrequency = TimeSpan.FromSeconds(30)
    });

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    public static (bool safe, string reason)? Get(string text)
    {
        if (text.Length > 500) return null;
        var key = (ulong)text.Length << 32 | (uint)string.GetHashCode(text, StringComparison.Ordinal);
        if (_cache.TryGetValue(key, out (bool safe, string reason) cached))
            return cached;
        return null;
    }

    public static void Set(string text, bool safe, string reason)
    {
        if (text.Length > 500) return;
        var key = (ulong)text.Length << 32 | (uint)string.GetHashCode(text, StringComparison.Ordinal);
        var entry = _cache.CreateEntry(key);
        entry.Value = (safe, reason);
        entry.AbsoluteExpirationRelativeToNow = CacheTtl;
        entry.Size = 1;
    }
}
