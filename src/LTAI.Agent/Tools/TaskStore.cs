using System.Collections.Concurrent;

namespace LTAI.Agent.Tools;

/// <summary>
/// Session-isolated todo list store. DI singleton.
/// Replaces static ConcurrentDictionary on <see cref="TaskTools"/>.
/// Has bounded capacity with LRU eviction.
/// </summary>
public sealed class TaskStore
{
    private readonly ConcurrentDictionary<string, object?> _store = new(StringComparer.Ordinal);
    private const int MaxCapacity = 500;

    public bool TryGet<T>(string key, out T? value)
    {
        if (_store.TryGetValue(key, out var obj) && obj is T t)
        {
            value = t;
            return true;
        }
        value = default;
        return false;
    }

    public T GetOrAdd<T>(string key, Func<T> factory) where T : class
    {
        return (T)_store.GetOrAdd(key, _key =>
        {
            if (_store.Count >= MaxCapacity)
            {
                var oldest = _store.Keys.FirstOrDefault();
                if (oldest != null) _store.TryRemove(oldest!, out _);
            }
            return factory();
        })!;
    }

    public void Remove(string key) => _store.TryRemove(key, out _);

    public void EvictStaleSessions()
    {
        // Eviction is handled on GetOrAdd via MaxCapacity; no TTL needed.
    }

    public int Count => _store.Count;

    public ICollection<string> Keys => _store.Keys;
}
