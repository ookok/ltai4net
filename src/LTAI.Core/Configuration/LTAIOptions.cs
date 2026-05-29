using System.Text.Json.Serialization;

namespace LTAI.Core.Configuration;

public sealed class ProviderConfig
{
    public string Endpoint { get; set; } = "";
    public string Model { get; set; } = "";
}

public sealed class AIConfig
{
    public string DefaultProvider { get; init; } = "deepseek";
    public string Model { get; init; } = "deepseek-chat";
    public int MaxTokens { get; init; } = 4096;
    public double Temperature { get; init; } = 0.7;
    public string? ApiKeyEnv { get; init; } = "DEEPSEEK_API_KEY";
    public string Mode { get; init; } = "balanced";
    public Dictionary<string, ProviderConfig> Providers { get; init; } = new();
    public Dictionary<string, string>? DegradationChain { get; init; }
    public long GlobalTokenBudget { get; init; } = 1_000_000;
    public long PerUserTokenBudget { get; init; } = 200_000;

    /// <summary>Get config for a named layer (fast/deep/etc). UI convenience.</summary>
    public ProviderConfig GetLayerConfig(string layer) => layer.ToLowerInvariant() switch
    {
        "fast" or "l1" => Providers.GetValueOrDefault("deepseek-fast") ?? new ProviderConfig { Model = "deepseek-v4-flash" },
        "deep" or "l2" or "pro" => Providers.GetValueOrDefault("deepseek-pro") ?? new ProviderConfig { Model = "deepseek-v4-pro" },
        "embedding" => Providers.GetValueOrDefault("embedding") ?? new ProviderConfig { Model = "text-embedding-3-small" },
        _ => Providers.GetValueOrDefault(layer) ?? new ProviderConfig { Model = Model }
    };
}

public sealed class WebConfig
{
    public int Port { get; init; } = 5100;
    public string[] CorsOrigins { get; init; } = Array.Empty<string>();
}

public sealed class VectorConfig
{
    public string Provider { get; init; } = "local";
    public int EmbeddingDim { get; init; } = 384;
}

public sealed class HarnessProfile
{
    public string Name { get; set; } = "development";
    public int MaxConcurrentWorkflows { get; set; } = 4;
    public string? SandboxType { get; set; }
    public bool EnableAuditTrail { get; set; } = true;
}

public sealed class LTAIOptions
{
    public const string SectionName = "LTAI";
    public AIConfig AI { get; init; } = new();
    public WebConfig Web { get; init; } = new();
    public VectorConfig Vector { get; init; } = new();
    public HarnessProfile Harness { get; set; } = new();
    public string DataDirectory { get; init; } = ".livingtree";
    public string ToolsDirectory { get; init; } = "tools";
    public string PromptsDirectory { get; init; } = "prompts";
    public string MemoryDirectory { get; init; } = "memory";
    public string ModelsDirectory { get; init; } = "models";
    public string LogsDirectory { get; init; } = "logs";
    public int MaxHistoryMessages { get; init; } = 200;
    public bool EnableObservability { get; init; } = true;

    public string ResolveDataPath(string subPath) =>
        Path.Combine(EnvDataDir ?? AppContext.BaseDirectory, DataDirectory, subPath);

    public string ResolveToolsPath(string? subPath = null) =>
        Path.Combine(EnvToolsDir ?? AppContext.BaseDirectory, ToolsDirectory, subPath ?? "");

    public string ResolvePromptsPath(string? subPath = null) =>
        Path.Combine(EnvPromptsDir ?? AppContext.BaseDirectory, PromptsDirectory, subPath ?? "");

    public string ResolveMemoryPath(string? subPath = null) =>
        Path.Combine(EnvMemoryDir ?? AppContext.BaseDirectory, MemoryDirectory, subPath ?? "");

    private static string? EnvDataDir => Environment.GetEnvironmentVariable("LTAI_DATA_DIR");
    private static string? EnvToolsDir => Environment.GetEnvironmentVariable("LTAI_TOOLS_DIR");
    private static string? EnvPromptsDir => Environment.GetEnvironmentVariable("LTAI_PROMPTS_DIR");
    private static string? EnvMemoryDir => Environment.GetEnvironmentVariable("LTAI_MEMORY_DIR");
}
