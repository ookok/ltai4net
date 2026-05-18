using System.Text.Json.Serialization;

namespace LTAI.Vector.Models;

public sealed class VectorRecord
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("vector")]
    public float[] Vector { get; init; } = [];

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, string> Metadata { get; init; } = new();

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

public sealed class VectorSearchResult
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("score")]
    public float Score { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, string> Metadata { get; init; } = new();
}

public sealed class VectorStoreStats
{
    [JsonPropertyName("total_vectors")]
    public int TotalVectors { get; init; }

    [JsonPropertyName("dimension")]
    public int Dimension { get; init; }

    [JsonPropertyName("collections")]
    public int Collections { get; init; }

    [JsonPropertyName("backend_type")]
    public string BackendType { get; init; } = "memory";
}

public enum VectorBackendType
{
    Memory,
    LanceDB,
    HNSW
}
