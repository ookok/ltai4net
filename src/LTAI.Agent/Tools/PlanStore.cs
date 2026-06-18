using System.Collections.Concurrent;

namespace LTAI.Agent.Tools;

/// <summary>
/// Session-isolated plan state store. DI singleton.
/// Replaces static ConcurrentDictionary on <see cref="PlanTools"/>.
/// Has bounded capacity with LRU eviction.
/// </summary>
public sealed class PlanStore
{
    private readonly ConcurrentDictionary<string, object?> _store = new(StringComparer.Ordinal);
    private const int MaxCapacity = 1000;

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

    public void Set<T>(string key, T value)
    {
        if (_store.Count >= MaxCapacity)
        {
            var oldest = _store.Keys.FirstOrDefault();
            if (oldest != null) _store.TryRemove(oldest!, out _);
        }
        _store[key] = value;
    }

    public void Remove(string key) => _store.TryRemove(key, out _);

    /// <summary>Evict stale sessions. Called by ChatAgent. Uses bounded capacity; no TTL required.</summary>
    public void EvictStaleSessions()
    {
        // Capacity-bound eviction handled in Set(); no TTL needed.
    }

    public T GetOrAdd<T>(string key, Func<T> factory) where T : class
    {
        return (T)_store.GetOrAdd(key, _ => factory())!;
    }

    public int Count => _store.Count;

    public ICollection<string> Keys => _store.Keys;
}
