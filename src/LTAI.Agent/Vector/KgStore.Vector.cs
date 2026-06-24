using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using TurboQuant.Core.Packing;

namespace LTAI.Agent.Vector;

public sealed partial class KgStore
{
    // ═══════════════════════════════════════════
    //  Quality scores
    // ═══════════════════════════════════════════

    private const string SQL_UPSERT_SCORE = """
        INSERT OR REPLACE INTO QualityScores(node_id, quality_score, freshness_score, relevance_score, confidence_score)
        VALUES (@nid, @q, @f, @r, @c);
        """;

    public async Task SetScoresAsync(long nodeId, double quality, double freshness,
        double relevance, double confidence)
    {
        await WriteLockVoidAsync(SQL_UPSERT_SCORE, cmd =>
        {
            cmd.Parameters.AddWithValue("@nid", nodeId);
            cmd.Parameters.AddWithValue("@q", quality);
            cmd.Parameters.AddWithValue("@f", freshness);
            cmd.Parameters.AddWithValue("@r", relevance);
            cmd.Parameters.AddWithValue("@c", confidence);
        }).ConfigureAwait(false);
    }

    private const string SQL_GET_SCORES = "SELECT * FROM QualityScores WHERE node_id = @nid;";

    public async Task<QualityScoreRow?> GetScoresAsync(long nodeId)
    {
        ThrowIfDisposed();
        using var cmd = _reader.CreateCommand();
        cmd.CommandText = SQL_GET_SCORES;
        cmd.Parameters.AddWithValue("@nid", nodeId);
        using var rdr = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        if (!rdr.Read()) return null;
        return new QualityScoreRow
        {
            NodeId = rdr.GetInt64(0),
            QualityScore = rdr.GetDouble(1),
            FreshnessScore = rdr.GetDouble(2),
            RelevanceScore = rdr.GetDouble(3),
            ConfidenceScore = rdr.GetDouble(4),
            ScoredAt = rdr.GetString(5),
        };
    }

    public async Task<List<(long nodeId, double quality)>> SearchByQuality(string? kindFilter = null, int topN = 30)
    {
        ThrowIfDisposed();
        var sql = kindFilter != null
            ? "SELECT q.node_id, q.quality_score FROM QualityScores q JOIN Nodes n ON n.id = q.node_id WHERE n.kind = @kind ORDER BY q.quality_score DESC LIMIT @lim;"
            : "SELECT node_id, quality_score FROM QualityScores ORDER BY quality_score DESC LIMIT @lim;";
        using var cmd = _reader.CreateCommand();
        cmd.CommandText = sql;
        if (kindFilter != null) cmd.Parameters.AddWithValue("@kind", kindFilter);
        cmd.Parameters.AddWithValue("@lim", topN);
        var results = new List<(long, double)>();
        using var rdr = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (rdr.Read())
            results.Add((rdr.GetInt64(0), rdr.GetDouble(1)));
        return results;
    }

    // ═══════════════════════════════════════════
    //  Vector Search (TurboQuant 4-bit compressed, in-memory HNSW)
    //  384-dim multilingual MiniLM embedding stored as BLOB (192 bytes).
    //  Linear scan is fast enough for ≤100K nodes.
    // ═══════════════════════════════════════════

    /// <summary>Insert or update a vector embedding for a node (TurboQuant 4-bit compressed).</summary>
    public async Task InsertVectorAsync(long nodeId, float[] embedding)
    {
        if (embedding.Length != VectorQuantizer.Dim)
            throw new ArgumentException($"Embedder requires {VectorQuantizer.Dim}-dim vectors, got {embedding.Length}");

        var packed = VectorQuantizer.Quantize(embedding);
        var blob = packed.ToBytes();

        _hnswLock.EnterWriteLock();
        try
        {
            await WriteLockVoidAsync(SQL_INSERT_VEC, cmd =>
            {
                cmd.Parameters.AddWithValue("@nid", nodeId);
                cmd.Parameters.AddWithValue("@vec", blob);
            }).ConfigureAwait(false);

            _hnsw.InsertPacked(packed);
            _hnswNodeIds.Add(nodeId);
        }
        finally { _hnswLock.ExitWriteLock(); }
        Interlocked.Increment(ref _vectorCount);
    }

    /// <summary>
    /// Vector similarity search by cosine distance (TurboQuant 4-bit compressed).
    /// Uses HNSW for approximate nearest neighbor search with packed vectors.
    /// A <see cref="_hnswNodeIds"/> list maps HNSW position → node_id to avoid
    /// the O(n) VecNodes rowid lookup (which was broken — rowid ≠ HNSW position).
    /// </summary>
    public async Task<List<(long nodeId, float distance)>> SearchVector(float[] query, int topN = 30, string? kindFilter = null)
    {
        ThrowIfDisposed();
        if (query.Length != VectorQuantizer.Dim)
            throw new ArgumentException($"Embedder requires {VectorQuantizer.Dim}-dim vectors, got {query.Length}");

        if (_hnswNodeIds.Count == 0)
            await WarmupHnswAsync().ConfigureAwait(false);

        List<(int idx, float dist)> hnswResults;
        _hnswLock.EnterReadLock();
        try
        {
            if (_hnswNodeIds.Count == 0) return [];
            hnswResults = _hnsw.Search(query, topN * 2);
        }
        finally { _hnswLock.ExitReadLock(); }

        if (hnswResults.Count == 0) return [];

        var candidates = new List<(long nodeId, float distance)>();
        _hnswLock.EnterReadLock();
        try
        {
            foreach (var (idx, dist) in hnswResults)
            {
                if (idx < 0 || idx >= _hnswNodeIds.Count) continue;
                candidates.Add((_hnswNodeIds[idx], dist));
                if (candidates.Count >= topN) break;
            }
        }
        finally { _hnswLock.ExitReadLock(); }

        if (candidates.Count == 0) return [];

        if (kindFilter != null)
        {
            var idList = candidates.Select(c => c.nodeId).Distinct().ToList();
            if (idList.Count == 0) return [];

            var sb = new StringBuilder("SELECT id FROM Nodes WHERE id IN (");
            sb.Append(string.Join(",", idList.Select((_, i) => $"@id{i}")));
            sb.Append(") AND kind = @kind;");

            using var cmd = _reader.CreateCommand();
            cmd.CommandText = sb.ToString();
            for (int i = 0; i < idList.Count; i++)
                cmd.Parameters.AddWithValue($"@id{i}", idList[i]);
            cmd.Parameters.AddWithValue("@kind", kindFilter);

            var validIds = new HashSet<long>();
            using var rdr = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (rdr.Read()) validIds.Add(rdr.GetInt64(0));

            return candidates.Where(c => validIds.Contains(c.nodeId)).Take(topN).ToList();
        }

        return candidates.Take(topN).ToList();
    }

    /// <summary>Delete the vector embedding for a node. Rebuilds HNSW index from remaining vectors.</summary>
    public async Task DeleteVectorAsync(long nodeId)
    {
        await WriteLockVoidAsync(SQL_DELETE_VEC, cmd =>
        {
            cmd.Parameters.AddWithValue("@nid", nodeId);
        }).ConfigureAwait(false);
        await RebuildCentroidsAsync().ConfigureAwait(false);
    }

    private static float CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
        => LTAI.AI.VectorMath.CosineSimilarity(a, b);

    private const string SQL_INSERT_VEC =
        "INSERT OR REPLACE INTO VecNodes(node_id, vec) VALUES (@nid, @vec);";

    private const string SQL_DELETE_VEC =
        "DELETE FROM VecNodes WHERE node_id = @nid;";

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    // ═══════════════════════════════════════════
    //  HNSW index rebuild from persisted vectors
    // ═══════════════════════════════════════════

    /// <summary>Rebuild the HNSW index + nodeId mapping from all TurboQuant-compressed vectors in VecNodes.</summary>
    public async Task RebuildCentroidsAsync()
    {
        ThrowIfDisposed();
        _hnswLock.EnterWriteLock();
        try
        {
            _hnswNodeIds.Clear();
            _hnsw.Rebuild([]);
        }
        finally { _hnswLock.ExitWriteLock(); }

        var batch = new List<(long nodeId, PackedVector packed)>();
        using (var rdr = _reader.CreateCommand())
        {
            rdr.CommandText = "SELECT node_id, vec FROM VecNodes;";
            using var reader = await rdr.ExecuteReaderAsync().ConfigureAwait(false);
            while (reader.Read())
            {
                var nid = reader.GetInt64(0);
                var blob = (byte[])reader["vec"];
                var packed = PackedVector.FromBytes(blob);
                batch.Add((nid, packed));
            }
        }

        _hnswLock.EnterWriteLock();
        try
        {
            _hnswNodeIds.Capacity = batch.Count;
            foreach (var (nid, packed) in batch)
            {
                _hnsw.InsertPacked(packed);
                _hnswNodeIds.Add(nid);
            }
        }
        finally { _hnswLock.ExitWriteLock(); }

        try { SaveHnswSnapshot(); } catch { }
    }

    private string HnswSnapshotPath => _dbPath + ".hnsw";

    public void SaveHnswSnapshot()
    {
        var snapshotPath = HnswSnapshotPath;
        var tmpPath = snapshotPath + ".tmp";
        _hnswLock.EnterReadLock();
        try
        {
            using var stream = File.Create(tmpPath);
            using var writer = new Utf8JsonWriter(stream);
            writer.WriteStartObject();
            writer.WriteNumber("Count", _hnswNodeIds.Count);
            writer.WriteStartArray("NodeIds");
            foreach (var nid in _hnswNodeIds)
                writer.WriteNumberValue(nid);
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }
        finally { _hnswLock.ExitReadLock(); }
        File.Move(tmpPath, snapshotPath, true);
    }

    public async Task WarmupHnswAsync()
    {
        var snapshotPath = HnswSnapshotPath;
        if (File.Exists(snapshotPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(snapshotPath).ConfigureAwait(false));
                var count = doc.RootElement.GetProperty("Count").GetInt32();
                if (count > 0 && _hnswNodeIds.Count == 0)
                {
                    await RebuildCentroidsAsync().ConfigureAwait(false);
                    return;
                }
            }
            catch { try { File.Delete(snapshotPath); } catch { } }
        }
        if (_hnswNodeIds.Count == 0)
            await RebuildCentroidsAsync().ConfigureAwait(false);
    }
}
