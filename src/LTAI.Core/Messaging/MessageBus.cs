namespace LTAI.Core.Messaging;

public interface IEventBus
{
    Task<TResponse> SendAsync<TMessage, TResponse>(TMessage message, CancellationToken ct = default)
        where TMessage : notnull;

    Task BroadcastAsync<TMessage>(TMessage message, CancellationToken ct = default)
        where TMessage : notnull;

    void Subscribe<TMessage>(Func<TMessage, CancellationToken, Task> handler)
        where TMessage : notnull;

    void RegisterHandler<TMessage, TResponse>(Func<TMessage, CancellationToken, Task<TResponse>> handler)
        where TMessage : notnull;

    bool HasHandler<TMessage>() where TMessage : notnull;
}

public sealed class EventBus : IEventBus
{
    private readonly Dictionary<Type, Delegate> _handlers = new();
    private readonly Dictionary<Type, List<Delegate>> _subscribers = new();
    private readonly object _lock = new();

    public void RegisterHandler<TMessage, TResponse>(Func<TMessage, CancellationToken, Task<TResponse>> handler)
        where TMessage : notnull
    {
        lock (_lock) { _handlers[typeof(TMessage)] = handler; }
    }

    public void Subscribe<TMessage>(Func<TMessage, CancellationToken, Task> handler)
        where TMessage : notnull
    {
        lock (_lock)
        {
            if (!_subscribers.ContainsKey(typeof(TMessage)))
                _subscribers[typeof(TMessage)] = new();
            _subscribers[typeof(TMessage)].Add(handler);
        }
    }

    public async Task<TResponse> SendAsync<TMessage, TResponse>(TMessage message, CancellationToken ct = default)
        where TMessage : notnull
    {
        Delegate? handler;
        lock (_lock) { _handlers.TryGetValue(typeof(TMessage), out handler); }

        if (handler is Func<TMessage, CancellationToken, Task<TResponse>> typedHandler)
            return await typedHandler(message, ct);

        throw new InvalidOperationException($"No handler registered for {typeof(TMessage).Name}");
    }

    public async Task BroadcastAsync<TMessage>(TMessage message, CancellationToken ct = default)
        where TMessage : notnull
    {
        List<Delegate>? subs;
        lock (_lock) { _subscribers.TryGetValue(typeof(TMessage), out subs); }
        if (subs == null) return;

        foreach (var sub in subs)
        {
            if (sub is Func<TMessage, CancellationToken, Task> handler)
            {
                try { await handler(message, ct); }
                catch (Exception ex)
                {
                    global::System.Diagnostics.Debug.WriteLine($"EventBus handler error: {ex.Message}");
                }
            }
        }
    }

    public bool HasHandler<TMessage>() where TMessage : notnull
    {
        lock (_lock) { return _handlers.ContainsKey(typeof(TMessage)); }
    }
}
