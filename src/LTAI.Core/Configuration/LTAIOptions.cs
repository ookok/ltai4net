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

public sealed class LayerConfig
{
    [JsonPropertyName("provider")]
    public string Provider { get; init; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;

    [JsonPropertyName("temperature")]
    public float? Temperature { get; init; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; init; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Provider) && !string.IsNullOrWhiteSpace(Model);
}

public sealed class AIConfig
{
    [JsonPropertyName("default_provider")]
    public string DefaultProvider { get; init; } = "deepseek";

    [JsonPropertyName("l0")]
    public LayerConfig L0 { get; init; } = new()
    {
        Provider = "siliconflow",
        Model = "BAAI/bge-large-zh-v1.5"
    };

    [JsonPropertyName("l1")]
    public LayerConfig L1 { get; init; } = new()
    {
        Provider = "deepseek",
        Model = "deepseek-v4-flash",
        Temperature = 0.3f
    };

    [JsonPropertyName("l2")]
    public LayerConfig L2 { get; init; } = new()
    {
        Provider = "deepseek",
        Model = "deepseek-v4-pro",
        Temperature = 0.3f
    };

    [JsonPropertyName("fast_model")]
    public string FastModel => L1.Model;

    [JsonPropertyName("deep_model")]
    public string DeepModel => L2.Model;

    [JsonPropertyName("embedding_model")]
    public string EmbeddingModel => L0.Model;

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

    public LayerConfig GetLayerConfig(string layer)
    {
        return layer.ToUpperInvariant() switch
        {
            "L0" => L0,
            "L1" => L1,
            "L2" => L2,
            "FAST" => L1,
            "DEEP" => L2,
            "EMBEDDING" => L0,
            _ => L2
        };
    }
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
