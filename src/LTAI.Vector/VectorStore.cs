using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using LTAI.Core.Configuration;
using LTAI.Vector.Interfaces;
using LTAI.Vector.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.Vector;

public sealed class VectorStore : IVectorStore, IDisposable
{
    private readonly ConcurrentDictionary<string, float[]> _vectors = new();
    private readonly ConcurrentDictionary<string, string> _idToCollection = new();
    private float[][]? _cachedMatrix;
    private string[]? _cachedIds;
    private bool _matrixDirty = true;
    private int _count;
    private const int MaxVectors = 50000;
    private readonly object _lock = new();

    private readonly IEmbeddingBackend _embeddingBackend;
    private readonly IOptions<LTAIOptions> _options;
    private readonly ILogger<VectorStore> _logger;
    private readonly int _dimension;

    public VectorStore(
        IEmbeddingBackend embeddingBackend,
        IOptions<LTAIOptions> options,
        ILogger<VectorStore> logger)
    {
        _embeddingBackend = embeddingBackend;
        _options = options;
        _logger = logger;
        _dimension = options.Value.Vector.Dimension;
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        var embeddings = await _embeddingBackend.EmbedAsync(new[] { text }, cancellationToken);
        return embeddings[0];
    }

    public Task AddVectorsAsync(
        IReadOnlyList<(string Id, float[] Vector)> items,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            foreach (var (id, vector) in items)
            {
                if (_count >= MaxVectors)
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
                _count++;
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
        var results = CosineTopK(queryVector, _cachedMatrix!, _cachedIds!, topK);
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

    public void Dispose()
    {
        _vectors.Clear();
        _cachedMatrix = null;
        _cachedIds = null;
        GC.SuppressFinalize(this);
    }
}
