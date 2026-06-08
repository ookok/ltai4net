// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  StepContext — data context between workflow steps
//
//  Phase 2b: a ConcurrentDictionary scoped to a single execution.
//  Steps read/write the context to pass intermediate results.
//
//  Thread-safe: ConcurrentDictionary + ImmutableList for child scopes.
// ═══════════════════════════════════════════════════════════════

using System.Collections.Concurrent;

namespace LTAI.Agent.Execution;

/// <summary>
/// Data context scoped to a single execution. Steps read/write
/// properties to communicate intermediate results. Thread-safe.
/// </summary>
public sealed class StepContext
{
    private readonly ConcurrentDictionary<string, object?> _properties = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Set a property value.</summary>
    public void Set(string key, object? value)
        => _properties[key] = value;

    /// <summary>Try to get a property value.</summary>
    public bool TryGet<T>(string key, out T? value)
    {
        if (_properties.TryGetValue(key, out var obj) && obj is T t)
        {
            value = t;
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>Get a property value, or default if not found.</summary>
    public T? Get<T>(string key) where T : class
        => _properties.TryGetValue(key, out var obj) ? obj as T : null;

    /// <summary>Get a property value as string, or null.</summary>
    public string? GetString(string key)
        => _properties.TryGetValue(key, out var obj) ? obj?.ToString() : null;

    /// <summary>Get or add a property value.</summary>
    public T GetOrAdd<T>(string key, Func<string, T> factory) where T : class
        => (T?)_properties.GetOrAdd(key, k => factory(k))!;

    /// <summary>Remove a property.</summary>
    public bool Remove(string key)
        => _properties.TryRemove(key, out _);

    /// <summary>Remove all properties.</summary>
    public void Clear()
        => _properties.Clear();

    /// <summary>Copy all properties into a new dictionary (for snapshotting).</summary>
    public IReadOnlyDictionary<string, object?> Snapshot()
        => new Dictionary<string, object?>(_properties, StringComparer.OrdinalIgnoreCase);
}
