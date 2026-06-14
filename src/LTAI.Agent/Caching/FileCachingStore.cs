// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  FileCachingStore — Tier 2: persistent JSON file checkpoint store
//
//  Zero new dependencies. Uses System.Text.Json for serialization
//  + atomic file write (tmp + rename) for crash safety.
//
//  AOT-compatible: no dynamic code generation, no IL2050 warnings.
//
//  Cascade: MemoryCachingStore → FileCachingStore → NullCachingStore
// ═══════════════════════════════════════════════════════════════

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Caching;

/// <summary>
/// JSON file-backed persistent checkpoint store. Tier 2 of the
/// Memory Caching Layer cascade. Survives process restart.
///
/// Architecture:
///   - Single JSON file: .livingtree/memory-checkpoints.json
///   - Atomic write: serialize → .tmp → File.Move(tmp → target)
///   - Full file read on startup, full file write on each change
///   - In-memory cache avoids re-reading for Lookup/FindNearest
/// </summary>
public sealed class FileCachingStore : IMemoryCachingStore
{
    private readonly string _filePath;
    private readonly ILogger<FileCachingStore> _logger;
    private readonly object _gate = new();
    private Dictionary<string, CheckpointFileEntry> _entries;
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        // AOT-compatible: no dynamic converter generation
        TypeInfoResolver = CheckpointSerializerContext.Default,
    };

    public string ActiveTier => "File";
    public int CheckpointCount
    {
        get { lock (_gate) return _entries.Count; }
    }

    public FileCachingStore(
        string dataDir,
        ILogger<FileCachingStore>? logger = null)
    {
        Directory.CreateDirectory(dataDir);
        _filePath = Path.Combine(dataDir, "memory-checkpoints.json");
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<FileCachingStore>.Instance;
        _entries = LoadFromDisk();
    }

    /// <summary>
    /// Internal: override the constructor for testing (no disk I/O).
    /// </summary>
    internal FileCachingStore(Dictionary<string, CheckpointFileEntry> entries)
    {
        _filePath = Path.Combine(Path.GetTempPath(), $"_test_{Guid.NewGuid():n}.json");
        _logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<FileCachingStore>.Instance;
        _entries = entries;
    }

    // ═══════════════════════════════════════════
    //  IMemoryCachingStore implementation
    // ═══════════════════════════════════════════

    public Task StoreAsync(string key, byte[] data, long tokenCount, CancellationToken ct = default)
    {
        if (_disposed) return Task.CompletedTask;

        lock (_gate)
        {
            _entries[key] = new CheckpointFileEntry(
                Convert.ToBase64String(data),
                tokenCount,
                DateTime.UtcNow,
                DateTime.UtcNow.AddHours(24));

            FlushToDisk();
        }

        return Task.CompletedTask;
    }

    public Task<byte[]?> LookupAsync(string key, CancellationToken ct = default)
    {
        if (_disposed) return Task.FromResult<byte[]?>(null);

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                // Check TTL
                if (entry.ExpiresAt >= DateTime.UtcNow)
                    return Task.FromResult<byte[]?>(Convert.FromBase64String(entry.DataBase64));

                // Expired — remove
                _entries.Remove(key);
            }
        }

        return Task.FromResult<byte[]?>(null);
    }

    public Task<(string key, byte[] data, long tokenCount)?> FindNearestAsync(
        string sessionId, long tokenCount, CancellationToken ct = default)
    {
        if (_disposed) return Task.FromResult<(string, byte[], long)?>(null);

        var prefix = $"session:{sessionId}:";

        lock (_gate)
        {
            var nearest = _entries
                .Where(kv => kv.Key.StartsWith(prefix)
                             && kv.Value.TokenCount <= tokenCount
                             && kv.Value.ExpiresAt >= DateTime.UtcNow)
                .OrderByDescending(kv => kv.Value.TokenCount)
                .FirstOrDefault();

            if (nearest.Key == null)
                return Task.FromResult<(string, byte[], long)?>(null);

            return Task.FromResult<(string, byte[], long)?>((
                nearest.Key,
                Convert.FromBase64String(nearest.Value.DataBase64),
                nearest.Value.TokenCount));
        }
    }

    public Task<IReadOnlyList<CheckpointSummary>> FindRangeAsync(
        string sessionId, long fromToken, long toToken, CancellationToken ct = default)
    {
        if (_disposed)
            return Task.FromResult<IReadOnlyList<CheckpointSummary>>([]);

        var prefix = $"session:{sessionId}:";

        lock (_gate)
        {
            var results = _entries
                .Where(kv => kv.Key.StartsWith(prefix)
                             && kv.Value.TokenCount >= fromToken
                             && kv.Value.TokenCount <= toToken
                             && kv.Value.ExpiresAt >= DateTime.UtcNow)
                .Select(kv => new CheckpointSummary(
                    kv.Key, kv.Value.TokenCount, kv.Value.SavedAt, "File"))
                .OrderBy(s => s.TokenCount)
                .ToList();

            return Task.FromResult<IReadOnlyList<CheckpointSummary>>(results);
        }
    }

    public Task InvalidateSessionAsync(string sessionId, CancellationToken ct = default)
    {
        if (_disposed) return Task.CompletedTask;
        var prefix = $"session:{sessionId}:";

        lock (_gate)
        {
            var keys = _entries.Keys.Where(k => k.StartsWith(prefix)).ToList();
            foreach (var key in keys) _entries.Remove(key);
            FlushToDisk();
        }

        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        if (_disposed) return Task.CompletedTask;

        lock (_gate)
        {
            _entries.Clear();
            FlushToDisk();
        }

        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════
    //  Persistence
    // ═══════════════════════════════════════════

    private Dictionary<string, CheckpointFileEntry> LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                _logger.LogDebug("FileCachingStore: no existing file at {Path}", _filePath);
                return new Dictionary<string, CheckpointFileEntry>(StringComparer.Ordinal);
            }

            var json = File.ReadAllText(_filePath);
            var deserialized = JsonSerializer.Deserialize(json, CheckpointSerializerContext.Default.DictionaryStringCheckpointFileEntry);
            if (deserialized != null)
            {
                // Filter expired entries
                var now = DateTime.UtcNow;
                var valid = deserialized
                    .Where(kv => kv.Value.ExpiresAt >= now)
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

                _logger.LogDebug("FileCachingStore: loaded {Count} checkpoints from {Path} ({Expired} expired)",
                    valid.Count, _filePath, deserialized.Count - valid.Count);
                return valid;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FileCachingStore: failed to load from {Path}", _filePath);
        }

        return new Dictionary<string, CheckpointFileEntry>(StringComparer.Ordinal);
    }

    private void FlushToDisk()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmpPath = _filePath + ".tmp";
            var json = JsonSerializer.Serialize(_entries, CheckpointSerializerContext.Default.DictionaryStringCheckpointFileEntry);
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, _filePath, overwrite: true); // atomic on NTFS
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FileCachingStore: failed to flush");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate) { FlushToDisk(); }
    }
}

/// <summary>
/// File entry for a single checkpoint. Serialized to JSON.
/// Data stored as Base64 string (AOT-safe, no custom converter needed).
/// </summary>
internal sealed record CheckpointFileEntry(
    [property: JsonPropertyName("d")] string DataBase64,
    [property: JsonPropertyName("tc")] long TokenCount,
    [property: JsonPropertyName("sa")] DateTime SavedAt,
    [property: JsonPropertyName("ea")] DateTime ExpiresAt);

/// <summary>
/// JSON serializer context for AOT-compatible serialization.
/// Registers the specific types we need — no reflection fallback.
/// </summary>
[JsonSerializable(typeof(Dictionary<string, CheckpointFileEntry>))]
internal sealed partial class CheckpointSerializerContext : JsonSerializerContext;

