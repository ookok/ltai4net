using System.Collections.Concurrent;

namespace LTAI.Desktop.Services;

public enum NotificationLevel { Info, Warning, Error }

public sealed record DesktopNotification(string Message, NotificationLevel Level, DateTime Timestamp);

public sealed class NotificationService
{
    private readonly ConcurrentQueue<DesktopNotification> _queue = new();
    private const int MaxEntries = 100;

    public event Action<DesktopNotification>? OnNotification;
    public int Count => _queue.Count;

    public void Publish(string message, NotificationLevel level = NotificationLevel.Info)
    {
        var entry = new DesktopNotification(message, level, DateTime.UtcNow);
        _queue.Enqueue(entry);
        while (_queue.Count > MaxEntries && _queue.TryDequeue(out _)) { }
        var handler = OnNotification;
        if (handler != null)
        {
            foreach (Action<DesktopNotification> h in handler.GetInvocationList())
            {
                try { h(entry); } catch { }
            }
        }
    }

    public DesktopNotification? Dequeue()
    {
        return _queue.TryDequeue(out var entry) ? entry : null;
    }

    public IReadOnlyList<DesktopNotification> PeekAll() => _queue.ToArray();

    public void Clear()
    {
        while (_queue.TryDequeue(out _)) { }
    }
}
