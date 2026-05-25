using LTAI.Knowledge.Vector.Interfaces;

namespace LTAI.Knowledge.Vector.Embedding;

public sealed record QuantizedEmbedding
{
    public byte[] Bytes { get; init; } = [];
    public float MinVal { get; init; }
    public float MaxVal { get; init; }
    public int OriginalDimension { get; init; }
    public int ByteDimension => Bytes.Length;

    public float[] Dequantize()
    {
        var range = MaxVal - MinVal;
        if (range <= 0) range = 1f;
        var result = new float[OriginalDimension];
        for (var i = 0; i < OriginalDimension && i < Bytes.Length; i++)
        {
            result[i] = MinVal + (Bytes[i] / 255f) * range;
        }
        return result;
    }

    public static QuantizedEmbedding FromFloats(float[] embedding)
    {
        if (embedding == null || embedding.Length == 0)
            return new QuantizedEmbedding { OriginalDimension = 0 };

        var minVal = embedding.Min();
        var maxVal = embedding.Max();
        var range = maxVal - minVal;
        if (range <= 0) range = 1f;

        var bytes = new byte[embedding.Length];
        for (var i = 0; i < embedding.Length; i++)
        {
            var normalized = (embedding[i] - minVal) / range;
            bytes[i] = (byte)Math.Clamp((int)(normalized * 255f), 0, 255);
        }

        return new QuantizedEmbedding
        {
            Bytes = bytes,
            MinVal = minVal,
            MaxVal = maxVal,
            OriginalDimension = embedding.Length
        };
    }
}

public sealed class EmbeddingQuantizer
{
    private readonly IEmbeddingBackend? _backend;
    private readonly IVectorStore? _vectorStore;

    public EmbeddingQuantizer(IEmbeddingBackend? backend = null, IVectorStore? vectorStore = null)
    {
        _backend = backend;
        _vectorStore = vectorStore;
    }

    public QuantizedEmbedding Quantize(float[] embedding)
    {
        return QuantizedEmbedding.FromFloats(embedding);
    }

    public List<QuantizedEmbedding> QuantizeBatch(IReadOnlyList<float[]> embeddings)
    {
        return embeddings.Select(QuantizedEmbedding.FromFloats).ToList();
    }

    public float[] Dequantize(QuantizedEmbedding quantized)
    {
        return quantized.Dequantize();
    }

    public List<float[]> DequantizeBatch(IReadOnlyList<QuantizedEmbedding> quantized)
    {
        return quantized.Select(q => q.Dequantize()).ToList();
    }

    public static double EstimateSimilarityQuick(byte[] a, byte[] b)
    {
        if (a.Length != b.Length || a.Length == 0)
            return 0;

        long sumSq = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var diff = (int)a[i] - (int)b[i];
            sumSq += diff * diff;
        }

        var l2 = Math.Sqrt(sumSq);
        var maxL2 = Math.Sqrt(255.0 * 255.0 * a.Length);
        return 1.0 - (l2 / maxL2);
    }

    public List<(int Index, double Score)> FastTopK(
        float[] query,
        IReadOnlyList<QuantizedEmbedding> candidates,
        int topK = 10)
    {
        var queryQuantized = QuantizedEmbedding.FromFloats(query);

        var scored = new List<(int Index, double Score)>(candidates.Count);
        for (var i = 0; i < candidates.Count; i++)
        {
            var score = EstimateSimilarityQuick(queryQuantized.Bytes, candidates[i].Bytes);
            scored.Add((i, score));
        }

        return scored
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .ToList();
    }

    public async Task<List<(int Index, double Score)>> FastTopKAsync(
        string queryText,
        IReadOnlyList<QuantizedEmbedding> candidates,
        int topK = 10,
        CancellationToken cancellationToken = default)
    {
        float[]? queryEmbedding = null;

        if (_vectorStore != null)
        {
            queryEmbedding = await _vectorStore.EmbedAsync(queryText, cancellationToken).ConfigureAwait(false);
        }
        else if (_backend != null)
        {
            var embeddings = await _backend.EmbedAsync(new[] { queryText }, cancellationToken).ConfigureAwait(false);
            queryEmbedding = embeddings.Length > 0 ? embeddings[0] : null;
        }

        if (queryEmbedding == null)
            return new List<(int, double)>();

        return FastTopK(queryEmbedding, candidates, topK);
    }

    public double ComputeStorageRatio(QuantizedEmbedding quantized)
    {
        var byteBytes = quantized.Bytes.Length * sizeof(byte);
        var floatBytes = quantized.OriginalDimension * sizeof(float);
        return (double)byteBytes / floatBytes;
    }

    public Dictionary<string, object> GetStats(int totalQuantized = 0)
    {
        return new Dictionary<string, object>
        {
            ["compression_ratio"] = 4.0,
            ["bytes_per_dimension"] = 1,
            ["float_bytes_per_dimension"] = 4,
            ["space_saved_pct"] = 75.0,
            ["total_quantized"] = totalQuantized,
            ["estimated_memory_saved"] = totalQuantized > 0
                ? $"{totalQuantized * 3.0 / 1024.0:F1} KB"
                : "0 KB"
        };
    }

    public static float DotProductApprox(byte[] a, byte[] b)
    {
        if (a.Length != b.Length || a.Length == 0)
            return 0;

        long sum = 0;
        for (var i = 0; i < a.Length; i++)
            sum += (int)a[i] * (int)b[i];

        return sum / (255f * 255f * a.Length);
    }
}
