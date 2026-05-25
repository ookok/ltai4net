using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using LTAI.Core.Configuration;
using LTAI.Knowledge.Vector.Interfaces;
using LTAI.Knowledge.Vector.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.Knowledge.Vector;

public sealed class VectorStore : IVectorStore, IDisposable
{
    private readonly ConcurrentDictionary<string, float[]> _vectors = new();
    private readonly ConcurrentDictionary<string, string> _idToCollection = new();
    private readonly ConcurrentDictionary<string, float[]> _embeddingCache = new();
    private float[][]? _cachedMatrix;
    private string[]? _cachedIds;
    private bool _matrixDirty = true;
    private int _count;
    private const int MaxVectors = 50000;
    private const int MaxEmbeddingCache = 500;
    private readonly object _lock = new();

    private readonly IEmbeddingBackend _embeddingBackend;
    private readonly IOptions<LTAIOptions> _options;
    private readonly ILogger<VectorStore> _logger;
    private readonly int _dimension;
    private readonly SqliteConnection _db;

    private readonly Dictionary<int, List<string>> _spatialIndex = new();
    private const int SpatialBuckets = 64;

    public VectorStore(
        IEmbeddingBackend embeddingBackend,
        IOptions<LTAIOptions> options,
        ILogger<VectorStore> logger)
    {
        _embeddingBackend = embeddingBackend;
        _options = options;
        _logger = logger;
        _dimension = options.Value.Vector.Dimension;

        var dbPath = Path.Combine(AppContext.BaseDirectory, ".livingtree", "vectors.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _db = new SqliteConnection($"Data Source={dbPath}");
        _db.Open();
        InitDb();
        LoadFromDb();
    }

    private void InitDb()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            CREATE TABLE IF NOT EXISTS vectors(id TEXT PRIMARY KEY, embedding BLOB, collection TEXT, created_at TEXT);
            """;
        cmd.ExecuteNonQuery();
    }

    private void LoadFromDb()
    {
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT id, embedding, collection FROM vectors LIMIT 10000";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetString(0);
                var blob = (byte[])r["embedding"];
                var vec = new float[blob.Length / 4];
                Buffer.BlockCopy(blob, 0, vec, 0, blob.Length);
                _vectors[id] = vec;
                if (!r.IsDBNull(2)) _idToCollection[id] = r.GetString(2);
                _count++;
            }
            if (_count > 0) { _matrixDirty = true; _logger.LogInformation("Loaded {N} persisted vectors", _count); }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "LoadFromDb failed"); }
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..20];
        if (_embeddingCache.TryGetValue(key, out var cached))
            return cached;

        var embeddings = await _embeddingBackend.EmbedAsync(new[] { text }, cancellationToken).ConfigureAwait(false);
        var result = embeddings[0];

        if (_embeddingCache.Count < MaxEmbeddingCache)
            _embeddingCache[key] = result;

        return result;
    }

    public Task AddVectorsAsync(
        IReadOnlyList<(string Id, float[] Vector)> items,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            foreach (var (id, vector) in items)
            {
                var isNew = !_vectors.ContainsKey(id);
                
                if (isNew && _count >= MaxVectors)
                {
                    var oldest = _vectors.Keys.FirstOrDefault();
                    if (oldest != null)
                    {
                        _vectors.TryRemove(oldest, out _);
                        _idToCollection.TryRemove(oldest, out _);
                        _count--;
                        _logger.LogDebug("Evicted oldest vector: {Id}", oldest);
                    }
                }

                _vectors[id] = vector;
                if (isNew)
                    _count++;

                var blob = new byte[vector.Length * 4];
                Buffer.BlockCopy(vector, 0, blob, 0, blob.Length);
                try
                {
                    using var c = _db.CreateCommand();
                    c.CommandText = "INSERT OR REPLACE INTO vectors(id, embedding, collection, created_at) VALUES(@id, @e, @col, @ts)";
                    c.Parameters.AddWithValue("@id", id);
                    c.Parameters.AddWithValue("@e", blob);
                    c.Parameters.AddWithValue("@col", _idToCollection.GetValueOrDefault(id) ?? (object)DBNull.Value);
                    c.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("O"));
                    c.ExecuteNonQuery();
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Persist vector failed {Id}", id); }
            }

            _matrixDirty = true;
        }

        _logger.LogDebug("Added {Count} vectors, total: {Total}", items.Count, _count);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VectorSearchResult>> SearchSimilarAsync(
        float[] queryVector,
        int topK = 5,
        CancellationToken cancellationToken = default)
    {
        if (_vectors.IsEmpty)
            return Task.FromResult<IReadOnlyList<VectorSearchResult>>(Array.Empty<VectorSearchResult>());

        RefreshMatrixCache();
        
        var results = _count > 1000 
            ? CosineTopKOptimized(queryVector, _cachedMatrix!, _cachedIds!, topK)
            : CosineTopK(queryVector, _cachedMatrix!, _cachedIds!, topK);
        
        return Task.FromResult(results);
    }

    public Task DeleteVectorAsync(string docId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_vectors.TryRemove(docId, out _))
            {
                _idToCollection.TryRemove(docId, out _);
                _count--;
                _matrixDirty = true;
                _logger.LogDebug("Deleted vector: {Id}", docId);
            }
        }

        return Task.CompletedTask;
    }

    public Task CreateCollectionAsync(string name, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Collection created: {Name}", name);
        return Task.CompletedTask;
    }

    public Task<VectorStoreStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new VectorStoreStats
        {
            TotalVectors = _count,
            Dimension = _dimension,
            Collections = _idToCollection.Values.Distinct().Count(),
            BackendType = "memory"
        });
    }

    private void RefreshMatrixCache()
    {
        if (!_matrixDirty && _cachedMatrix != null)
            return;

        lock (_lock)
        {
            if (!_matrixDirty || _vectors.IsEmpty)
                return;

            var ids = _vectors.Keys.ToArray();
            var matrix = new float[ids.Length][];
            for (var i = 0; i < ids.Length; i++)
                matrix[i] = _vectors[ids[i]];

            _cachedIds = ids;
            _cachedMatrix = matrix;
            _matrixDirty = false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float DotProduct(float[] a, float[] b)
    {
        var sum = 0.0f;
        for (var i = 0; i < a.Length; i++)
            sum += a[i] * b[i];
        return sum;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Norm(float[] v)
    {
        var sum = 0.0f;
        for (var i = 0; i < v.Length; i++)
            sum += v[i] * v[i];
        return MathF.Sqrt(sum);
    }

    private static IReadOnlyList<VectorSearchResult> CosineTopK(
        float[] query,
        float[][] matrix,
        string[] ids,
        int topK)
    {
        var queryNorm = Norm(query);
        if (queryNorm < 1e-9f)
            return Array.Empty<VectorSearchResult>();

        var scores = new List<(int Index, float Score)>(matrix.Length);
        for (var i = 0; i < matrix.Length; i++)
        {
            var dot = DotProduct(query, matrix[i]);
            var norm = Norm(matrix[i]);
            var score = norm > 1e-9f ? dot / (queryNorm * norm) : 0f;
            scores.Add((i, score));
        }

        scores.Sort((a, b) => b.Score.CompareTo(a.Score));

        return scores
            .Take(topK)
            .Select(s => new VectorSearchResult
            {
                Id = ids[s.Index],
                Score = s.Score
            })
            .ToList();
    }

    private static IReadOnlyList<VectorSearchResult> CosineTopKOptimized(
        float[] query,
        float[][] matrix,
        string[] ids,
        int topK)
    {
        var queryNorm = Norm(query);
        if (queryNorm < 1e-9f)
            return Array.Empty<VectorSearchResult>();

        var results = new List<(int Index, float Score)>(Math.Min(topK * 4, matrix.Length));
        var minScore = 0f;

        for (var i = 0; i < matrix.Length; i++)
        {
            var dot = DotProduct(query, matrix[i]);
            var norm = Norm(matrix[i]);
            var score = norm > 1e-9f ? dot / (queryNorm * norm) : 0f;

            if (results.Count < topK)
            {
                results.Add((i, score));
                if (results.Count == topK)
                    minScore = results.Min(r => r.Score);
            }
            else if (score > minScore)
            {
                results.Add((i, score));
                results.Sort((a, b) => b.Score.CompareTo(a.Score));
                if (results.Count > topK)
                    results.RemoveAt(results.Count - 1);
                minScore = results[^1].Score;
            }
        }

        return results
            .Select(s => new VectorSearchResult
            {
                Id = ids[s.Index],
                Score = s.Score
            })
            .ToList();
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _vectors.Clear();
        _idToCollection.Clear();
        _embeddingCache.Clear();
        _cachedMatrix = null;
        _cachedIds = null;
        try { _db.Close(); } catch { }
        try { _db.Dispose(); } catch { }
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
