using System.Text.Json.Serialization;

namespace LTAI.Core.Configuration;

public sealed class ProviderConfig
{
    public string Endpoint { get; set; } = "";
    public string Model { get; set; } = "";

    public string? GetApiKey() =>
        this.EnvVar != null ? SecretManager.Get(this.EnvVar) : null;

    public void SetApiKey(string key)
    {
        if (this.EnvVar != null) SecretManager.Set(this.EnvVar, key);
    }

    [JsonIgnore]
    public string? EnvVar { get; set; }
}

public sealed class AIConfig
{
    public string? DefaultProvider { get; init; } = null;
    public string? Model { get; init; } = null;
    public int MaxTokens { get; init; } = 4096;
    public double Temperature { get; init; } = 0.7;
    public string? ApiKeyEnv { get; init; } = null;
    public bool SkipSafetyChecks { get; init; } = false;
    public string Mode { get; init; } = "balanced";
    public int ContextWindowSize { get; init; } = 64000;
    public Dictionary<string, ProviderConfig> Providers { get; init; } = new();
    public Dictionary<string, string>? DegradationChain { get; init; }
    public long GlobalTokenBudget { get; init; } = 1_000_000;
    public long PerUserTokenBudget { get; init; } = 200_000;
    public int ResponseCacheSize { get; init; } = 256;
    public LayerConfig? L0 { get; init; }
    public LayerConfig? L1 { get; init; }
    public LayerConfig? L2 { get; init; }
    public LayerConfig? L3 { get; init; }
    public AutoSelectConfig? AutoSelect { get; init; }

    public ProviderConfig GetLayerConfig(string layer)
    {
        var key = layer.ToLowerInvariant() switch
        {
            "fast" or "l1" => "deepseek-fast",
            "deep" or "l2" or "pro" => "deepseek-pro",
            "embedding" => "embedding",
            _ => layer
        };
        var pc = Providers.GetValueOrDefault(key);
        if (pc != null) return pc;
        return new ProviderConfig { Model = Model };
    }

    public string ResolveLayerProvider(string layer) => layer.ToLowerInvariant() switch
    {
        "fast" or "l0" => !string.IsNullOrEmpty(L0?.Provider) ? L0.Provider : (DefaultProvider ?? ""),
        "l1" => !string.IsNullOrEmpty(L1?.Provider) ? L1.Provider : (DefaultProvider ?? ""),
        "deep" or "l2" or "pro" => !string.IsNullOrEmpty(L2?.Provider) ? L2.Provider : (DefaultProvider ?? ""),
        _ => DefaultProvider ?? ""
    };
}

public sealed class LayerConfig
{
    public string Provider { get; init; } = "";
    public string? Model { get; init; }
    public string? Endpoint { get; init; }
}

public sealed class SteerConfig
{
    public bool Enabled { get; init; } = false;
    public string Endpoint { get; init; } = "https://api.siliconflow.cn/v1";
    public string? Model { get; init; } = null;
    public string? ApiKeyEnv { get; init; } = null;
    public double Temperature { get; init; } = 0.2;
    public int MaxTokens { get; init; } = 512;
}

public sealed class AutoSelectConfig
{
    public bool Enabled { get; set; } = true;
    public int RefreshIntervalMin { get; set; } = 30;
    public double MinScoreImprovement { get; set; } = 0.15;
}
