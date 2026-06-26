// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  PalaceStore — HNSW vector index management
// ═══════════════════════════════════════════════════════════════

using System.Collections.Concurrent;
using System.Text.Json;
using LTAI.Agent.Vector;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Memory;

partial class PalaceStore
{
    // HNSW vector index for sub-linear semantic search
    private readonly HnswIndex _hnsw = new(HnswOptions.Default);
    private readonly ConcurrentDictionary<int, string> _hnswMap = new();
    private readonly ConcurrentDictionary<string, int> _hnswRev = new();
    private readonly ConcurrentDictionary<string, byte> _removed = new();
    private int _hnswReady;
    private readonly SemaphoreSlim _hnswLock = new(1, 1);

    // EvoEmbedding-inspired batch write buffer: prevents representation collapse
    // by accumulating HNSW inserts and flushing in batches (segment-batching).
    private MemoryWriteQueue? _writeQueue;

    private string HnswSnapshotPath => _dbPath + ".hnsw";
    private long _lastRemovedCleanupMs;

    /// <summary>Warm up the HNSW index on first use — tries snapshot, falls back to SQL rebuild.</summary>
    public Task WarmupHnswAsync()
    {
        if (Interlocked.CompareExchange(ref _hnswReady, 0, 0) == 1)
            return Task.CompletedTask;
        return WarmupHnswCoreAsync();
    }

    private async Task WarmupHnswCoreAsync()
    {
        await _hnswLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Interlocked.CompareExchange(ref _hnswReady, 0, 0) == 1)
                return;

            var snapshotPath = HnswSnapshotPath;
            if (File.Exists(snapshotPath))
            {
                try
                {
                    _logger?.LogInformation("PalaceStore: loading HNSW snapshot from {Path}", snapshotPath);
                    await RebuildHnswFromSnapshotAsync(snapshotPath).ConfigureAwait(false);
                    Interlocked.Exchange(ref _hnswReady, 1);
                    _logger?.LogInformation("PalaceStore: HNSW snapshot loaded ({Count} nodes)", _hnsw.Count);
                    return;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "PalaceStore: HNSW snapshot load failed, rebuilding from SQL");
                    try { File.Delete(snapshotPath); } catch { }
                }
            }
            await RebuildHnswCoreAsync().ConfigureAwait(false);
        }
        finally { _hnswLock.Release(); }
    }

    private async Task RebuildHnswFromSnapshotAsync(string snapshotPath)
    {
        var json = await File.ReadAllTextAsync(snapshotPath).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        _hnswMap.Clear();
        _hnswRev.Clear();
        _removed.Clear();
        _hnsw.Rebuild([]);

        var nodes = root.GetProperty("Nodes").EnumerateArray();
        var vectors = new List<(float[] vec, string id)>();
        foreach (var nodeEl in nodes)
        {
            var id = nodeEl.GetProperty("Id").GetString()!;
            var data = Convert.FromBase64String(nodeEl.GetProperty("Data").GetString()!);
            var vec = VectorQuantizer.DequantizeFromBytes(data);
            vectors.Add((vec, id));
        }
        _hnsw.Rebuild(vectors.Select(v => (ReadOnlyMemory<float>)v.vec));
        for (int i = 0; i < vectors.Count; i++)
        {
            _hnswMap[i] = vectors[i].id;
            _hnswRev[vectors[i].id] = i;
        }
    }

    /// <summary>Rebuild the HNSW index from all non-expired palace entries with embeddings.</summary>
    public async Task RebuildHnswAsync()
    {
        await _hnswLock.WaitAsync().ConfigureAwait(false);
        try { await RebuildHnswCoreAsync().ConfigureAwait(false); }
        finally { _hnswLock.Release(); }
    }

    private async Task RebuildHnswCoreAsync()
    {
        EnsureSchema();
        Interlocked.Exchange(ref _hnswReady, 0);
        _hnswMap.Clear();
        _hnswRev.Clear();
        _removed.Clear();

        var vectors = new List<(float[] vec, string id)>();
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT drawer_id, embedding FROM palace WHERE embedding IS NOT NULL AND (expires_at IS NULL OR expires_at>$now)";
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        using var rdr = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await rdr.ReadAsync().ConfigureAwait(false))
        {
            var id = rdr.GetString(0);
            var emb = DeserializeEmb(rdr, 1);
            if (emb != null && emb.Length == VectorQuantizer.Dim)
                vectors.Add((emb, id));
        }

        _logger?.LogInformation("PalaceStore: rebuilding HNSW index with {Count} vectors", vectors.Count);
        _hnsw.Rebuild(vectors.Select(v => (ReadOnlyMemory<float>)v.vec));
        _hnswMap.Clear();
        _hnswRev.Clear();
        _removed.Clear();
        for (int i = 0; i < vectors.Count; i++)
        {
            _hnswMap[i] = vectors[i].id;
            _hnswRev[vectors[i].id] = i;
        }
        Interlocked.Exchange(ref _hnswReady, 1);
        _logger?.LogInformation("PalaceStore: HNSW index rebuilt ({Count} nodes)", vectors.Count);

        _ = Task.Run(() =>
        {
            try { SaveHnswSnapshot(); }
            catch (Exception ex) { _logger?.LogWarning(ex, "PalaceStore: HNSW snapshot save failed"); }
        });
    }

    private void SaveHnswSnapshot()
    {
        var snapshotPath = HnswSnapshotPath;
        var tmpPath = snapshotPath + ".tmp";

        var embBlobs = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        using (var conn = new SqliteConnection(_connectionString))
        {
            conn.Open();
            var ids = _hnswMap.Values.ToList();
            if (ids.Count == 0) return;
            var placeholders = string.Join(",", ids.Select((_, i) => $"@p{i}"));
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT drawer_id, embedding FROM palace WHERE drawer_id IN ({placeholders})";
            for (int i = 0; i < ids.Count; i++)
                cmd.Parameters.AddWithValue($"@p{i}", ids[i]);
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                var id = rdr.GetString(0);
                if (!rdr.IsDBNull(1))
                    embBlobs[id] = (byte[])rdr[1];
            }
        }

        using var stream = File.Create(tmpPath);
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteStartArray("Nodes");
        foreach (var (_, drawerId) in _hnswMap)
        {
            writer.WriteStartObject();
            writer.WriteString("Id", drawerId);
            writer.WriteString("Data", embBlobs.TryGetValue(drawerId, out var blob) ? Convert.ToBase64String(blob) : "");
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        File.Move(tmpPath, snapshotPath, true);
    }

    private async Task TriggerHnswRebuildAsync()
    {
        try { await RebuildHnswAsync().ConfigureAwait(false); }
        catch (Exception ex) { _logger?.LogWarning(ex, "PalaceStore background RebuildHnswAsync failed"); }
    }

    private void CleanupRemovedIfNeeded()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (now - Interlocked.Read(ref _lastRemovedCleanupMs) < 300_000) return;
        Interlocked.Exchange(ref _lastRemovedCleanupMs, now);

        var toRemove = new List<string>();
        foreach (var (key, _) in _removed)
            if (!_hnswRev.ContainsKey(key)) toRemove.Add(key);

        foreach (var key in toRemove)
            _removed.TryRemove(key, out _);
    }
}
