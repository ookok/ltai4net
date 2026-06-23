using LTAI.Core.Caching;

namespace LTAI.Core.Safety;

internal static class VerdictCache
{
    private static readonly LTAICache<string, (bool safe, string reason)> _cache = new(
        new LTAICacheOptions
        {
            MaxEntries = 2000,
            DefaultTtl = TimeSpan.FromSeconds(60)
        });

    public static (bool safe, string reason)? Get(string text, string direction = "")
    {
        if (text.Length > 500) return null;
        var key = direction.Length > 0 ? $"{direction}:{text}" : text;
        if (_cache.TryGet(key, out var cached))
            return cached;
        return null;
    }

    public static void Set(string text, bool safe, string reason, string direction = "")
    {
        if (text.Length > 500) return;
        var key = direction.Length > 0 ? $"{direction}:{text}" : text;
        _cache.Set(key, (safe, reason));
    }
}
