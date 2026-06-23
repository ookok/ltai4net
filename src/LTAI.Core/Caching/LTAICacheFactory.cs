namespace LTAI.Core.Caching;

public sealed class LTAICacheFactory
{
    private readonly Dictionary<string, object> _caches = new(StringComparer.Ordinal);

    public LTAICache<TKey, TValue> GetOrCreate<TKey, TValue>(
        string name,
        LTAICacheOptions? options = null)
        where TKey : notnull
    {
        lock (_caches)
        {
            if (_caches.TryGetValue(name, out var existing))
                return (LTAICache<TKey, TValue>)existing;

            var cache = new LTAICache<TKey, TValue>(options);
            _caches[name] = cache;
            return cache;
        }
    }

    public void ClearAll()
    {
        lock (_caches)
        {
            foreach (var cache in _caches.Values)
            {
                if (cache is IDisposable d) d.Dispose();
            }
            _caches.Clear();
        }
    }
}
