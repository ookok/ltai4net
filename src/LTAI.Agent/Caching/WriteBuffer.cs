using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Caching;

public sealed class WriteBuffer : IDisposable
{
    private sealed class DirtyEntry
    {
        public readonly string Path;
        public readonly string TmpPath;
        public string Content;
        public long LastWriteTimestamp;
        public Task? PendingFlush;

        public DirtyEntry(string path, string tmpPath, string content)
        {
            Path = path;
            TmpPath = tmpPath;
            Content = content;
            LastWriteTimestamp = Stopwatch.GetTimestamp();
        }
    }

    private readonly ConcurrentDictionary<string, DirtyEntry> _dirty = new(StringComparer.OrdinalIgnoreCase);
    private readonly WriteBufferOptions _opts;
    private readonly MmapCache? _mmap;
    private readonly ILogger _logger;
    private readonly Timer _timer;
    private readonly object _flushLock = new();
    private bool _disposed;

    public WriteBuffer(WriteBufferOptions? opts = null, MmapCache? mmap = null, ILogger<WriteBuffer>? logger = null)
    {
        _opts = opts ?? new WriteBufferOptions();
        _mmap = mmap;
        _logger = logger ?? NullLogger<WriteBuffer>.Instance;
        _timer = new Timer(_ => FlushDue(), null, _opts.FlushIntervalMs, _opts.FlushIntervalMs);
    }

    public int DirtyCount => _dirty.Count;

    public Task WriteAsync(string path, string content, bool flush = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Fast path: buffer in memory
        var created = false;
        var entry = _dirty.AddOrUpdate(path,
            _ =>
            {
                created = true;
                return new DirtyEntry(path, path + ".tmp." + Guid.NewGuid().ToString("N")[..8], content)
                {
                    LastWriteTimestamp = Stopwatch.GetTimestamp()
                };
            },
            (_, existing) =>
            {
                existing.Content = content;
                existing.LastWriteTimestamp = Stopwatch.GetTimestamp();
                return existing;
            });

        // If limit exceeded, flush the oldest (async, no await)
        if (!created && _dirty.Count > _opts.MaxPending)
        {
            FlushOneIfDue(path, entry);
        }

        if (flush)
            return FlushOneAsync(path, entry);

        return Task.CompletedTask;
    }

    public Task FlushAsync(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _dirty.TryGetValue(path, out var entry)
            ? FlushOneAsync(path, entry)
            : Task.CompletedTask;
    }

    public Task FlushAllAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var tasks = new List<Task>(_dirty.Count);
        foreach (var kv in _dirty)
            tasks.Add(FlushOneAsync(kv.Key, kv.Value));
        return Task.WhenAll(tasks);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _timer.Dispose(); } catch
        {
            _logger?.LogWarning("Swallowing exception in WriteBuffer.cs");
        }
        FlushAllSync();
        _dirty.Clear();
    }

    private void FlushAllSync()
    {
        foreach (var kv in _dirty)
        {
            try
            {
                var dir = Path.GetDirectoryName(kv.Value.Path);
                if (dir != null) Directory.CreateDirectory(dir);
                File.WriteAllText(kv.Value.Path, kv.Value.Content, _opts.Encoding);
                _mmap?.Invalidate(kv.Value.Path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WriteBuffer: sync flush failed for {Path}", kv.Value.Path);
            }
        }
    }

    private void FlushDue()
    {
        if (_disposed) return;
        var now = Stopwatch.GetTimestamp();
        var maxAgeTicks = _opts.FlushIntervalMs * TimeSpan.TicksPerMillisecond;

        foreach (var kv in _dirty)
        {
            if ((now - kv.Value.LastWriteTimestamp) >= maxAgeTicks)
                FlushOneIfDue(kv.Key, kv.Value);
        }
    }

    private void FlushOneIfDue(string path, DirtyEntry entry)
    {
        lock (_flushLock)
        {
            if (entry.PendingFlush is { IsCompleted: false }) return;
            if (!_dirty.TryGetValue(path, out var current) || current != entry) return;

            entry.PendingFlush = FlushOneCore(entry);
        }
    }

    private async Task FlushOneAsync(string path, DirtyEntry entry)
    {
        Task task;
        lock (_flushLock)
        {
            if (entry.PendingFlush is { IsCompleted: false })
                task = entry.PendingFlush;
            else
            {
                entry.PendingFlush = FlushOneCore(entry);
                task = entry.PendingFlush;
            }
        }
        await task.ConfigureAwait(false);
    }

    private async Task FlushOneCore(DirtyEntry entry)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(entry.Path)!);

            if (_opts.AtomicWrites)
            {
                await File.WriteAllTextAsync(entry.TmpPath, entry.Content, _opts.Encoding).ConfigureAwait(false);
                File.Move(entry.TmpPath, entry.Path, overwrite: true);
            }
            else
            {
                await File.WriteAllTextAsync(entry.Path, entry.Content, _opts.Encoding).ConfigureAwait(false);
            }

            _mmap?.Invalidate(entry.Path);
            _dirty.TryRemove(entry.Path, out _);

            _logger.LogTrace("WriteBuffer: flushed {Path}", entry.Path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WriteBuffer: flush failed for {Path}", entry.Path);
            throw;
        }
    }
}
