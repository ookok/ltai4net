using System.Text.Json.Serialization;

namespace LTAI.Core.Configuration;

public sealed class LTAIOptions
{
    public const string SectionName = "LTAI";

    public AIConfig AI { get; init; } = new();
    public WebConfig Web { get; init; } = new();
    public VectorConfig Vector { get; init; } = new();
    public NetworkConfig Network { get; init; } = new();
}

public sealed class AIConfig
{
    [JsonPropertyName("default_provider")]
    public string DefaultProvider { get; init; } = "deepseek";

    [JsonPropertyName("fast_model")]
    public string FastModel { get; init; } = "deepseek-v4-flash";

    [JsonPropertyName("deep_model")]
    public string DeepModel { get; init; } = "deepseek-v4-pro";

    [JsonPropertyName("embedding_model")]
    public string EmbeddingModel { get; init; } = "text-embedding-3-small";

    [JsonPropertyName("daily_budget_usd")]
    public decimal DailyBudgetUsd { get; init; } = 10.00m;

    [JsonPropertyName("default_temperature")]
    public float DefaultTemperature { get; init; } = 0.3f;

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; init; } = 4096;

    [JsonPropertyName("timeout_ms")]
    public int TimeoutMs { get; init; } = 60000;

    [JsonPropertyName("max_collaboration_rounds")]
    public int MaxCollaborationRounds { get; init; } = 5;

    [JsonPropertyName("providers")]
    public Dictionary<string, ProviderConfig> Providers { get; init; } = new();
}

public sealed class ProviderConfig
{
    [JsonPropertyName("endpoint")]
    public string Endpoint { get; init; } = string.Empty;

    [JsonPropertyName("api_key")]
    public string ApiKey { get; init; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;
}

public sealed class WebConfig
{
    [JsonPropertyName("host")]
    public string Host { get; init; } = "0.0.0.0";

    [JsonPropertyName("port")]
    public int Port { get; init; } = 8080;

    [JsonPropertyName("rate_limit_per_minute")]
    public int RateLimitPerMinute { get; init; } = 60;
}

public sealed class VectorConfig
{
    [JsonPropertyName("dimension")]
    public int Dimension { get; init; } = 384;

    [JsonPropertyName("backend")]
    public string Backend { get; init; } = "hnsw";

    [JsonPropertyName("cache_size_mb")]
    public int CacheSizeMb { get; init; } = 256;
}

public sealed class NetworkConfig
{
    [JsonPropertyName("p2p_port")]
    public int P2PPort { get; init; } = 9090;

    [JsonPropertyName("discovery_endpoint")]
    public string DiscoveryEndpoint { get; init; } = string.Empty;
}
