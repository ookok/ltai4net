using System.Text.Json;

namespace LTAI.Core.Configuration;

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

    /// <summary>Hardcoded defaults — used when <c>LTAI:Providers</c> config is empty.</summary>
    private static readonly KeyInfo[] DefaultHardcoded =
    [
        // ── LLM Providers (官方 ¥/1M tokens 价格，来源各官网定价页) ──
        new("DEEPSEEK_API_KEY",     "DeepSeek",       "输入¥1/输出¥2/缓存¥0.02 per 1M", "https://platform.deepseek.com/api_keys", "https://api.deepseek.com/v1", null, 1.0m, 2.0m, 0.02m),
        new("SILICONFLOW_API_KEY",  "SiliconFlow",    "¥1/¥2 per 1M",       "https://cloud.siliconflow.cn/", "https://api.siliconflow.cn/v1", null, 1.0m, 2.0m),
        new("DASHSCOPE_API_KEY",    "Aliyun(Qwen)",   "¥0.8/¥2 per 1M",     "https://dashscope.console.aliyun.com/", "https://dashscope.aliyuncs.com/compatible-mode/v1", null, 0.8m, 2.0m),
        new("ZHIPU_API_KEY",        "Zhipu(GLM)",     "¥1/¥2 per 1M",       "https://open.bigmodel.cn/", "https://open.bigmodel.cn/api/paas/v4", null, 1.0m, 2.0m),
        new("STEP_API_KEY",         "StepFun",        "¥1/¥2 per 1M",       "https://platform.stepfun.com/", "https://api.stepfun.com/v1", null, 1.0m, 2.0m),
        new("OPENROUTER_API_KEY",   "OpenRouter",     "按源模型定价",        "https://openrouter.ai/keys", "https://openrouter.ai/api/v1", null),
        new("MIMO_API_KEY",         "小米 MiMo",     "输入¥1/输出¥2 per 1M", "https://dev.mi.com/", "https://api.xiaomimimo.com/v1", null, 1.0m, 2.0m),
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
        // ── Memory ──
        new("MEM0_API_KEY",         "Mem0",           "跨会话长期记忆",    "https://app.mem0.ai/", "https://api.mem0.ai"),
        // ── Steer Model (可选项，用于判断/安全/路由等辅助决策) ──
        new("STEER_API_KEY",        "Steer Model",   "辅助决策模型（可选项）", null, "https://api.siliconflow.cn/v1", null),
    ];

    /// <summary>
    /// All known keys. Source of truth for UI panels and cost calculation.
    /// Can be overridden by setting <c>LTAI:Providers</c> in appsettings.json.
    /// Call <see cref="ApplyConfig"/> at startup if config providers are present.
    /// <b>Consumers:</b> ConfigView, MainWindow, LLMConfigPanel, UsageTracker.
    /// Thread safety: reads are lock-free via volatile; writes use atomic swap.
    /// </summary>
    public static volatile KeyInfo[] All = DefaultHardcoded;

    /// <summary>
    /// Generate default provider configurations in tuple format.
    /// Filters to providers that have both an endpoint and a model defined.
    /// <b>Callers:</b> MultiProviderChatClient (initialize default providers).
    /// </summary>
    public static (string envVar, string endpoint, string model, string name)[] GetDefaultProviders()
    {
        var snapshot = All;
        return snapshot.Where(k => k.Endpoint != null && k.Model != null)
           .Select(k => (k.EnvVar, k.Endpoint!, k.Model!, k.Service))
           .ToArray();
    }

    /// <summary>
    /// Get all keys grouped by service category for UI display.
    /// Categories: "LLM Providers", "Map / GIS", "Web Search", "Weather", "Translation", "Image", "Other".
    /// <b>Consumers:</b> ConfigView (category tabs).
    /// </summary>
    public static ILookup<string, KeyInfo> ByCategory
    {
        get
        {
            var snapshot = All;
            return snapshot.ToLookup(k => k.Service.Contains("API") ? "LLM Providers"
                          : k.EnvVar.Contains("MAP") || k.EnvVar.Contains("TIANDITU") ? "Map / GIS"
                          : k.EnvVar.Contains("BRAVE") || k.EnvVar.Contains("SERPER") ? "Web Search"
                          : k.EnvVar.Contains("WEATHER") ? "Weather"
                          : k.EnvVar.Contains("TRANSLATE") ? "Translation"
                          : k.EnvVar.Contains("UNSPLASH") ? "Image"
                          : "Other");
        }
    }

    /// <summary>
    /// Override <see cref="All"/> with entries from <c>LTAI:Providers</c> config.
    /// Call once at startup from DI registration when config is available.
    /// Entries with the same <c>EnvVar</c> replace hardcoded defaults; new entries append.
    /// </summary>
    public static void ApplyConfig(ProviderDefinition[] providers)
    {
        if (providers == null || providers.Length == 0) return;
        var merged = new List<KeyInfo>(DefaultHardcoded);
        var seen = new HashSet<string>(merged.Select(k => k.EnvVar), StringComparer.OrdinalIgnoreCase);
        foreach (var p in providers)
        {
            var ki = new KeyInfo(p.EnvVar, p.Service, p.Description, p.Url, p.Endpoint, p.Model,
                p.PriceInPerM, p.PriceOutPerM, p.PriceInCachePerM);
            var idx = merged.FindIndex(k => k.EnvVar.Equals(p.EnvVar, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
                merged[idx] = ki;
            else
                merged.Add(ki);
        }
        All = merged.ToArray();
    }

    /// <summary>
    /// Update the default model for a specific provider (by Service name or EnvVar).
    /// This mutates <see cref="All"/> in memory. Does NOT persist to disk.
    /// Returns true if the provider was found and updated.
    /// </summary>
    public static bool UpdateProviderModel(string serviceOrEnvVar, string newModel)
    {
        var snapshot = All;
        for (int i = 0; i < snapshot.Length; i++)
        {
            if (snapshot[i].Service.Equals(serviceOrEnvVar, StringComparison.OrdinalIgnoreCase) ||
                snapshot[i].EnvVar.Equals(serviceOrEnvVar, StringComparison.OrdinalIgnoreCase))
            {
                var copy = (KeyInfo[])snapshot.Clone();
                copy[i] = copy[i] with { Model = newModel };
                All = copy;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Save current provider models to an appsettings.json file.
    /// Writes a <c>LTAI:Providers</c> section with current endpoint + model for
    /// each provider that has both defined. File is created or overwritten atomically.
    /// Returns the file path written to.
    /// </summary>
    public static string SaveProviderModels(string configPath)
    {
        var providers = All
            .Where(k => k.Endpoint != null && k.Model != null)
            .Select(k => new ProviderDefinition(
                k.EnvVar,
                k.Service,
                k.Description ?? "",
                k.Url,
                k.Endpoint!,
                k.Model!,
                k.PriceInPerM,
                k.PriceOutPerM,
                k.PriceInCachePerM
            ))
            .ToArray();

        var wrapper = new { LTAI = new { Providers = providers } };
        var json = JsonSerializer.Serialize(wrapper,
            new JsonSerializerOptions { WriteIndented = true });
        var dir = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(configPath, json);
        return configPath;
    }
}
