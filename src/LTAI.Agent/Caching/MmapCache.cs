using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Caching;

public sealed class MmapCache : IDisposable
{
    private sealed class MappedEntry
    {
        public string Path { get; }
        public MemoryMappedFile Mmap { get; }
        public MemoryMappedViewAccessor Accessor { get; }
        public long Length { get; }
        public int AccessCount;
        public DateTime LastAccess;
        public readonly DateTime CreatedAt = DateTime.UtcNow;
        public readonly byte[] FileVersion;

        public MappedEntry(string path, MemoryMappedFile mmap, MemoryMappedViewAccessor accessor, long length, byte[] version)
        {
            Path = path; Mmap = mmap; Accessor = accessor; Length = length; FileVersion = version;
            AccessCount = 1; LastAccess = DateTime.UtcNow;
        }
    }

    private readonly ConcurrentDictionary<string, MappedEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _accessCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _lastInvalidate = new(StringComparer.OrdinalIgnoreCase);
    private readonly MmapCacheOptions _opts;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private long _totalBytes;
    private bool _disposed;

    public MmapCache(MmapCacheOptions? opts = null, ILogger<MmapCache>? logger = null)
    {
        _opts = opts ?? new MmapCacheOptions();
        _logger = logger ?? NullLogger<MmapCache>.Instance;

        if (_opts.WatchDirectories is { Length: > 0 })
        {
            foreach (var dir in _opts.WatchDirectories)
            {
                if (Directory.Exists(dir))
                    StartWatching(dir);
            }
        }
    }

    public int CachedCount => _cache.Count;
    public long TotalBytes => _totalBytes;

    public string? ReadAllText(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var fi = new FileInfo(path);
        if (!fi.Exists) return null;

        // Fast path: cached
        if (_cache.TryGetValue(path, out var entry))
        {
            Interlocked.Increment(ref entry.AccessCount);
            entry.LastAccess = DateTime.UtcNow;
            return ReadFromAccessor(entry.Accessor, entry.Length);
        }

        // Slow path: read from disk
        string? content = null;
        try { content = File.ReadAllText(path); }
        catch { return null; }

        // Track access count
        var count = _accessCounts.AddOrUpdate(path, 1, (_, v) => v + 1);

        // Decide whether to cache
        if (count >= _opts.MinReadsForCache && fi.Length <= _opts.MaxFileSize && fi.Length > 0)
        {
            TryAddEntry(path, fi);
        }

        return content;
    }

    public Stream? OpenReadStream(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!File.Exists(path)) return null;

        if (_cache.TryGetValue(path, out var entry))
        {
            Interlocked.Increment(ref entry.AccessCount);
            entry.LastAccess = DateTime.UtcNow;
            return new MemoryStream(ReadFromAccessorBytes(entry.Accessor, entry.Length));
        }

        try { return File.OpenRead(path); }
        catch { return null; }
    }

    public void Invalidate(string path)
    {
        var now = Stopwatch.GetTimestamp();
        var debounceTicks = _opts.WatchDebounceMs * TimeSpan.TicksPerMillisecond;
        if (_lastInvalidate.TryGetValue(path, out var last) && (now - last) < debounceTicks)
            return;
        _lastInvalidate[path] = now;

        if (_cache.TryRemove(path, out var entry))
        {
            Interlocked.Add(ref _totalBytes, -entry.Length);
            entry.Accessor.Dispose();
            entry.Mmap.Dispose();
        }
    }

    public void StartWatching(string directoryPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_watchers.ContainsKey(directoryPath)) return;

        try
        {
            var watcher = new FileSystemWatcher(directoryPath)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                IncludeSubdirectories = true,
                EnableRaisingEvents = true
            };

            watcher.Changed += OnFileChanged;
            watcher.Created += OnFileChanged;
            watcher.Deleted += OnFileDeleted;
            watcher.Renamed += OnFileRenamed;
            watcher.Error += OnWatcherError;

            if (_watchers.TryAdd(directoryPath, watcher))
            {
                _logger.LogDebug("MmapCache: started watching {Dir}", directoryPath);
            }
            else
            {
                watcher.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MmapCache: failed to start watching {Dir}", directoryPath);
        }
    }

    public void StopWatching(string directoryPath)
    {
        if (_watchers.TryRemove(directoryPath, out var watcher))
        {
            try { watcher.EnableRaisingEvents = false; watcher.Dispose(); } catch
            {
                _logger?.LogWarning("Swallowing exception in MmapCache.cs");
            }
            _logger.LogDebug("MmapCache: stopped watching {Dir}", directoryPath);
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (_disposed) return;
        Invalidate(e.FullPath);
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        if (_disposed) return;
        Invalidate(e.FullPath);
        _accessCounts.TryRemove(e.FullPath, out _);
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        if (_disposed) return;
        Invalidate(e.OldFullPath);
        Invalidate(e.FullPath);
        _accessCounts.TryRemove(e.OldFullPath, out _);
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        _logger.LogWarning(e.GetException(), "MmapCache: FileSystemWatcher error");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Stop all watchers first
        foreach (var kv in _watchers)
        {
            try { kv.Value.EnableRaisingEvents = false; kv.Value.Dispose(); } catch
            {
                _logger?.LogWarning("Swallowing exception in MmapCache.cs");
            }
        }
        _watchers.Clear();

        foreach (var entry in _cache.Values)
        {
            try { entry.Accessor.Dispose(); entry.Mmap.Dispose(); } catch
            {
                _logger?.LogWarning("Swallowing exception in MmapCache.cs");
            }
        }
        _cache.Clear();
        _accessCounts.Clear();
        _totalBytes = 0;
    }

    private void TryAddEntry(string path, FileInfo fi)
    {
        lock (_lock)
        {
            if (_cache.ContainsKey(path)) return;
            if (_cache.Count >= _opts.MaxCachedFiles || _totalBytes + fi.Length > _opts.MaxTotalBytes)
                EvictOne();

            try
            {
                var fileBytes = File.ReadAllBytes(path);
                var version = fileBytes.Length > 0
                    ? System.Security.Cryptography.SHA256.HashData(fileBytes)[..8]
                    : [];

                var mmap = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
                var accessor = mmap.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                var entry = new MappedEntry(path, mmap, accessor, fi.Length, version);

                if (_cache.TryAdd(path, entry))
                {
                    Interlocked.Add(ref _totalBytes, fi.Length);
                    _logger.LogDebug("MmapCache: cached {Path} ({Len}KB, {Total}KB total)",
                        path, fi.Length / 1024, _totalBytes / 1024);
                }
                else
                {
                    accessor.Dispose();
                    mmap.Dispose();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MmapCache: failed to cache {Path}", path);
            }
        }
    }

    private void EvictOne()
    {
        MappedEntry? victim = null;
        double lowestScore = double.MaxValue;

        foreach (var entry in _cache.Values)
        {
            var age = (DateTime.UtcNow - entry.LastAccess).TotalSeconds;
            var score = entry.AccessCount / Math.Max(age, 1);
            if (score < lowestScore)
            {
                lowestScore = score;
                victim = entry;
            }
        }

        if (victim != null)
        {
            _cache.TryRemove(victim.Path, out _);
            Interlocked.Add(ref _totalBytes, -victim.Length);
            try { victim.Accessor.Dispose(); victim.Mmap.Dispose(); } catch
            {
                _logger?.LogWarning("Swallowing exception in MmapCache.cs");
            }
            _logger.LogDebug("MmapCache: evicted {Path}", victim.Path);
        }
    }

    private static unsafe string ReadFromAccessor(MemoryMappedViewAccessor accessor, long length)
    {
        byte* ptr = null;
        try
        {
            accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            return System.Text.Encoding.UTF8.GetString(ptr, (int)length);
        }
        finally
        {
            if (ptr != null) accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }

    private static unsafe byte[] ReadFromAccessorBytes(MemoryMappedViewAccessor accessor, long length)
    {
        byte* ptr = null;
        try
        {
            accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            var buf = new byte[length];
            Marshal.Copy((nint)ptr, buf, 0, (int)length);
            return buf;
        }
        finally
        {
            if (ptr != null) accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }
}
