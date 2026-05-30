using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LTAI.Core.Configuration;

public sealed class ProviderConfig
{
    public string Endpoint { get; set; } = "";
    public string Model { get; set; } = "";

    /// <summary>Read API key from environment variable (not from config file).</summary>
    public string? GetApiKey() =>
        SecretManager.Get(this.EnvVar);

    /// <summary>Set API key to environment variable (persisted to User scope).</summary>
    public void SetApiKey(string key) =>
        SecretManager.Set(this.EnvVar, key);

    /// <summary>The environment variable name for this provider's API key.</summary>
    [JsonIgnore]
    public string? EnvVar { get; set; }
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

/// <summary>
/// All environment variables the system uses, with descriptions.
/// Used by TUI/Desktop/CLI to show users what keys are available and what each is for.
/// Config files store endpoint/model only — keys are NEVER stored in config files.
/// </summary>
public static class KnownKeys
{
    public sealed record KeyInfo(string EnvVar, string Service, string Description,
        string? Url = null, string? Endpoint = null, string? Model = null,
        decimal PriceInPerM = 0, decimal PriceOutPerM = 0);

    /// <summary>All known keys. Source of truth for all UI panels.</summary>
    public static readonly KeyInfo[] All =
    [
        // ── LLM Providers (官方 ¥/1M tokens 价格，来源各官网定价页) ──
        new("DEEPSEEK_API_KEY",     "DeepSeek",       "¥0.5/¥2 per 1M",     "https://platform.deepseek.com/api_keys", "https://api.deepseek.com/v1", "deepseek-chat", 0.5m, 2.0m),
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

    /// <summary>Generate DefaultProviders format: (envVar, endpoint, model, name).</summary>
    public static (string envVar, string endpoint, string model, string name)[] GetDefaultProviders() =>
        All.Where(k => k.Endpoint != null && k.Model != null)
           .Select(k => (k.EnvVar, k.Endpoint!, k.Model!, k.Service))
           .ToArray();

    /// <summary>Get all keys grouped by service category.</summary>
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
/// Real-time token and cost tracker for all LLM calls.
/// Accumulates across the entire session. Thread-safe.
/// Uses per-provider pricing from <see cref="KnownKeys.All"/>.
/// </summary>
public static class UsageTracker
{
    private static long _promptTokens;
    private static long _completionTokens;
    private static double _totalCost;
    private static readonly object _costLock = new();
    private static long _requests;
    private static readonly Stopwatch _timer = Stopwatch.StartNew();

    /// <summary>Record token usage from an API call. Looks up pricing by model name.</summary>
    public static void Record(int prompt, int completion, string model = "")
    {
        Interlocked.Add(ref _promptTokens, prompt);
        Interlocked.Add(ref _completionTokens, completion);
        Interlocked.Increment(ref _requests);
        if (!string.IsNullOrEmpty(model)) _activeModel = model;

        // Look up provider pricing from KnownKeys by model name prefix match
        var key = KnownKeys.All.FirstOrDefault(k =>
            !string.IsNullOrEmpty(k.Model) && model.StartsWith(k.Model, StringComparison.OrdinalIgnoreCase));
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

    public static long PromptTokens => Interlocked.Read(ref _promptTokens);
    public static long CompletionTokens => Interlocked.Read(ref _completionTokens);
    public static long TotalTokens => PromptTokens + CompletionTokens;
    public static long Requests => Interlocked.Read(ref _requests);
    public static TimeSpan Uptime => _timer.Elapsed;

    /// <summary>Accumulated cost in RMB, calculated per-provider from KnownKeys pricing.</summary>
    public static decimal EstimatedCost { get { lock (_costLock) { return (decimal)_totalCost; } } }

    /// <summary>Cost estimate as string with ¥ symbol.</summary>
    public static string CostDisplay => $"¥{EstimatedCost:F4}";

    // ═══════════════════════════════════════════
    //  Context & cache tracking
    // ═══════════════════════════════════════════

    private static string _activeModel = "";
    private static long _cacheHits;
    private static long _cacheMisses;
    private static int _contextWindowSize = 64000;

    /// <summary>Currently active model name.</summary>
    public static string ActiveModel => string.IsNullOrEmpty(_activeModel) ? "N/A" : _activeModel;

    /// <summary>Set the active model name (called after each API call).</summary>
    public static void SetActiveModel(string model) => _activeModel = model;

    /// <summary>Cache hit/miss tracking.</summary>
    public static void RecordCacheHit() => Interlocked.Increment(ref _cacheHits);
    public static void RecordCacheMiss() => Interlocked.Increment(ref _cacheMisses);
    public static long CacheHits => Interlocked.Read(ref _cacheHits);
    public static long CacheMisses => Interlocked.Read(ref _cacheMisses);
    public static double CacheHitRate =>
        CacheHits + CacheMisses > 0 ? (double)CacheHits / (CacheHits + CacheMisses) * 100 : 0;

    /// <summary>Set the context window size (from config).</summary>
    public static void SetContextWindowSize(int size) => _contextWindowSize = size;

    /// <summary>Context usage as ratio 0.0-1.0 (for UI ProgressBar rendering).</summary>
    public static double ContextRatio(int contextWindowOverride = 0)
    {
        var maxTokens = contextWindowOverride > 0 ? contextWindowOverride : _contextWindowSize;
        if (maxTokens <= 0) return 0;
        var used = PromptTokens % (maxTokens + 1);
        return Math.Clamp((double)used / maxTokens, 0, 1);
    }

    /// <summary>Context usage text description.</summary>
    public static string ContextText(int contextWindowOverride = 0)
    {
        var maxTokens = contextWindowOverride > 0 ? contextWindowOverride : _contextWindowSize;
        if (maxTokens <= 0) return "";
        var used = PromptTokens % (maxTokens + 1);
        return $"{used:N0}/{maxTokens:N0} ({(double)used / maxTokens * 100:F0}%)";
    }

    // ═══════════════════════════════════════════
    //  Balance tracking
    // ═══════════════════════════════════════════

    private static double _balance;
    private static string _balanceCurrency = "";
    private static string _balanceSource = "";

    /// <summary>Account balance from provider (e.g. ¥12.34). Empty if unavailable.</summary>
    public static string BalanceDisplay =>
        string.IsNullOrEmpty(_balanceSource) ? "N/A"
        : $"{_balanceCurrency}{_balance:F2} ({_balanceSource})";

    /// <summary>Asynchronously fetch balance from the default provider's API.</summary>
    public static async Task FetchBalanceAsync(string defaultProvider, string? apiKey = null)
    {
        try
        {
            if (string.IsNullOrEmpty(apiKey)) return;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

            // Try known balance APIs based on provider name
            if (defaultProvider.Contains("siliconflow", StringComparison.OrdinalIgnoreCase))
            {
                http.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
                var resp = await http.GetStringAsync("https://api.siliconflow.cn/v1/user/balance");
                using var json = JsonDocument.Parse(resp);
                var bal = json.RootElement.GetProperty("balance").GetDouble();
                _balance = bal;
                _balanceCurrency = "¥";
                _balanceSource = "SiliconFlow";
            }
            else if (defaultProvider.Contains("deepseek", StringComparison.OrdinalIgnoreCase))
            {
                // DeepSeek has no public balance API — use estimated remaining from budget
                _balanceSource = "";
            }
            else if (defaultProvider.Contains("openrouter", StringComparison.OrdinalIgnoreCase))
            {
                http.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
                var resp = await http.GetStringAsync("https://openrouter.ai/api/v1/auth/key");
                using var json = JsonDocument.Parse(resp);
                var credits = json.RootElement.GetProperty("data").GetProperty("credits").GetDouble();
                _balance = credits;
                _balanceCurrency = "$";
                _balanceSource = "OpenRouter";
            }
            // Add more providers as their balance APIs become available
        }
        catch
        {
            // Balance fetch is best-effort; silently ignore failures
        }
    }

    public static string Summary()
    {
        var p = PromptTokens;
        var c = CompletionTokens;
        return $"Tokens: {p:N0}+{c:N0}={TotalTokens:N0} | "
             + $"Requests: {Requests} | "
             + $"Cost: ${EstimatedCost:F4} | "
             + $"Uptime: {Uptime:hh\\:mm\\:ss}";
    }
}

/// <summary>
/// Centralized API key manager. Keys stored ONLY in environment variables.
/// Config files (provider_endpoints.md, appsettings.json) store endpoint/model only — never keys.
/// </summary>
public static class SecretManager
{
    private static readonly ConcurrentDictionary<string, string?> _cache = new();

    /// <summary>Read secret: runtime cache → Process env → User env → Machine env.</summary>
    public static string? Get(string envVar) =>
        _cache.GetOrAdd(envVar, key =>
            Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.Process)
            ?? Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.User)
            ?? Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.Machine));

    /// <summary>Write secret to runtime cache + persist to User scope (Windows: encrypted registry).</summary>
    public static void Set(string envVar, string value, bool persistent = true)
    {
        _cache[envVar] = value;
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
