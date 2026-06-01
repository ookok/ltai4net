// Copyright (c) LTAI. All rights reserved.

using System.Collections.Concurrent;
using System.Text.Json;
using LTAI.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace LTAI.AI;

/// <summary>
/// P11.1b: Persistent cache for tool/agent/role description embeddings.
/// Avoids re-embedding the same tool list on every routing call (80+ tools ×
/// 384d × 4 bytes ≈ 120 KB per snapshot). On first call to <see cref="GetOrComputeAllAsync"/>,
/// computes the full set in a single <see cref="LocalEmbedder.GenerateBatch"/>
/// invocation (1 ONNX session.Run for all descriptions) and stores the rows
/// in a JSON file under <c>%LOCALAPPDATA%/LTAI/tool_embeddings.json</c> keyed
/// by a SHA-256 of the description text. Subsequent calls skip embedding for
/// unchanged entries; only new/changed descriptions are recomputed (delta batch).
/// Survives process restarts. JSON chosen over SQLite to keep LTAI.AI free of
/// the Microsoft.Data.Sqlite dependency (which lives in LTAI.Agent).
/// </summary>
public sealed class ToolEmbeddingCache
{
    private readonly EmbeddingClient _embedder;
    private readonly ILogger<ToolEmbeddingCache> _logger;
    private readonly string _filePath;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;
    private readonly ConcurrentDictionary<string, CacheEntry> _store = new(StringComparer.Ordinal);

    public ToolEmbeddingCache(EmbeddingClient embedder, ILogger<ToolEmbeddingCache> logger, string? dataDir = null)
    {
        _embedder = embedder;
        _logger = logger;
        dataDir ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LTAI");
        Directory.CreateDirectory(dataDir);
        _filePath = Path.Combine(dataDir, "tool_embeddings.json");
    }

    public string FilePath => _filePath;
    public int CachedEntryCount => _store.Count;

    public async Task<IReadOnlyDictionary<string, float[]>> GetOrComputeAllAsync(
        IReadOnlyList<(string Key, string Description)> items,
        CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        if (items.Count == 0) return new Dictionary<string, float[]>();

        // Compute SHA-256 fingerprint for each description; cache entry = (key, fingerprint)
        var fingerprints = items.ToDictionary(
            it => it.Key,
            it => (Hash: ComputeHash(it.Description), it.Description),
            StringComparer.OrdinalIgnoreCase);

        // Look up which keys+fingerprints are already cached
        var result = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<(string Key, string Hash, string Description)>();
        foreach (var (key, fp) in fingerprints)
        {
            if (_store.TryGetValue(key, out var entry) &&
                string.Equals(entry.Fingerprint, fp.Hash, StringComparison.Ordinal))
            {
                result[key] = entry.Vector;
            }
            else
            {
                missing.Add((key, fp.Hash, fp.Description));
            }
        }

        if (missing.Count > 0)
        {
            _logger.LogInformation("ToolEmbeddingCache: computing {N}/{T} missing embeddings", missing.Count, items.Count);
            // Single batched call — much faster than N sequential Generate calls
            var texts = missing.Select(m => m.Description).ToArray();
            var vectors = await _embedder.GenerateBatchAsync(texts, ct).ConfigureAwait(false);
            for (int i = 0; i < missing.Count; i++)
            {
                var (key, hash, desc) = missing[i];
                var vec = vectors[i];
                result[key] = vec;
                _store[key] = new CacheEntry { Fingerprint = hash, Description = desc, Vector = vec };
            }
            await PersistAsync(ct).ConfigureAwait(false);
        }
        else
        {
            _logger.LogDebug("ToolEmbeddingCache: all {N} embeddings served from cache", items.Count);
        }
        return result;
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_initialized) return;
        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            if (File.Exists(_filePath))
            {
                try
                {
                    await using var fs = File.OpenRead(_filePath);
                    var entries = await JsonSerializer.DeserializeAsync<List<CacheEntry>>(
                        fs, JsonOpts, ct).ConfigureAwait(false);
                    if (entries is not null)
                    {
                        foreach (var e in entries)
                            _store[e.Key] = e;
                    }
                    _logger.LogInformation("ToolEmbeddingCache: loaded {N} entries from {Path}", _store.Count, _filePath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ToolEmbeddingCache: failed to load {Path}, starting fresh", _filePath);
                    _store.Clear();
                }
            }
            _initialized = true;
        }
        finally { _initLock.Release(); }
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        var entries = _store.Select(kv => new CacheEntry
        {
            Key = kv.Key,
            Fingerprint = kv.Value.Fingerprint,
            Description = kv.Value.Description,
            Vector = kv.Value.Vector,
        }).ToList();
        var tmp = _filePath + ".tmp";
        await using (var fs = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(fs, entries, JsonOpts, ct).ConfigureAwait(false);
        }
        File.Move(tmp, _filePath, overwrite: true);
    }

    private static string ComputeHash(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        IncludeFields = false,
    };

    public sealed class CacheEntry
    {
        public string Key { get; set; } = "";
        public string Fingerprint { get; set; } = "";
        public string Description { get; set; } = "";
        public float[] Vector { get; set; } = [];
    }
}
