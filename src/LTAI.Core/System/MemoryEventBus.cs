using System.Collections.Concurrent;

namespace LTAI.Core.System;

/// <summary>Categories of memory events for cross-component synchronization.</summary>
public enum MemoryEventType
{
    NodeAdded,
    NodeRemoved,
    NodePruned,
    KnowledgeAdded,
    DecisionRouted,
    BuildIteration,
    AuditEntry
}

/// <summary>Payload for a memory event.</summary>
public sealed record MemoryEvent
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public MemoryEventType Type { get; init; }
    public string Source { get; init; } = "";
    public string Detail { get; init; } = "";
    public Dictionary<string, object?> Metadata { get; init; } = new();
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>Cross-component event bus for memory/knowledge synchronization.</summary>
public interface IMemoryEventBus
{
    void Publish(MemoryEvent evt);
    IDisposable Subscribe(MemoryEventType type, Action<MemoryEvent> handler);
    IDisposable SubscribeAll(Action<MemoryEvent> handler);
}

public sealed class MemoryEventBus : IMemoryEventBus
{
    private readonly ConcurrentDictionary<MemoryEventType, List<Action<MemoryEvent>>> _handlers = new();
    private readonly List<Action<MemoryEvent>> _allHandlers = new();
    private readonly object _lock = new();

    public void Publish(MemoryEvent evt)
    {
        // Notify type-specific handlers
        if (_handlers.TryGetValue(evt.Type, out var typeHandlers))
        {
            foreach (var handler in typeHandlers)
            {
                try { handler(evt); }
                catch { /* isolate handler failures */ }
            }
        }

        // Notify all-event handlers
        foreach (var handler in _allHandlers)
        {
            try { handler(evt); }
            catch { /* isolate handler failures */ }
        }
    }

    public IDisposable Subscribe(MemoryEventType type, Action<MemoryEvent> handler)
    {
        _handlers.AddOrUpdate(type,
            _ => new List<Action<MemoryEvent>> { handler },
            (_, list) => { list.Add(handler); return list; });

        return new Unsubscriber(() =>
        {
            if (_handlers.TryGetValue(type, out var list))
                list.Remove(handler);
        });
    }

    public IDisposable SubscribeAll(Action<MemoryEvent> handler)
    {
        lock (_lock) { _allHandlers.Add(handler); }
        return new Unsubscriber(() => { lock (_lock) { _allHandlers.Remove(handler); } });
    }

    private sealed class Unsubscriber(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
