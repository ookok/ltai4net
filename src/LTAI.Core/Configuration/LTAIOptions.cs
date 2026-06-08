using System.Text.Json.Serialization;

namespace LTAI.Core.Configuration;

/// <summary>
/// Configuration for a single LLM provider (endpoint + model + API key env var).
/// API keys are NEVER stored in config files — only in environment variables, managed via
/// <see cref="SecretManager"/>. Keys are read from env var at runtime.
/// <b>Consumers:</b> MultiProviderChatClient, EmbeddingClient (via GetApiKey/SetApiKey);
/// ConfigView, LLMConfigPanel (UI display/edit).
/// </summary>
public sealed class ProviderConfig
{
    public string Endpoint { get; set; } = "";
    public string Model { get; set; } = "";

    /// <summary>
    /// Read API key from environment variable via <see cref="SecretManager"/>.
    /// NEVER reads from config files — keys stay in env vars only.
    /// <b>Callers:</b> MultiProviderChatClient, EmbeddingClient.
    /// </summary>
    public string? GetApiKey() =>
        this.EnvVar != null ? SecretManager.Get(this.EnvVar) : null;

    /// <summary>
    /// Set API key to environment variable (persisted to User scope on Windows).
    /// <b>Callers:</b> ConfigView, LLMConfigPanel (UI key input).
    /// </summary>
    public void SetApiKey(string key)
    {
        if (this.EnvVar != null) SecretManager.Set(this.EnvVar, key);
    }

    /// <summary>
    /// The environment variable name for this provider's API key.
    /// E.g. "DEEPSEEK_API_KEY". Set at config load time from KnownKeys.
    /// </summary>
    [JsonIgnore]
    public string? EnvVar { get; set; }
}

/// <summary>
/// AI model configuration including provider selection, token budgets, and degradation chain.
/// Loaded from appsettings.json under "LTAI:AI".
/// <b>Consumers:</b> MultiProviderChatClient (DI service setup), TuiApp, ConfigView.
/// </summary>
public sealed class AIConfig
{
    public string? DefaultProvider { get; init; } = null;
    public string? Model { get; init; } = null;
    public int MaxTokens { get; init; } = 4096;
    public double Temperature { get; init; } = 0.7;
    public string? ApiKeyEnv { get; init; } = null;
    /// <summary>Skip safety input/output guardrails. Default false for security.</summary>
    public bool SkipSafetyChecks { get; init; } = false;
    /// <summary>Operational mode: "balanced", "fast", "precise", etc.</summary>
    public string Mode { get; init; } = "balanced";
    /// <summary>Context window size for token budget calculations. Default 64000.</summary>
    public int ContextWindowSize { get; init; } = 64000;
    /// <summary>Known LLM providers keyed by alias (e.g. "deepseek-fast", "deepseek-pro").</summary>
    public Dictionary<string, ProviderConfig> Providers { get; init; } = new();
    /// <summary>Degradation chain: on provider failure, try next in sequence."ProviderAlias" → "FallbackAlias".</summary>
    public Dictionary<string, string>? DegradationChain { get; init; }
    public long GlobalTokenBudget { get; init; } = 1_000_000;
    public long PerUserTokenBudget { get; init; } = 200_000;
    /// <summary>Response cache size limit per provider. 0 disables cache.</summary>
    public int ResponseCacheSize { get; init; } = 256;

    /// <summary>L0/L1/L2 independent layer configs. Each can point to any provider with any model.
    /// Unset layers fall back to <see cref="DefaultProvider"/> with its default model from KnownKeys.</summary>
    public LayerConfig? L0 { get; init; }
    public LayerConfig? L1 { get; init; }
    public LayerConfig? L2 { get; init; }

    /// <summary>
    /// Resolve ProviderConfig by layer name (legacy compat — callers should migrate to GetEffectiveLayer).
    /// </summary>
    public ProviderConfig GetLayerConfig(string layer)
    {
        var key = layer.ToLowerInvariant() switch
        {
            "fast" or "l1" => "deepseek-fast",
            "deep" or "l2" or "pro" => "deepseek-pro",
            "embedding" => "embedding",
            _ => layer
        };
        return Providers.GetValueOrDefault(key) ?? new ProviderConfig { Model = Model };
    }

    /// <summary>Resolve a layer's provider name from config, falling back to DefaultProvider.</summary>
    public string ResolveLayerProvider(string layer) => layer.ToLowerInvariant() switch
    {
        "fast" or "l0" => !string.IsNullOrEmpty(L0?.Provider) ? L0.Provider : DefaultProvider,
        "l1" => !string.IsNullOrEmpty(L1?.Provider) ? L1.Provider : DefaultProvider,
        "deep" or "l2" or "pro" => !string.IsNullOrEmpty(L2?.Provider) ? L2.Provider : DefaultProvider,
        _ => DefaultProvider
    };
}

/// <summary>Independent layer model config — provider name + optional model/endpoint override.</summary>
public sealed class LayerConfig
{
    public string Provider { get; init; } = "";
    public string? Model { get; init; }
    public string? Endpoint { get; init; }
}

/// <summary>
/// P13.1 + P13.2: local ONNX embedding model execution preferences.
/// Loaded from appsettings.json under "LTAI:Embedding".
/// <b>Consumers:</b> LTAI.AI.LocalEmbedder (Options), MultiProviderChatClient (AddLTAIAI).
/// </summary>
public sealed class EmbeddingConfig
{
    /// <summary>
    /// GPU execution provider preference. <c>auto</c> (default) probes DirectML
    /// (Windows) → CUDA (NVIDIA) → CPU in order. Other values: <c>dml</c> /
    /// <c>cuda</c> / <c>cpu</c>. <c>cpu</c> skips GPU probes entirely.
    /// </summary>
    public string Gpu { get; init; } = "auto";

    /// <summary>
    /// Model quantization preference. <c>auto</c> (default) uses INT8 quantized
    /// <c>model.int8.onnx</c> if downloaded (MiniLM does, BGE doesn't). <c>int8</c>
    /// requires quantized; <c>fp32</c> forces original FP32. P13.1.
    /// </summary>
    public string Quantization { get; init; } = "auto";

    /// <summary>GPU device ID (multi-GPU systems). Default 0.</summary>
    public int DeviceId { get; init; } = 0;

    /// <summary>
    /// P14.9: per-model quantization overrides. Keyed by model id
    /// (e.g. <c>minilm-l6-v2</c>, <c>bge-small-zh</c>); value is one of
    /// <c>int8</c> / <c>fp32</c> / <c>auto</c> (same vocabulary as
    /// <see cref="Quantization"/>). When a model is missing from this
    /// dictionary, <see cref="Quantization"/> is used as the fallback.
    /// <b>Priority: per-model &gt; global.</b>
    /// <example>
    /// appsettings.json:
    /// <code>
    /// "Embedding": {
    ///   "Quantization": "auto",
    ///   "Models": {
    ///     "minilm-l6-v2": "int8",
    ///     "bge-small-zh":  "fp32"
    ///   }
    /// }
    /// </code>
    /// </example>
    /// </summary>
    public IDictionary<string, string> Models { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// P14.9: resolve the effective quantization preference for a given
    /// model id. Looks up <see cref="Models"/> first, then falls back to
    /// <see cref="Quantization"/>. Returns <c>"auto"</c> if neither is set.
    /// </summary>
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

    /// <summary>
    /// P14.12: when <c>true</c>, the <c>PreWarmEmbeddingModelsHostedService</c>
    /// spawns a background task on host start that downloads every
    /// <see cref="LocalEmbedder.KnownModels"/> entry that's not already on
    /// disk. Default <c>false</c> — users opt in via appsettings.json. The
    /// service no-ops when <see cref="LocalEmbedder.DefaultDisabled"/> is
    /// <c>true</c> (remote API key is in use) or when
    /// <see cref="LocalEmbedder.BaseModelsDirectory"/> is null.
    /// </summary>
    public bool PreWarmAllModels { get; init; } = false;
}

/// <summary>
/// Steer model configuration — a lightweight, free/low-cost model used for
/// meta-decision tasks: response quality judging, safety pre-checks,
/// ambiguous routing, and summary verification. Not used for user-facing
/// conversation. Currently supports OpenAI-compatible endpoints
/// (SiliconFlow, Zhipu, etc.).
/// </summary>
/// <remarks>
/// Recommended providers:
/// <list type="bullet">
///   <item><b>SiliconFlow</b> (default): Qwen2.5-7B-Instruct, free API key at
///        <c>https://cloud.siliconflow.cn</c>, endpoint <c>https://api.siliconflow.cn/v1</c>.</item>
///   <item><b>Zhipu</b>: GLM-4-9B-Chat or GLM-Flash, endpoint
///        <c>https://open.bigmodel.cn/api/paas/v4</c>.</item>
/// </list>
/// When disabled, the system degrades gracefully to the current judgment path
/// (reusing the main agent for judging, DeepSeek for safety, etc.).
/// </remarks>
public sealed class SteerConfig
{
    /// <summary>Enable the steer model. Default <c>false</c> (opt-in).</summary>
    public bool Enabled { get; init; } = false;

    /// <summary>OpenAI-compatible endpoint URL.</summary>
    public string Endpoint { get; init; } = "https://api.siliconflow.cn/v1";

    /// <summary>Model name for steer tasks.</summary>
    public string? Model { get; init; } = null;

    /// <summary>Environment variable holding the API key.</summary>
    public string? ApiKeyEnv { get; init; } = null;

    /// <summary>LLM temperature for decision tasks (lower = more deterministic).</summary>
    public double Temperature { get; init; } = 0.2;

    /// <summary>Max output tokens per steer call.</summary>
    public int MaxTokens { get; init; } = 512;
}

/// <summary>
/// HTTP/SSE endpoint configuration for the ASP.NET Core host.
/// Loaded from appsettings.json under "LTAI:Web".
/// <b>Consumers:</b> TuiApp, Program files (bind port).
/// </summary>
public sealed class WebConfig
{
    public int Port { get; init; } = 5100;
    public string[] CorsOrigins { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Vector store configuration (local SQLite vs remote).
/// <b>Consumers:</b> KgStore, Reranker (initialization).
/// </summary>
public sealed class VectorConfig
{
    public string Provider { get; init; } = "local";
    public int EmbeddingDim { get; init; } = 384;

    /// <summary>
    /// Active IVectorStore backend. Default "hnsw" (in-memory HNSW with
    /// TurboQuant 4-bit). Future: "pgvector", "qdrant", "memory".
    /// Phase 1a: VectorStoreFactory uses this to select the implementation.
    /// </summary>
    public string Store { get; init; } = "hnsw";

    /// <summary>
    /// Dimensionality reduction setting. "none" (default) = use full 384-dim.
    /// "pca-128" / "pca-64" reduces to 128 or 64 dimensions via PCA.
    /// Phase 1b: used by embedding pipeline before IVectorStore insert/search.
    /// </summary>
    public string Reduction { get; init; } = "none";

    /// <summary>
    /// Product Quantization codec setting. "none" (default) = no PQ.
    /// "turboquant-4bit" / "pq-M8" / "pq-M16" for PQ with 8 or 16 sub-quantizers.
    /// Phase 1c: used transparently inside IVectorStore.
    /// </summary>
    public string Quantizer { get; init; } = "turboquant-4bit";
}

/// <summary>
/// MCP (Model Context Protocol) client configuration.
/// Each entry spawns a stdio MCP server process and exposes its tools
/// to the LTAI agent's tool list.
/// <b>Consumers:</b> Agent/ServiceCollectionExtensions.cs (BuildAgentImpl).
/// </summary>
public sealed class McpConfig
{
    public McpServerConfig[] Servers { get; init; } = Array.Empty<McpServerConfig>();
}

/// <summary>
/// Configuration for a single MCP server (stdio transport).
/// <b>Example appsettings.json entry:</b>
/// <code>
/// {
///   "LTAI": {
///     "Mcp": {
///       "Servers": [
///         { "Name": "filesystem", "Command": "npx", "Args": ["-y", "@modelcontextprotocol/server-filesystem", "C:\\workspace"] }
///       ]
///     }
///   }
/// }
/// </code>
/// </summary>
public sealed class McpServerConfig
{
    public string Name { get; init; } = "";
    public string Command { get; init; } = "";
    public string[] Args { get; init; } = Array.Empty<string>();
    public Dictionary<string, string>? Env { get; init; }
}

/// <summary>
/// Mirror download URLs for various tools and models.
/// Loaded from appsettings.json under "LTAI:Mirrors".
/// </summary>
public sealed class MirrorConfig
{
    public string WarpMsiUrl { get; init; } = "http://mogoo.com.cn/Cloudflare_WARP_2026.4.1390.0.msi";
    public string WindowsTerminalUrl { get; init; } = "http://mogoo.com.cn/Microsoft.WindowsTerminal_1.24.11321.0_x64.zip";
    public string RipGrepUrl { get; init; } = "http://mogoo.com.cn/rg.exe";
    public string ModelBaseUrl { get; init; } = "http://mogoo.com.cn/";
}

/// <summary>
/// Security-related settings (path restrictions, sandbox policies).
/// Loaded from appsettings.json under "LTAI:Security".
/// </summary>
public sealed class SecurityConfig
{
    /// <summary>Fallback PATH for sandboxed process execution. Default Windows system paths.</summary>
    public string SystemPathFallback { get; init; } = @"C:\Windows\system32;C:\Windows";
}

/// <summary>
/// Agent workflow parallelism and sandbox settings.
/// <b>Consumers:</b> WorkflowOrchestrator, CoordinationScheduler.
/// </summary>
public sealed class HarnessProfile
{
    public string Name { get; set; } = "development";
    public int MaxConcurrentWorkflows { get; set; } = 4;
    public string? SandboxType { get; set; }
    public bool EnableAuditTrail { get; set; } = true;
}

/// <summary>
/// MAF Durable Task pipeline (P8) configuration.
/// </summary>
public sealed class DurableConfig
{
    /// <summary>
    /// When <c>true</c>, every agent is wrapped in a <c>DurableAIAgentProxy</c>
    /// and all run state (messages, tool calls, function results) is persisted
    /// via the in-process DTFx gRPC sidecar. Default <c>true</c>.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Fixed loopback port for the in-process gRPC sidecar. <c>null</c> = auto
    /// (any free port). Pin only for debugging / tests.
    /// </summary>
    public int? SidecarPort { get; init; }

    /// <summary>
    /// SQLite file used by the in-process orchestration service for cross-restart
    /// persistence (P8.1). Relative paths are anchored at the current working
    /// directory. Defaults to <c>.livingtree/durability.db</c>.
    /// </summary>
    public string DatabasePath { get; init; } = ".livingtree/durability.db";
}

/// <summary>Session persistence configuration (P2).</summary>
public sealed class SessionConfig
{
    /// <summary>Directory for session JSON files. Default <c>.livingtree/sessions</c>.</summary>
    public string Path { get; init; } = ".livingtree/sessions";
    /// <summary>Max session files before pruning oldest. Default 500.</summary>
    public int MaxSessions { get; init; } = 500;
    /// <summary>Months before AES encryption key rotation. 0 = never rotate. Default 6.</summary>
    public int KeyRotationMonths { get; init; } = 6;
}

/// <summary>P15: Hot-editable workflow watch directory config.</summary>
public sealed class WorkflowsConfig
{
    /// <summary>
    /// Directory where user-editable <c>.yaml</c> / <c>.json</c> workflow files are stored.
    /// Default <c>.livingtree/workflows</c>.
    /// </summary>
    public string WatchDirectory { get; init; } = ".livingtree/workflows";
}

/// <summary>
/// Root configuration object, loaded from appsettings.json under section "LTAI".
/// Holds AI, Web, Vector, and Harness sub-configs plus runtime directory resolution.
/// Validated at startup by <see cref="LTAIOptionsValidator"/>.
/// </summary>
public sealed class LTAIOptions
{
    public const string SectionName = "LTAI";
    public WorkflowsConfig Workflows { get; init; } = new();
    public SessionConfig Session { get; init; } = new();
    public AIConfig AI { get; init; } = new();
    public WebConfig Web { get; init; } = new();
    public VectorConfig Vector { get; init; } = new();
    public HarnessProfile Harness { get; set; } = new();
    public McpConfig Mcp { get; init; } = new();
    public DurableConfig Durable { get; init; } = new();
    public AutoTuneConfig AutoTune { get; init; } = new();
    public EmbeddingConfig Embedding { get; init; } = new();
    public SteerConfig Steer { get; init; } = new();
    public MirrorConfig Mirrors { get; init; } = new();
    public SecurityConfig Security { get; init; } = new();
    public ProviderDefinition[] Providers { get; init; } = []; // overwrites KnownKeys.All when non-empty
    public string DataDirectory { get; init; } = ".livingtree";
    public string ToolsDirectory { get; init; } = "tools";
    public string[] SkillsUrls { get; init; } = Array.Empty<string>();
    public string PromptsDirectory { get; init; } = "prompts";
    public string MemoryDirectory { get; init; } = "memory";
    public string ModelsDirectory { get; init; } = "models";
    public string LogsDirectory { get; init; } = "logs";
    public int MaxHistoryMessages { get; init; } = 200;
    public bool EnableObservability { get; init; } = false;

    /// <summary>
    /// Resolve a path under the data directory. Env var LTAI_DATA_DIR overrides default.
    /// <b>Callers:</b> Agent/ServiceCollectionExtensions.cs (KgStore initialization).
    /// </summary>
    public string ResolveDataPath(string subPath) =>
        Path.Combine(EnvDataDir ?? AppContext.BaseDirectory, DataDirectory, subPath);

    /// <summary>
    /// Resolve a path under the tools directory. Env var LTAI_TOOLS_DIR overrides default.
    /// <b>Callers:</b> Agent/ServiceCollectionExtensions.cs.
    /// </summary>
    public string ResolveToolsPath(string? subPath = null) =>
        Path.Combine(EnvToolsDir ?? AppContext.BaseDirectory, ToolsDirectory, subPath ?? "");

    /// <summary>
    /// Resolve a path under the prompts directory. Env var LTAI_PROMPTS_DIR overrides default.
    /// <b>Callers:</b> Agent/ServiceCollectionExtensions.cs.
    /// </summary>
    public string ResolvePromptsPath(string? subPath = null) =>
        Path.Combine(EnvPromptsDir ?? AppContext.BaseDirectory, PromptsDirectory, subPath ?? "");

    /// <summary>
    /// Resolve a path under the memory directory. Env var LTAI_MEMORY_DIR overrides default.
    /// <b>Callers:</b> Desktop/MainWindow.cs.
    /// </summary>
    public string ResolveMemoryPath(string? subPath = null) =>
        Path.Combine(EnvMemoryDir ?? AppContext.BaseDirectory, MemoryDirectory, subPath ?? "");

    // ══ Env var overrides (private — consumers use Resolve* methods) ══
    private static readonly string? EnvDataDir = Environment.GetEnvironmentVariable("LTAI_DATA_DIR");
    private static readonly string? EnvToolsDir = Environment.GetEnvironmentVariable("LTAI_TOOLS_DIR");
    private static readonly string? EnvPromptsDir = Environment.GetEnvironmentVariable("LTAI_PROMPTS_DIR");
    private static readonly string? EnvMemoryDir = Environment.GetEnvironmentVariable("LTAI_MEMORY_DIR");

    /// <summary>
    /// Persist layer selection (L0/L1/L2) to appsettings.json at runtime.
    /// Creates the file if it doesn't exist. Uses AppContext.BaseDirectory
    /// which matches the AddJsonFile base path in TUI/Desktop/Web.
    /// </summary>
    public static void SaveLayerToAppSettings(string layer, string provider, string model)
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var path = Path.Combine(baseDir, "appsettings.json");

            System.Text.Json.Nodes.JsonNode json;
            if (File.Exists(path))
                json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            else
                json = new System.Text.Json.Nodes.JsonObject();

            var ltai = json["LTAI"] as System.Text.Json.Nodes.JsonObject
                       ?? new System.Text.Json.Nodes.JsonObject();
            json["LTAI"] = ltai;
            var ai = ltai["AI"] as System.Text.Json.Nodes.JsonObject
                     ?? new System.Text.Json.Nodes.JsonObject();
            ltai["AI"] = ai;

            ai[layer] = new System.Text.Json.Nodes.JsonObject
            {
                ["Provider"] = provider,
                ["Model"] = model
            };

            File.WriteAllText(path, json.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }
        catch { /* best-effort */ }
    }
}

/// <summary>JSON-serializable provider definition, mirrors <see cref="KnownKeys.KeyInfo"/>.</summary>
public sealed record ProviderDefinition(
    string EnvVar,
    string Service,
    string Description = "",
    string? Url = null,
    string? Endpoint = null,
    string? Model = null,
    decimal PriceInPerM = 0,
    decimal PriceOutPerM = 0,
    decimal PriceInCachePerM = 0);

