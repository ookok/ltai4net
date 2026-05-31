using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
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
    public string DefaultProvider { get; init; } = "deepseek";
    public string Model { get; init; } = "deepseek-v4-flash";
    public int MaxTokens { get; init; } = 4096;
    public double Temperature { get; init; } = 0.7;
    public string? ApiKeyEnv { get; init; } = "DEEPSEEK_API_KEY";
    /// <summary>Skip safety input/output guardrails. Default true for local dev tool.</summary>
    public bool SkipSafetyChecks { get; init; } = true;
    /// <summary>Operational mode: "balanced", "fast", "precise", etc.</summary>
    public string Mode { get; init; } = "balanced";
    /// <summary>Known LLM providers keyed by alias (e.g. "deepseek-fast", "deepseek-pro").</summary>
    public Dictionary<string, ProviderConfig> Providers { get; init; } = new();
    /// <summary>Degradation chain: on provider failure, try next in sequence."ProviderAlias" → "FallbackAlias".</summary>
    public Dictionary<string, string>? DegradationChain { get; init; }
    public long GlobalTokenBudget { get; init; } = 1_000_000;
    public long PerUserTokenBudget { get; init; } = 200_000;

    /// <summary>
    /// Resolve ProviderConfig by layer name ("fast"/"deep"/"pro"/"embedding"/custom).
    /// Falls back to a default ProviderConfig with model name only if layer not found.
    /// <b>Callers:</b> MultiProviderChatClient (builds IChatClient per layer), LLMConfigPanel.
    /// </summary>
    public ProviderConfig GetLayerConfig(string layer) => layer.ToLowerInvariant() switch
    {
        "fast" or "l1" => Providers.GetValueOrDefault("deepseek-fast") ?? new ProviderConfig { Model = "deepseek-v4-flash" },
        "deep" or "l2" or "pro" => Providers.GetValueOrDefault("deepseek-pro") ?? new ProviderConfig { Model = "deepseek-v4-pro" },
        "embedding" => Providers.GetValueOrDefault("embedding") ?? new ProviderConfig { Model = "text-embedding-3-small" },
        _ => Providers.GetValueOrDefault(layer) ?? new ProviderConfig { Model = Model }
    };
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
/// Root configuration object, loaded from appsettings.json under section "LTAI".
/// Holds AI, Web, Vector, and Harness sub-configs plus runtime directory resolution.
/// Validated at startup by <see cref="LTAIOptionsValidator"/>.
/// <b>Consumers:</b> All projects via DI — Agent/ServiceCollectionExtensions.cs,
/// MultiProviderChatClient, Desktop/Program.cs, TUI/Program.cs, test files.
/// </summary>
public sealed class LTAIOptions
{
    public const string SectionName = "LTAI";
    public AIConfig AI { get; init; } = new();
    public WebConfig Web { get; init; } = new();
    public VectorConfig Vector { get; init; } = new();
    public HarnessProfile Harness { get; set; } = new();
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
}

/// <summary>
/// Registry of all environment variables the system uses, with descriptions and pricing.
/// Serves as the single source of truth for:
///   - UI panels (which keys to show, their endpoints/models)
///   - Cost calculation (per-provider ¥/1M tokens)
///   - Provider config initialization (endpoint + model defaults)
/// Keys are NEVER stored in config files — only env vars, accessed via <see cref="SecretManager"/>.
/// <b>Consumers:</b> ConfigView, MainWindow, LLMConfigPanel (display);
/// EmbeddingClient, MultiProviderChatClient (provider init);
/// UsageTracker (pricing lookup).
/// </summary>
public static class KnownKeys
{
    /// <summary>
    /// Record for a single known API key's metadata.
    /// </summary>
    /// <param name="EnvVar">Environment variable name, e.g. "DEEPSEEK_API_KEY".</param>
    /// <param name="Service">Display name, e.g. "DeepSeek".</param>
    /// <param name="Description">Human-readable description with pricing.</param>
    /// <param name="Url">Link to the API key management page.</param>
    /// <param name="Endpoint">Default API endpoint URL.</param>
    /// <param name="Model">Default model name for this provider.</param>
    /// <param name="PriceInPerM">Input price per million tokens (¥).</param>
    /// <param name="PriceOutPerM">Output price per million tokens (¥).</param>
    /// <param name="PriceInCachePerM">Cached input price per million tokens (¥, default = PriceInPerM).</param>
    public sealed record KeyInfo(string EnvVar, string Service, string Description,
        string? Url = null, string? Endpoint = null, string? Model = null,
        decimal PriceInPerM = 0, decimal PriceOutPerM = 0,
        decimal PriceInCachePerM = 0);

    /// <summary>
    /// All known keys. Source of truth for UI panels and cost calculation.
    /// <b>Consumers:</b> ConfigView, MainWindow, LLMConfigPanel, UsageTracker.
    /// </summary>
    public static readonly KeyInfo[] All =
    [
        // ── LLM Providers (官方 ¥/1M tokens 价格，来源各官网定价页) ──
        new("DEEPSEEK_API_KEY",     "DeepSeek",       "输入¥1/输出¥2/缓存¥0.02 per 1M", "https://platform.deepseek.com/api_keys", "https://api.deepseek.com/v1", "deepseek-v4-flash", 1.0m, 2.0m, 0.02m),
        new("SILICONFLOW_API_KEY",  "SiliconFlow",    "¥1/¥2 per 1M",       "https://cloud.siliconflow.cn/", "https://api.siliconflow.cn/v1", "deepseek-ai/DeepSeek-V2.5", 1.0m, 2.0m),
        new("DASHSCOPE_API_KEY",    "Aliyun(Qwen)",   "¥0.8/¥2 per 1M",     "https://dashscope.console.aliyun.com/", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus", 0.8m, 2.0m),
        new("ZHIPU_API_KEY",        "Zhipu(GLM)",     "¥1/¥2 per 1M",       "https://open.bigmodel.cn/", "https://open.bigmodel.cn/api/paas/v4", "glm-4-plus", 1.0m, 2.0m),
        new("DOUBAO_API_KEY",       "Doubao",         "¥0.8/¥2 per 1M",     "https://console.volcengine.com/ark/", "https://ark.cn-beijing.volces.com/api/v3", "ep-XXXXXX", 0.8m, 2.0m),
        new("HUNYUAN_API_KEY",      "Hunyuan",        "¥1/¥2 per 1M",       "https://console.cloud.tencent.com/hunyuan", "https://api.hunyuan.cloud.tencent.com/v1", "hunyuan-pro", 1.0m, 2.0m),
        new("BAIDU_API_KEY",        "Baidu(ERNIE)",   "¥1.2/¥2.4 per 1M",  "https://console.bce.baidu.com/ai/", "https://aip.baidubce.com/rpc/2.0/ai_custom", "ernie-4.0", 1.2m, 2.4m),
        new("SPARK_API_KEY",        "iFlytek(Spark)", "¥0.5/¥1 per 1M",     "https://www.xfyun.cn/service/spark", "https://spark-api.xf-yun.com/v3.5/chat", "spark-3.5", 0.5m, 1.0m),
        new("MOONSHOT_API_KEY",     "Moonshot(Kimi)", "¥1/¥2 per 1M",       "https://platform.moonshot.cn/", "https://api.moonshot.cn/v1", "moonshot-v1-8k", 1.0m, 2.0m),
        new("BAICHUAN_API_KEY",     "Baichuan",       "¥0.5/¥1 per 1M",     "https://platform.baichuan-ai.com/", "https://api.baichuan-ai.com/v1", "Baichuan4", 0.5m, 1.0m),
        new("YI_API_KEY",           "Yi(01.AI)",      "¥1/¥2 per 1M",       "https://platform.lingyiwanwu.com/", "https://api.lingyiwanwu.com/v1", "yi-large", 1.0m, 2.0m),
        new("STEP_API_KEY",         "StepFun",        "¥1/¥2 per 1M",       "https://platform.stepfun.com/", "https://api.stepfun.com/v1", "step-2-16k", 1.0m, 2.0m),
        new("MINIMAX_API_KEY",      "Minimax",        "¥0.8/¥1.6 per 1M",   "https://platform.minimax.chat/", "https://api.minimax.chat/v1", "MiniMax-Text-01", 0.8m, 1.6m),
        new("OPENAI_API_KEY",       "OpenAI",         "≈¥10/¥30 per 1M",    "https://platform.openai.com/api-keys", "https://api.openai.com/v1", "gpt-4o", 10.0m, 30.0m),
        new("GROQ_API_KEY",         "Groq",           "免费",               "https://console.groq.com/keys", "https://api.groq.com/openai/v1", "llama-3.3-70b-versatile"),
        new("OPENROUTER_API_KEY",   "OpenRouter",     "按源模型定价",        "https://openrouter.ai/keys", "https://openrouter.ai/api/v1", "deepseek/deepseek-chat"),
        new("TOGETHER_API_KEY",     "Together AI",    "按源模型定价",        "https://api.together.xyz/", "https://api.together.xyz/v1", "mistralai/Mixtral-8x22B-Instruct-v0.1"),
        new("MISTRAL_API_KEY",      "Mistral",        "≈¥8/¥24 per 1M",    "https://console.mistral.ai/", "https://api.mistral.ai/v1", "mistral-large-latest", 8.0m, 24.0m),
        new("PERPLEXITY_API_KEY",   "Perplexity",     "¥2/¥8 per 1M",       "https://docs.perplexity.ai/", "https://api.perplexity.ai", "sonar-pro", 2.0m, 8.0m),
        new("XAI_API_KEY",          "X.AI(Grok)",     "¥3/¥5 per 1M",       "https://console.x.ai/", "https://api.x.ai/v1", "grok-2-1212", 3.0m, 5.0m),
        new("COHERE_API_KEY",       "Cohere",         "≈¥5/¥15 per 1M",    "https://dashboard.cohere.com/", "https://api.cohere.ai/v1", "command-r-plus", 5.0m, 15.0m),
        new("FIREWORKS_API_KEY",    "Fireworks AI",   "¥0.9/¥0.9 per 1M",   "https://fireworks.ai/", "https://api.fireworks.ai/inference/v1", "accounts/fireworks/models/llama-v3p3-70b-instruct", 0.9m, 0.9m),
        new("MIMO_API_KEY",         "小米 MiMo",     "输入¥1/输出¥2 per 1M", "https://dev.mi.com/", "https://api.mimo.mi.com/v1", "deepseek-v4", 1.0m, 2.0m),
        // ── Web Search ──
        new("BRAVE_API_KEY",        "Brave Search",   "网页搜索（默认）",  "https://brave.com/search/api/"),
        new("SERPER_API_KEY",       "Serper(Google)", "Google 搜索（备用）", "https://serper.dev/"),
        // ── Map / GIS ──
        new("AMAP_KEY",             "高德地图",       "地理编码/逆编码/POI", "https://console.amap.com/"),
        new("TENCENT_MAP_KEY",      "腾讯地图",       "地理编码/逆编码",   "https://lbs.qq.com/"),
        new("BAIDU_MAP_KEY",        "百度地图",       "地理编码/逆编码",   "https://lbsyun.baidu.com/"),
        new("TIANDITU_KEY",         "天地图",         "国家地理信息平台",  "https://console.tianditu.gov.cn/"),
        // ── Weather ──
        new("WEATHER_KEY",          "和风天气",       "实时天气/预报",     "https://console.qweather.com/"),
        // ── Translation ──
        new("BAIDU_TRANSLATE_APPID","百度翻译",       "APP ID",            "https://api.fanyi.baidu.com/"),
        new("BAIDU_TRANSLATE_SECRET","百度翻译",      "SECRET",            "https://api.fanyi.baidu.com/"),
        // ── Image ──
        new("UNSPLASH_KEY",         "Unsplash",       "图片搜索 API",      "https://unsplash.com/developers"),
    ];

    /// <summary>
    /// Generate default provider configurations in tuple format.
    /// Filters to providers that have both an endpoint and a model defined.
    /// <b>Callers:</b> MultiProviderChatClient (initialize default providers).
    /// </summary>
    public static (string envVar, string endpoint, string model, string name)[] GetDefaultProviders() =>
        All.Where(k => k.Endpoint != null && k.Model != null)
           .Select(k => (k.EnvVar, k.Endpoint!, k.Model!, k.Service))
           .ToArray();

    /// <summary>
    /// Get all keys grouped by service category for UI display.
    /// Categories: "LLM Providers", "Map / GIS", "Web Search", "Weather", "Translation", "Image", "Other".
    /// <b>Consumers:</b> ConfigView (category tabs).
    /// </summary>
    public static ILookup<string, KeyInfo> ByCategory =>
        All.ToLookup(k => k.Service.Contains("API") ? "LLM Providers"
                      : k.EnvVar.Contains("MAP") || k.EnvVar.Contains("TIANDITU") ? "Map / GIS"
                      : k.EnvVar.Contains("BRAVE") || k.EnvVar.Contains("SERPER") ? "Web Search"
                      : k.EnvVar.Contains("WEATHER") ? "Weather"
                      : k.EnvVar.Contains("TRANSLATE") ? "Translation"
                      : k.EnvVar.Contains("UNSPLASH") ? "Image"
                      : "Other");
}

/// <summary>
/// Interface for token/cost tracking. Inject via DI for per-scope tracking,
/// or use static <see cref="UsageTracker.Current"/> for existing callers.
/// Implementations must be thread-safe.
/// </summary>
public interface IUsageTracker
{
    /// <summary>Record token usage from an API call.</summary>
    void Record(int prompt, int completion, string model = "");
    /// <summary>Record token usage with cache breakdown (三档计价).</summary>
    void RecordWithCache(int prompt, int completion, int cacheHit, int cacheMiss, string model);
    /// <summary>Record a response cache hit.</summary>
    void RecordCacheHit();
    /// <summary>Record a response cache miss.</summary>
    void RecordCacheMiss();
    /// <summary>Total prompt tokens.</summary>
    long PromptTokens { get; }
    /// <summary>Total completion tokens.</summary>
    long CompletionTokens { get; }
    /// <summary>Total requests.</summary>
    long Requests { get; }
    /// <summary>Estimated cost in ¥.</summary>
    decimal EstimatedCost { get; }
    /// <summary>Cache hit count.</summary>
    long CacheHits { get; }
    /// <summary>Cache miss count.</summary>
    long CacheMisses { get; }
    /// <summary>Cache hit rate (0-100%).</summary>
    double CacheHitRate { get; }
    /// <summary>Context usage ratio 0.0-1.0.</summary>
    double ContextRatio(int contextWindowOverride = 0);
    /// <summary>Context usage text (e.g. "12,345/64,000 (19.3%)").</summary>
    string ContextText(int contextWindowOverride = 0);
    /// <summary>One-line summary of session stats.</summary>
    string Summary();
    /// <summary>Cost display string (¥ prefix).</summary>
    string CostDisplay { get; }
    /// <summary>Active model name.</summary>
    string ActiveModel { get; }
    /// <summary>Account balance display.</summary>
    string BalanceDisplay { get; }
    /// <summary>Fetch balance from provider API (best-effort).</summary>
    Task FetchBalanceAsync(string defaultProvider, string? apiKey = null);
    /// <summary>Set active model name.</summary>
    void SetActiveModel(string model);
    /// <summary>Set context window size.</summary>
    void SetContextWindowSize(int size);
    /// <summary>Cache hit tokens (from API).</summary>
    long CacheHitTokens { get; }
    /// <summary>Cache miss tokens (from API).</summary>
    long CacheMissTokens { get; }
    /// <summary>Tool call count.</summary>
    long ToolCalls { get; }
    /// <summary>Cache saved amount display.</summary>
    string CacheSavedDisplay { get; }
    /// <summary>Record streaming metrics for t/s calculation.</summary>
    void RecordStreamingMetrics(long completionTokens, long elapsedMs);
    /// <summary>Current tokens-per-second (null if insufficient data).</summary>
    double? CurrentTps { get; }
    /// <summary>Tool call count.</summary>
    string TpsDisplay { get; }
    /// <summary>Set currently executing tool name (for TUI animation).</summary>
    void SetActiveTool(string toolName);
    /// <summary>Currently executing tool name, empty if none.</summary>
    string CurrentTool { get; }
}

/// <summary>
/// Default implementation of <see cref="IUsageTracker"/>.
/// Thread-safe via Interlocked and lock. Uses per-provider pricing from <see cref="KnownKeys.All"/>.
/// Supports optional scoped tracking (see <see cref="BeginScope"/>) for per-request cost attribution.
/// <b>Consumers:</b> Cli/Program.cs (dashboard), DashboardView, SessionStatsPanel,
/// TuiApp (status bar), CoreTests, MultiProviderChatClient (Record calls).
/// 
/// Backward-compat static forwarding: all static members delegate to <see cref="Default"/>,
/// so existing callers like <c>UsageTracker.Record(...)</c> continue to work unchanged.
/// New code should inject <see cref="IUsageTracker"/> via DI.
/// </summary>
public sealed class UsageTracker : IUsageTracker
{
    /// <summary>Global default instance. All static methods forward here.</summary>
    public static readonly UsageTracker Default = new();

    /// <summary>Current scoped tracker (if set via DI), or <see cref="Default"/>.</summary>
    internal static readonly AsyncLocal<UsageTracker?> Scoped = new();

    private static UsageTracker Current => Scoped.Value ?? Default;

    // ── Instance methods ──

    public UsageTracker() { }

    /// <summary>
    /// Begin a scoped cost tracking session. Records token/cost deltas on dispose.
    /// Useful for per-request or per-conversation cost attribution in multi-tenant scenarios.
    /// Nested scopes are supported — each records from its start snapshot.
    /// Example:
    /// <code>
    /// using (var scope = UsageTracker.BeginScope())
    /// {
    ///     await llm.GetResponseAsync(...);
    ///     Console.WriteLine($"This request cost {scope.Cost:F4}¥");
    /// }
    /// </code>
    /// </summary>
    public UsageScope BeginScope() => new(
        Interlocked.Read(ref _promptTokens),
        Interlocked.Read(ref _completionTokens),
        _totalCost);

    /// <summary>Static forwarding to <see cref="Default"/>.</summary>
    public static UsageScope BeginScopeStatic() => Default.BeginScope();

    /// <summary>
    /// Records token/cost deltas within a scope. Created by <see cref="BeginScope"/>.
    /// Dispose to record the scope's contribution to the aggregate.
    /// </summary>
    public sealed class UsageScope : IDisposable
    {
        private readonly long _startPrompt;
        private readonly long _startCompletion;
        private readonly double _startCost;
        internal UsageScope(long startPrompt, long startCompletion, double startCost)
        {
            _startPrompt = startPrompt;
            _startCompletion = startCompletion;
            _startCost = startCost;
        }

        /// <summary>Prompt tokens used within this scope.</summary>
        public long PromptDelta => Interlocked.Read(ref _promptTokens) - _startPrompt;
        /// <summary>Completion tokens used within this scope.</summary>
        public long CompletionDelta => Interlocked.Read(ref _completionTokens) - _startCompletion;
        /// <summary>Estimated cost (¥) within this scope.</summary>
        public decimal Cost => (decimal)(Volatile.Read(ref _totalCost) - _startCost);

        public void Dispose() { }
    }

    // ══ Shared state (static, shared by Default + any scoped instances) ══
    private static long _promptTokens;
    private static long _completionTokens;
    private static double _totalCost;
    private static readonly object _costLock = new();
    private static long _requests;
    private static readonly Stopwatch _timer = Stopwatch.StartNew();
    private static string _activeModel = "";
    private static long _cacheHits;          // 计数：缓存响应命中次数（缓存层）
    private static long _cacheMisses;        // 计数：缓存响应未命中次数
    private static long _cacheHitTokens;     // Token 级：API 返回的 prompt_cache_hit_tokens
    private static long _cacheMissTokens;    // Token 级：API 返回的 prompt_cache_miss_tokens
    private static long _toolCalls;          // Tool call 累计次数
    private static string _currentTool = ""; // 当前正在执行的工具名（供 TUI 动画读取）
    private static long _lastToolCallMs;
    private static long _lastLlmCallMs;
    private static readonly System.Diagnostics.Stopwatch _currentToolStopwatch = new();
    private static long _lastStreamTokens;   // 最近一次流式 completion tokens
    private static long _lastStreamElapsedMs; // 最近一次流式耗时(ms)
    private static int _contextWindowSize = 64000;
    private static double _balance;
    private static string _balanceCurrency = "";
    private static string _balanceSource = "";
    private static readonly HttpClient _balanceHttp = new() { Timeout = TimeSpan.FromSeconds(5) };
    /// <summary>Model context window cache, populated from provider /v1/models API.</summary>
    private static readonly ConcurrentDictionary<string, int> _modelContextCache = new(StringComparer.OrdinalIgnoreCase);

    // ══ IUsageTracker explicit implementation (accessible when cast to interface) ══
    void IUsageTracker.Record(int prompt, int completion, string model) => RecordInternal(prompt, completion, model);
    void IUsageTracker.RecordWithCache(int prompt, int completion, int cacheHit, int cacheMiss, string model) => RecordInternal(prompt, completion, cacheHit, cacheMiss, model);

    // Last-matched model cache to avoid repeated linear scans of KnownKeys.All
    private static string? _lastLookupModel;
    private static KnownKeys.KeyInfo? _lastLookupKey;

    private static KnownKeys.KeyInfo? LookupModel(string model)
    {
        if (string.IsNullOrEmpty(model)) return null;
        if (string.Equals(_lastLookupModel, model, StringComparison.OrdinalIgnoreCase))
            return _lastLookupKey;
        _lastLookupModel = model;
        return _lastLookupKey = KnownKeys.All.FirstOrDefault(k =>
            !string.IsNullOrEmpty(k.Model) && model.StartsWith(k.Model, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Core Record logic — called by both static forwarder and interface impl.</summary>
    private static void RecordInternal(int prompt, int completion, string model)
    {
        Interlocked.Add(ref _promptTokens, prompt);
        Interlocked.Add(ref _completionTokens, completion);
        Interlocked.Increment(ref _requests);
        if (!string.IsNullOrEmpty(model)) _activeModel = model;

        var key = LookupModel(model);
        double cost;
        if (key != null && (key.PriceInPerM > 0 || key.PriceOutPerM > 0))
        {
            cost = (prompt / 1_000_000.0) * (double)key.PriceInPerM
                 + (completion / 1_000_000.0) * (double)key.PriceOutPerM;
        }
        else
        {
            cost = (prompt / 1_000_000.0) * 1.0
                 + (completion / 1_000_000.0) * 4.0;
        }
        lock (_costLock) { _totalCost += cost; }
    }

    /// <summary>Record with cache token breakdown — 三档计价 (cacheHit / cacheMiss / output).</summary>
    private static void RecordInternal(int prompt, int completion, int cacheHit, int cacheMiss, string model)
    {
        Interlocked.Add(ref _promptTokens, prompt);
        Interlocked.Add(ref _completionTokens, completion);
        Interlocked.Add(ref _cacheHitTokens, cacheHit);
        Interlocked.Add(ref _cacheMissTokens, cacheMiss);
        Interlocked.Increment(ref _requests);
        if (!string.IsNullOrEmpty(model)) _activeModel = model;

        var key = LookupModel(model);
        double cost;
        if (key != null && (key.PriceInPerM > 0 || key.PriceOutPerM > 0))
        {
            var cachePrice = key.PriceInCachePerM > 0 ? (double)key.PriceInCachePerM : (double)key.PriceInPerM;
            cost = (cacheHit / 1_000_000.0) * cachePrice
                 + (cacheMiss / 1_000_000.0) * (double)key.PriceInPerM
                 + (completion / 1_000_000.0) * (double)key.PriceOutPerM;
        }
        else
        {
            cost = (prompt / 1_000_000.0) * 1.0
                 + (completion / 1_000_000.0) * 4.0;
        }
        lock (_costLock) { _totalCost += cost; }
    }

    /// <summary>Static forwarding — delegates to <see cref="Default"/>.</summary>
    public static void Record(int prompt, int completion, string model = "") => RecordInternal(prompt, completion, model);
    public static void RecordWithCache(int prompt, int completion, int cacheHit, int cacheMiss, string model)
        => RecordInternal(prompt, completion, cacheHit, cacheMiss, model);

    // ══ IUsageTracker explicit implementation (cast to interface to access) ══
    long IUsageTracker.PromptTokens => Interlocked.Read(ref _promptTokens);
    long IUsageTracker.CompletionTokens => Interlocked.Read(ref _completionTokens);
    long IUsageTracker.Requests => Interlocked.Read(ref _requests);
    decimal IUsageTracker.EstimatedCost { get { lock (_costLock) { return (decimal)_totalCost; } } }
    string IUsageTracker.CostDisplay => $"¥{((IUsageTracker)this).EstimatedCost:F4}";
    string IUsageTracker.ActiveModel => !string.IsNullOrEmpty(_activeModel) ? _activeModel : "deepseek-v4-flash";
    void IUsageTracker.SetActiveModel(string model) => _activeModel = model;
    void IUsageTracker.RecordCacheHit() => Interlocked.Increment(ref _cacheHits);
    void IUsageTracker.RecordCacheMiss() => Interlocked.Increment(ref _cacheMisses);
    long IUsageTracker.CacheHits => Interlocked.Read(ref _cacheHits);
    long IUsageTracker.CacheMisses => Interlocked.Read(ref _cacheMisses);
    double IUsageTracker.CacheHitRate
    {
        get
        {
            var hitT = Interlocked.Read(ref _cacheHitTokens);
            var missT = Interlocked.Read(ref _cacheMissTokens);
            if (hitT + missT > 0)
                return (double)hitT / (hitT + missT) * 100;
            var hitC = Interlocked.Read(ref _cacheHits);
            var missC = Interlocked.Read(ref _cacheMisses);
            return hitC + missC > 0 ? (double)hitC / (hitC + missC) * 100 : 0;
        }
    }
    double IUsageTracker.ContextRatio(int ovr) => CalcContextRatio(ovr);
    string IUsageTracker.ContextText(int ovr) => CalcContextText(ovr);
    string IUsageTracker.BalanceDisplay => BalanceDisplayStatic;
    void IUsageTracker.SetContextWindowSize(int size) => _contextWindowSize = size;
    async Task IUsageTracker.FetchBalanceAsync(string p, string? k) => await FetchBalanceStaticAsync(p, k);
    string IUsageTracker.Summary() => BuildSummary();
    long IUsageTracker.CacheHitTokens => Interlocked.Read(ref _cacheHitTokens);
    long IUsageTracker.CacheMissTokens => Interlocked.Read(ref _cacheMissTokens);
    long IUsageTracker.ToolCalls => Interlocked.Read(ref _toolCalls);
    string IUsageTracker.CacheSavedDisplay => CacheSavedDisplay;
    void IUsageTracker.RecordStreamingMetrics(long completionTokens, long elapsedMs)
    {
        Interlocked.Exchange(ref _lastStreamTokens, completionTokens);
        Interlocked.Exchange(ref _lastStreamElapsedMs, elapsedMs);
    }
    double? IUsageTracker.CurrentTps => CurrentTps;
    string IUsageTracker.TpsDisplay => TpsDisplay;
    void IUsageTracker.SetActiveTool(string toolName) => _currentTool = toolName;
    string IUsageTracker.CurrentTool => _currentTool;

    // ══ Public static members (same names as before — backward compatible) ══
    public static long PromptTokens => Interlocked.Read(ref _promptTokens);
    public static long CompletionTokens => Interlocked.Read(ref _completionTokens);
    public static long TotalTokens => PromptTokens + CompletionTokens;
    public static long Requests => Interlocked.Read(ref _requests);
    public static TimeSpan Uptime => _timer.Elapsed;
    public static decimal EstimatedCost { get { lock (_costLock) { return (decimal)_totalCost; } } }
    public static string CostDisplay => $"¥{EstimatedCost:F4}";
    public static string ActiveModel => !string.IsNullOrEmpty(_activeModel) ? _activeModel : "deepseek-v4-flash";
    public static void SetActiveModel(string model) => _activeModel = model;
    public static void RecordCacheHit() => Interlocked.Increment(ref _cacheHits);
    public static void RecordCacheMiss() => Interlocked.Increment(ref _cacheMisses);
    public static long CacheHits => Interlocked.Read(ref _cacheHits);
    public static long CacheMisses => Interlocked.Read(ref _cacheMisses);
    public static void RecordCacheTokens(long hitTokens, long missTokens)
    {
        Interlocked.Add(ref _cacheHitTokens, hitTokens);
        Interlocked.Add(ref _cacheMissTokens, missTokens);
    }
    public static long CacheHitTokens => Interlocked.Read(ref _cacheHitTokens);
    public static long CacheMissTokens => Interlocked.Read(ref _cacheMissTokens);
    public static double CacheHitRate
    {
        get
        {
            var hitT = Interlocked.Read(ref _cacheHitTokens);
            var missT = Interlocked.Read(ref _cacheMissTokens);
            if (hitT + missT > 0)
                return (double)hitT / (hitT + missT) * 100;
            // Fallback: 计数比（缓存层命中）
            var hitC = Interlocked.Read(ref _cacheHits);
            var missC = Interlocked.Read(ref _cacheMisses);
            return hitC + missC > 0 ? (double)hitC / (hitC + missC) * 100 : 0;
        }
    }
    /// <summary>缓存节省金额 (¥) = cacheHitTokens × (missPrice − hitPrice) / 1M</summary>
    public static string CacheSavedDisplay
    {
        get
        {
            var hitT = Interlocked.Read(ref _cacheHitTokens);
            if (hitT == 0) return "¥0.0000";
            // DeepSeek V4 Flash: miss=¥1.0/M, hit=¥0.02/M
            var saved = hitT / 1_000_000.0 * (1.0 - 0.02);
            return $"¥{saved:F4}";
        }
    }
    public static void RecordToolCall() => Interlocked.Increment(ref _toolCalls);
    public static long ToolCalls => Interlocked.Read(ref _toolCalls);
    /// <summary>设置当前正在执行的工具名（用于 TUI 动画实时显示）。</summary>
    public static void SetActiveTool(string toolName) => _currentTool = toolName;
    /// <summary>当前正在执行的工具名，空字符串表示无活跃工具。</summary>
    public static string CurrentTool => _currentTool;
    /// <summary>开始工具调用计时。每次调用前重置，支持重入。</summary>
    public static void StartToolTimer()
    {
        _currentToolStopwatch.Restart();
    }
    /// <summary>停止工具调用计时并记录耗时 (ms)。</summary>
    public static void StopToolTimer()
    {
        _currentToolStopwatch.Stop();
        Interlocked.Exchange(ref _lastToolCallMs, _currentToolStopwatch.ElapsedMilliseconds);
    }
    /// <summary>最近一次工具调用耗时 (ms)。</summary>
    public static long ToolCallMs => Interlocked.Read(ref _lastToolCallMs);
    /// <summary>最近一次工具调用耗时格式化显示。</summary>
    public static string ToolCallTimeDisplay
    {
        get
        {
            var ms = Interlocked.Read(ref _lastToolCallMs);
            return ms >= 1000 ? $"{ms / 1000.0:F1}s" : ms > 0 ? $"{ms}ms" : "";
        }
    }
    /// <summary>记录最近一次 LLM 调用耗时 (ms)。</summary>
    public static void RecordLlmCallDuration(long latencyMs)
    {
        Interlocked.Exchange(ref _lastLlmCallMs, latencyMs);
    }
    /// <summary>最近一次 LLM 调用耗时 (ms)。</summary>
    public static long LlmCallMs => Interlocked.Read(ref _lastLlmCallMs);
    /// <summary>最近一次 LLM 调用耗时格式化显示。</summary>
    public static string LlmCallTimeDisplay
    {
        get
        {
            var ms = Interlocked.Read(ref _lastLlmCallMs);
            return ms >= 1000 ? $"{ms / 1000.0:F1}s" : ms > 0 ? $"{ms}ms" : "";
        }
    }
    /// <summary>记录流式请求的 token 数和耗时，用于计算响应速率。</summary>
    public static void RecordStreamingMetrics(long completionTokens, long elapsedMs)
    {
        Interlocked.Exchange(ref _lastStreamTokens, completionTokens);
        Interlocked.Exchange(ref _lastStreamElapsedMs, elapsedMs);
    }
    /// <summary>最近一次流式请求的响应速率 (tokens/sec)，不可用时返回 null。</summary>
    public static double? CurrentTps
    {
        get
        {
            var tok = Interlocked.Read(ref _lastStreamTokens);
            var ms = Interlocked.Read(ref _lastStreamElapsedMs);
            if (ms < 500 || tok < 4) return null; // 同 Reasonix 最小阈值
            return Math.Round(tok * 1000.0 / ms, 1);
        }
    }
    public static string TpsDisplay
    {
        get
        {
            var tps = CurrentTps;
            return tps.HasValue ? $"{tps:F0} t/s" : "";
        }
    }
    public static void SetContextWindowSize(int size) => _contextWindowSize = size;
    public static double ContextRatio(int contextWindowOverride = 0) => CalcContextRatio(contextWindowOverride);
    public static string ContextText(int contextWindowOverride = 0) => CalcContextText(contextWindowOverride);
    public static string BalanceDisplay => BalanceDisplayStatic;
    public static async Task FetchBalanceAsync(string defaultProvider, string? apiKey = null)
        => await FetchBalanceStaticAsync(defaultProvider, apiKey);
    public static string Summary() => BuildSummary();

    // ══ Internal helpers (shared by static + interface impl) ══
    /// <summary>Refresh model info cache from provider API (GET /v1/models).</summary>
    public static async Task RefreshModelInfoAsync(string endpoint, string apiKey)
    {
        try
        {
            if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(apiKey)) return;
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{endpoint.TrimEnd('/')}/models");
            req.Headers.Authorization = new("Bearer", apiKey);
            using var resp = await _balanceHttp.SendAsync(req).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var json = JsonDocument.Parse(body);
            foreach (var model in json.RootElement.GetProperty("data").EnumerateArray())
            {
                var id = model.GetProperty("id").GetString();
                if (id == null) continue;
                if (model.TryGetProperty("max_context_length", out var ctx))
                    _modelContextCache[id] = ctx.GetInt32();
                else if (model.TryGetProperty("context_length", out var ctx2))
                    _modelContextCache[id] = ctx2.GetInt32();
            }
        }
        catch { /* best-effort */ }
    }

    /// <summary>Get effective context window: API cache > hardcoded fallback > configured MaxTokens.</summary>
    private static int EffectiveContextWindow(int ovr)
    {
        if (ovr > 0) return ovr;
        // 优先从当前模型名查 KnownContextWindows（API 缓存优先）
        var modelKey = _activeModel;
        if (string.IsNullOrEmpty(modelKey))
            modelKey = "deepseek-v4-flash"; // 默认 Fallback
        var modelLimit = _modelContextCache.TryGetValue(modelKey, out var cached)
            ? cached
            : KnownContextWindows.TryGetValue(modelKey, out var known) ? known : 0;
        return Math.Max(modelLimit, _contextWindowSize);
    }
    private static double CalcContextRatio(int ovr)
    {
        var max = EffectiveContextWindow(ovr);
        if (max <= 0) return 0;
        return Math.Clamp((double)(PromptTokens % (max + 1)) / max, 0, 1);
    }
    private static string CalcContextText(int ovr)
    {
        var max = EffectiveContextWindow(ovr);
        if (max <= 0) return "";
        return $"{PromptTokens:N0}/{max:N0} ({(double)PromptTokens / max * 100:F1}%)";
    }
    private static string BalanceDisplayStatic =>
        string.IsNullOrEmpty(_balanceSource) ? "N/A"
        : $"{_balanceCurrency}{_balance:F2} ({_balanceSource})";
    private static async Task FetchBalanceStaticAsync(string defaultProvider, string? apiKey)
    {
        try
        {
            if (string.IsNullOrEmpty(apiKey)) return;

            if (defaultProvider.Contains("deepseek", StringComparison.OrdinalIgnoreCase))
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.deepseek.com/user/balance");
                req.Headers.Authorization = new("Bearer", apiKey);
                using var resp = await _balanceHttp.SendAsync(req);
                resp.EnsureSuccessStatusCode();
                var body = await resp.Content.ReadAsStringAsync();
                using var json = JsonDocument.Parse(body);
                var root = json.RootElement;
                // DeepSeek balance API 实际格式：
                // {"is_available":true,"balance_infos":[{"currency":"CNY","total_balance":"110.00",...}]}
                if (root.TryGetProperty("balance_infos", out var infos) && infos.GetArrayLength() > 0)
                {
                    var info = infos[0];
                    var totalStr = info.GetProperty("total_balance").GetString() ?? "0";
                    var currency = info.GetProperty("currency").GetString() ?? "CNY";
                    _balance = double.Parse(totalStr, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture);
                    _balanceCurrency = currency == "CNY" ? "¥" : currency;
                    _balanceSource = "DeepSeek";
                }
                else
                {
                    // Fallback: 旧格式
                    var bal = root.TryGetProperty("balance", out var b) ? b.GetDouble() : 0;
                    _balance = bal; _balanceCurrency = "¥"; _balanceSource = "DeepSeek";
                }
            }
            else if (defaultProvider.Contains("siliconflow", StringComparison.OrdinalIgnoreCase))
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.siliconflow.cn/v1/user/balance");
                req.Headers.Authorization = new("Bearer", apiKey);
                using var resp = await _balanceHttp.SendAsync(req);
                resp.EnsureSuccessStatusCode();
                var body = await resp.Content.ReadAsStringAsync();
                using var json = JsonDocument.Parse(body);
                var bal = json.RootElement.GetProperty("balance").GetDouble();
                _balance = bal; _balanceCurrency = "¥"; _balanceSource = "SiliconFlow";
            }
            else if (defaultProvider.Contains("openrouter", StringComparison.OrdinalIgnoreCase))
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://openrouter.ai/api/v1/auth/key");
                req.Headers.Authorization = new("Bearer", apiKey);
                using var resp = await _balanceHttp.SendAsync(req);
                resp.EnsureSuccessStatusCode();
                var body = await resp.Content.ReadAsStringAsync();
                using var json = JsonDocument.Parse(body);
                var credits = json.RootElement.GetProperty("data").GetProperty("credits").GetDouble();
                _balance = credits; _balanceCurrency = "$"; _balanceSource = "OpenRouter";
            }
            else if (defaultProvider.Contains("zhipu", StringComparison.OrdinalIgnoreCase))
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://open.bigmodel.cn/api/llm/balance");
                req.Headers.Authorization = new("Bearer", apiKey);
                using var resp = await _balanceHttp.SendAsync(req);
                resp.EnsureSuccessStatusCode();
                var body = await resp.Content.ReadAsStringAsync();
                using var json = JsonDocument.Parse(body);
                var bal = json.RootElement.GetProperty("data").GetProperty("total_balance").GetDouble();
                _balance = bal; _balanceCurrency = "¥"; _balanceSource = "Zhipu(GLM)";
            }
            else if (defaultProvider.Contains("aliyun", StringComparison.OrdinalIgnoreCase) ||
                     defaultProvider.Contains("dashscope", StringComparison.OrdinalIgnoreCase))
            {
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    "https://dashscope.aliyuncs.com/api/v1/services/llm/balance");
                req.Headers.Authorization = new("Bearer", apiKey);
                using var resp = await _balanceHttp.SendAsync(req);
                resp.EnsureSuccessStatusCode();
                var body = await resp.Content.ReadAsStringAsync();
                using var json = JsonDocument.Parse(body);
                var bal = json.RootElement.GetProperty("available_balance").GetDouble();
                _balance = bal; _balanceCurrency = "¥"; _balanceSource = "Aliyun(Qwen)";
            }
            else if (defaultProvider.Contains("moonshot", StringComparison.OrdinalIgnoreCase))
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.moonshot.cn/v1/balance");
                req.Headers.Authorization = new("Bearer", apiKey);
                using var resp = await _balanceHttp.SendAsync(req);
                resp.EnsureSuccessStatusCode();
                var body = await resp.Content.ReadAsStringAsync();
                using var json = JsonDocument.Parse(body);
                var bal = json.RootElement.GetProperty("available_balance").GetDouble();
                _balance = bal; _balanceCurrency = "¥"; _balanceSource = "Moonshot(Kimi)";
            }
        }
        catch { /* best-effort */ }
    }

    /// <summary>Known context windows for common models (used when config MaxTokens is smaller).</summary>
    private static readonly Dictionary<string, int> KnownContextWindows = new(StringComparer.OrdinalIgnoreCase)
    {
        // DeepSeek (V4 全系列 1M context, 384K output)
        ["deepseek-chat"] = 1048576,
        ["deepseek-reasoner"] = 1048576,
        ["deepseek-v4-flash"] = 1048576,
        ["deepseek-v4-pro"] = 1048576,
        ["deepseek-v3"] = 65536,
        // OpenAI
        ["gpt-4o"] = 131072,
        ["gpt-4o-mini"] = 131072,
        ["gpt-4-turbo"] = 131072,
        ["gpt-4"] = 8192,
        ["gpt-3.5-turbo"] = 16384,
        // Aliyun / DashScope
        ["qwen-plus"] = 131072,
        ["qwen-max"] = 32768,
        ["qwen-turbo"] = 131072,
        ["qwen-long"] = 1048576,
        // Zhipu
        ["glm-4-plus"] = 131072,
        ["glm-4"] = 131072,
        ["glm-4-flash"] = 131072,
        // Moonshot
        ["moonshot-v1-8k"] = 8192,
        ["moonshot-v1-32k"] = 32768,
        ["moonshot-v1-128k"] = 131072,
        // Claude
        ["claude-3-5-sonnet"] = 204800,
        ["claude-3-haiku"] = 204800,
        ["claude-3-opus"] = 204800,
        // Groq
        ["llama-3.3-70b"] = 131072,
        ["llama-3.1-8b"] = 131072,
        ["mixtral-8x7b"] = 32768,
        // Mistral
        ["mistral-large"] = 131072,
        ["mistral-small"] = 32768,
        // Perplexity
        ["sonar-pro"] = 131072,
        ["sonar"] = 131072,
        // X.AI
        ["grok-2"] = 131072,
        // Fireworks
        ["llama-v3p3"] = 131072,
    };
    private static string BuildSummary()
    {
        var p = PromptTokens;
        var c = CompletionTokens;
        return $"Tokens: {p:N0}+{c:N0}={TotalTokens:N0} | "
             + $"Requests: {Requests} | "
             + $"Cost: ¥{EstimatedCost:F4} | "
             + $"Uptime: {_timer.Elapsed:hh\\:mm\\:ss}";
    }
}

/// <summary>
/// Centralized API key manager. Keys stored ONLY in environment variables.
/// Config files (provider_endpoints.md, appsettings.json) store endpoint/model only — never keys.
/// ⚠ Cache has no TTL — if an env var changes externally, SecretManager returns stale value
/// until Invalidate() is called.
/// <b>Consumers:</b> MultiProviderChatClient, EmbeddingClient (Get for LLM calls);
/// WebTools, IntegrationTools (Get for web/map APIs);
/// Cli/Program.cs, ConfigView (Set for key configuration);
/// Tests (CoreTests).
/// </summary>
public static class SecretManager
{
    private static readonly ConcurrentDictionary<string, (string? value, DateTime cached)> _cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>Read secret: cache (with TTL check) → Process env → User env → Machine env.</summary>
    public static string? Get(string envVar)
    {
        if (_cache.TryGetValue(envVar, out var entry) && (DateTime.UtcNow - entry.cached) < CacheTtl)
            return entry.value;

        var val = Environment.GetEnvironmentVariable(envVar, EnvironmentVariableTarget.Process)
               ?? Environment.GetEnvironmentVariable(envVar, EnvironmentVariableTarget.User)
               ?? Environment.GetEnvironmentVariable(envVar, EnvironmentVariableTarget.Machine);
        _cache[envVar] = (val, DateTime.UtcNow);
        return val;
    }

    /// <summary>Write secret to runtime cache + persist to User scope (Windows: encrypted registry).</summary>
    public static void Set(string envVar, string? value, bool persistent = false)
    {
        _cache[envVar] = (value, DateTime.UtcNow);
        Environment.SetEnvironmentVariable(envVar, value, EnvironmentVariableTarget.Process);
        if (persistent)
            try { Environment.SetEnvironmentVariable(envVar, value, EnvironmentVariableTarget.User); }
            catch { /* non-fatal on Linux if ~/.profile not writable */ }
    }

    /// <summary>Check if a secret is set and non-empty.</summary>
    public static bool Has(string envVar) => !string.IsNullOrEmpty(Get(envVar));

    /// <summary>Invalidate cache to force re-read from environment on next Get.</summary>
    public static void Invalidate(string envVar) => _cache.TryRemove(envVar, out _);
}
