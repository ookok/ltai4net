using System.Collections.Concurrent;

namespace LTAI.TUI.Services;

public enum NotificationLevel { Info, Warning, Error }

public sealed record NotificationEntry(string Message, NotificationLevel Level, DateTime Timestamp);

public static class NotificationService
{
    private static readonly ConcurrentQueue<NotificationEntry> _queue = new();
    private const int MaxEntries = 50;

    public static event Action<NotificationEntry>? OnNotification;
    
    public static int Count => _queue.Count;

    public static void Publish(string message, NotificationLevel level = NotificationLevel.Info)
    {
        var entry = new NotificationEntry(message, level, DateTime.UtcNow);
        _queue.Enqueue(entry);
        while (_queue.Count > MaxEntries && _queue.TryDequeue(out _)) { }
        OnNotification?.Invoke(entry);
    }

    public static NotificationEntry? Dequeue()
    {
        if (_queue.TryDequeue(out var entry))
            return entry;
        return null;
    }

    public static IReadOnlyList<NotificationEntry> PeekAll()
    {
        return _queue.ToArray();
    }

    public static void Clear()
    {
        while (_queue.TryDequeue(out _)) { }
    }
}
