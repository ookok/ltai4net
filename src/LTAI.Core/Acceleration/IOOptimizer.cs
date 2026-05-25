using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace LTAI.Core.Acceleration;

public sealed class LruCache<T>
{
    private readonly int _maxSize;
    private readonly double _ttlSeconds;
    private readonly ConcurrentDictionary<string, CacheEntry<T>> _data = new();
    private long _hits;
    private long _misses;
    private string? _oldestKey;

    private sealed record CacheEntry<TVal>(TVal Value, double Timestamp);

    public LruCache(int maxSize = 1000, double ttlSeconds = 300)
    {
        _maxSize = maxSize;
        _ttlSeconds = ttlSeconds;
    }

    public T? Get(string key)
    {
        if (!_data.TryGetValue(key, out var entry))
        {
            Interlocked.Increment(ref _misses);
            return default;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (now - entry.Timestamp > _ttlSeconds)
        {
            _data.TryRemove(key, out _);
            Interlocked.Increment(ref _misses);
            return default;
        }

        var updated = entry with { Timestamp = now };
        _data.TryUpdate(key, updated, entry);
        Interlocked.Increment(ref _hits);
        return entry.Value;
    }

    public void Set(string key, T value)
    {
        _data[key] = new CacheEntry<T>(value, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        if (_data.Count > _maxSize)
        {
            var oldestKey = _oldestKey;
            if (oldestKey != null && _data.TryRemove(oldestKey, out _))
            {
                _oldestKey = null;
                return;
            }

            double oldestTs = double.MaxValue;
            string? foundKey = null;
            foreach (var kvp in _data)
            {
                if (kvp.Value.Timestamp < oldestTs)
                {
                    oldestTs = kvp.Value.Timestamp;
                    foundKey = kvp.Key;
                }
            }
            if (foundKey != null)
                _data.TryRemove(foundKey, out _);
        }
    }

    public Dictionary<string, object> GetStats()
    {
        var total = _hits + _misses;
        return new Dictionary<string, object>
        {
            ["size"] = _data.Count,
            ["max_size"] = _maxSize,
            ["hits"] = _hits,
            ["misses"] = _misses,
            ["hit_rate"] = total > 0 ? (double)_hits / total : 0
        };
    }

    public void Clear() => _data.Clear();
}

public sealed class EmbeddingCache
{
    private readonly LruCache<float[]> _cache;

    public EmbeddingCache(int maxSize = 10000)
    {
        _cache = new LruCache<float[]>(maxSize, 3600);
    }

    public string GetEmbeddingKey(string text)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..24];
    }

    public float[]? Get(string text) => _cache.Get(GetEmbeddingKey(text));
    public void Set(string text, float[] embedding) => _cache.Set(GetEmbeddingKey(text), embedding);
    public Dictionary<string, object> GetStats() => _cache.GetStats();
}

public sealed class BatchWriter
{
    private readonly double _flushInterval;
    private readonly int _maxPending;
    private readonly ConcurrentDictionary<string, string> _pending = new();
    private double _lastFlush;

    public BatchWriter(double flushInterval = 5.0, int maxPending = 100)
    {
        _flushInterval = flushInterval;
        _maxPending = maxPending;
        _lastFlush = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    public void Write(string path, string content)
    {
        _pending[path] = content;
        if (_pending.Count >= _maxPending)
            Flush();
    }

    public void Flush()
    {
        if (_pending.IsEmpty) return;

        var snapshot = new Dictionary<string, string>();
        foreach (var kvp in _pending)
        {
            if (_pending.TryRemove(kvp.Key, out var content))
                snapshot[kvp.Key] = content;
        }

        foreach (var (path, content) in snapshot)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (dir != null) Directory.CreateDirectory(dir);
                File.WriteAllText(path, content);
            }
            catch
            {
            }
        }

        _lastFlush = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    public async Task StartAutoFlushAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(_flushInterval), cancellationToken).ConfigureAwait(false);
            Flush();
        }
    }
}

public static class IOUtils
{
    public static string SmartRead(string path, int maxMemoryMb = 50)
    {
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists) return "";

        var sizeMb = fileInfo.Length / (1024.0 * 1024.0);
        if (sizeMb < 10)
            return File.ReadAllText(path);

        if (sizeMb < maxMemoryMb)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var mmap = global::System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(
                    fs, null, 0, global::System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read, HandleInheritability.None, false);
                using var stream = mmap.CreateViewStream(0, 0);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch
            {
                return File.ReadAllText(path);
            }
        }

        var chunks = new List<string>();
        using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var streamReader = new StreamReader(fileStream);
        var buffer = new char[128 * 1024];
        var chunkCount = 0;
        while (chunkCount < 100)
        {
            var read = streamReader.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            chunks.Add(new string(buffer, 0, read));
            chunkCount++;
        }

        return string.Join("\n---CHUNK---\n", chunks);
    }

    public static async Task<List<string>> StreamReadAsync(string path, int chunkSize = 65536)
    {
        var chunks = new List<string>();
        var size = new FileInfo(path).Length;
        if (size < chunkSize * 2)
        {
            var content = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            chunks.Add(content);
            return chunks;
        }

        var buffer = new char[chunkSize];
        using var reader = new StreamReader(path);
        while (true)
        {
            var read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            if (read == 0) break;
            chunks.Add(new string(buffer, 0, read));
        }

        return chunks;
    }
}
