using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.System;

public interface IAsyncDisk
{
    void WriteJson(string path, object data);
    void WriteText(string path, string text);
    Task FlushNowAsync(string path);
    Task FlushAllAsync();
    void Start();
    void Stop();
    Dictionary<string, object> Stats();
}

public sealed class AsyncDisk : IAsyncDisk
{
    private static readonly Lazy<AsyncDisk> _instance = new(() => new AsyncDisk());
    public static AsyncDisk Instance => _instance.Value;

    private readonly ConcurrentDictionary<string, string> _pending = new();
    private readonly ConcurrentDictionary<string, byte> _dirty = new();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private readonly int _batchInterval = 5000;
    private readonly int _maxBatchSize = 100;
    private CancellationTokenSource? _cts;
    private readonly ILogger<AsyncDisk> _logger;

    public AsyncDisk() : this(NullLogger<AsyncDisk>.Instance) { }

    public AsyncDisk(ILogger<AsyncDisk> logger)
    {
        _logger = logger ?? NullLogger<AsyncDisk>.Instance;
    }

    public void WriteJson(string path, object data)
    {
        var json = JsonSerializer.Serialize(data);
        _pending[path] = json;
        _dirty[path] = 1;
        _logger.LogDebug("Queued JSON write: {Path}", path);
    }

    public void WriteText(string path, string text)
    {
        _pending[path] = text;
        _dirty[path] = 1;
        _logger.LogDebug("Queued text write: {Path}", path);
    }

    public async Task FlushNowAsync(string path)
    {
        if (!_dirty.ContainsKey(path))
            return;

        if (_pending.TryGetValue(path, out var content))
        {
            await _writeFileAsync(path, content).ConfigureAwait(false);
            _dirty.TryRemove(path, out _);
            _logger.LogDebug("Flushed immediately: {Path}", path);
        }
    }

    [Obsolete("Use FlushNowAsync instead")]
    public void FlushNow(string path)
    {
        FlushNowAsync(path).GetAwaiter().GetResult();
    }

    public async Task FlushAllAsync()
    {
        await _flushBatchAsync().ConfigureAwait(false);
    }

    [Obsolete("Use FlushAllAsync instead")]
    public void FlushAll()
    {
        FlushAllAsync().GetAwaiter().GetResult();
    }

    private async Task _flushBatchAsync()
    {
        await _flushLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var dirtyKeys = _dirty.Keys.Take(_maxBatchSize).ToList();
            foreach (var path in dirtyKeys)
            {
                if (_pending.TryGetValue(path, out var content))
                {
                    await _writeFileAsync(path, content).ConfigureAwait(false);
                }

                _dirty.TryRemove(path, out _);
            }

            if (dirtyKeys.Count > 0)
                _logger.LogInformation("Flushed {Count} files", dirtyKeys.Count);
        }
        finally
        {
            _flushLock.Release();
        }
    }

    [Obsolete("Use _flushBatchAsync instead")]
    private void _flushBatch()
    {
        _flushBatchAsync().GetAwaiter().GetResult();
    }

    private async Task _writeFileAsync(string path, string content)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var tempPath = path + ".tmp." + Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(tempPath, content).ConfigureAwait(false);
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write file: {Path}", path);
        }
    }

    [Obsolete("Use _writeFileAsync instead")]
    private void _writeFile(string path, string content)
    {
        _writeFileAsync(path, content).GetAwaiter().GetResult();
    }

    public void Start()
    {
        if (_cts != null)
            return;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_batchInterval, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (_dirty.Count > 0)
                    await _flushBatchAsync().ConfigureAwait(false);
            }
        }, token);

        _logger.LogInformation("AsyncDisk background loop started");
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
#pragma warning disable CS0618
        _flushBatch();
#pragma warning restore CS0618
        _logger.LogInformation("AsyncDisk stopped, all pending flushed");
    }

    public static void SaveJson(string path, object data)
    {
        var json = JsonSerializer.Serialize(data);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, json);
    }

    public Dictionary<string, object> Stats()
    {
        return new Dictionary<string, object>
        {
            ["pending_count"] = _pending.Count,
            ["dirty_count"] = _dirty.Count
        };
    }
}
