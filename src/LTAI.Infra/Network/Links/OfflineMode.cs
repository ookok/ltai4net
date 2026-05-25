using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LTAI.Infra.Network.Links;

public sealed record SyncQueueItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Action { get; init; } = string.Empty;
    public string Data { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public int RetryCount { get; init; }
    public int MaxRetries { get; init; } = 5;
}

public sealed class DualMode : IDisposable
{
    private static readonly Lazy<DualMode> _instance = new(() => new DualMode());
    public static DualMode Instance => _instance.Value;

    private bool _isOnline = true;
    private readonly object _onlineLock = new();
    private readonly ConcurrentQueue<SyncQueueItem> _queue = new();
    private int _queueCount;
    private readonly object _queueLock = new();
    private readonly HttpClient _http;
    private readonly ILogger<DualMode>? _logger;
    private const int MaxQueueSize = 1000;

    private static readonly string[] HeartbeatUrls =
    [
        "https://api.deepseek.com",
        "https://google.com/generate_204"
    ];

    private DualMode()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        _logger = null;
    }

    public void Dispose() { _http?.Dispose(); }

    public async Task Check()
    {
        var online = false;

        foreach (var url in HeartbeatUrls)
        {
            if (await Ping(url))
            {
                online = true;
                break;
            }
        }

        var wasOnline = IsOnline();
        lock (_onlineLock)
        {
            _isOnline = online;
        }

        if (!wasOnline && online)
        {
            _logger?.LogInformation("Network restored, triggering reconnect sync");
            _ = SyncOnReconnect();
        }

        _logger?.LogDebug("Connectivity check: {Status}", online ? "online" : "offline");
    }

    public bool IsOnline()
    {
        lock (_onlineLock)
        {
            return _isOnline;
        }
    }

    public async Task StartMonitoring(int intervalMs)
    {
        _logger?.LogInformation("Starting connectivity monitoring at {Interval}ms", intervalMs);

        while (true)
        {
            await Task.Delay(intervalMs).ConfigureAwait(false);
            await Check().ConfigureAwait(false);
        }
    }

    public bool QueueSync(string action, string data)
    {
        if (IsOnline())
            return false;

        lock (_queueLock)
        {
            if (_queueCount >= MaxQueueSize)
            {
                _logger?.LogWarning("Sync queue full ({Count}/{Max})", _queueCount, MaxQueueSize);
                return false;
            }

            _queueCount++;
        }

        var item = new SyncQueueItem
        {
            Action = action,
            Data = data
        };

        _queue.Enqueue(item);
        _logger?.LogInformation("Queued sync item: {Id} ({Action})", item.Id, action);
        return true;
    }

    public async Task<int> SyncOnReconnect()
    {
        var count = 0;

        while (_queue.TryDequeue(out var item))
        {
            lock (_queueLock)
            {
                _queueCount--;
            }

            await SyncItem(item).ConfigureAwait(false);
            count++;
        }

        _logger?.LogInformation("Reconnect sync completed: {Count} items processed", count);
        return count;
    }

    public int GetPendingCount()
    {
        lock (_queueLock)
        {
            return _queueCount;
        }
    }

    public (bool IsOnline, int PendingCount, int QueueLimit) GetStatus()
    {
        return (IsOnline(), GetPendingCount(), MaxQueueSize);
    }

    public void ClearQueue()
    {
        while (_queue.TryDequeue(out _))
        {
            lock (_queueLock)
            {
                _queueCount--;
            }
        }

        _logger?.LogInformation("Sync queue cleared");
    }

    private async Task SyncItem(SyncQueueItem item)
    {
        try
        {
            if (item.RetryCount >= item.MaxRetries)
            {
                _logger?.LogWarning("Sync item exceeded max retries: {Id}", item.Id);
                return;
            }

            await Task.Delay(10).ConfigureAwait(false);

            var updated = item with { RetryCount = item.RetryCount + 1 };
            _logger?.LogDebug("Sync item processed: {Id} ({Action})", item.Id, updated.Action);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Sync item failed: {Id}", item.Id);
        }
    }

    private async Task<bool> Ping(string url)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            var response = await _http.SendAsync(request).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
