namespace LTAI.Core.Configuration;

public sealed class EmbeddingConfig
{
    public string Gpu { get; init; } = "auto";
    public string Quantization { get; init; } = "auto";
    public int DeviceId { get; init; } = 0;
    public IDictionary<string, string> Models { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public bool PreWarmAllModels { get; init; } = false;

    public string GetQuantizationFor(string modelId)
    {
        if (!string.IsNullOrEmpty(modelId) &&
            Models.TryGetValue(modelId, out var m) &&
            !string.IsNullOrWhiteSpace(m))
        {
            return m.Trim().ToLowerInvariant();
        }
        return string.IsNullOrWhiteSpace(Quantization) ? "auto" : Quantization.Trim().ToLowerInvariant();
    }
}

public sealed class VectorConfig
{
    public string Provider { get; init; } = "local";
    public int EmbeddingDim { get; init; } = 384;
    public string Store { get; init; } = "hnsw";
    public string Reduction { get; init; } = "none";
    public string Quantizer { get; init; } = "turboquant-4bit";
}
