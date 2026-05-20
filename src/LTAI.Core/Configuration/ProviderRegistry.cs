using System.Collections.Concurrent;

namespace LTAI.Core.Configuration;

public interface IProviderRegistry
{
    string? GetBaseUrl(string provider);
    string? GetDefaultModel(string provider);
    List<string> GetCapabilities(string provider);
    List<ProviderTierVariant> GetTierVariants(string provider);
    ProviderConfig? CreateConfig(string provider, string apiKey);
    ProviderConfig? ResolveConfig(string provider, string model);
    void RegisterProvider(string name, string baseUrl, string defaultModel, List<string>? capabilities = null);
    IEnumerable<string> AllProviders { get; }
}

public sealed class ProviderRegistry : IProviderRegistry
{
    private readonly ConcurrentDictionary<string, string> _baseUrls;
    private readonly ConcurrentDictionary<string, string> _defaultModels;
    private readonly ConcurrentDictionary<string, List<string>> _capabilities;
    private readonly ConcurrentDictionary<string, List<ProviderTierVariant>> _tierVariants;

    private static readonly Dictionary<string, string> BuiltInBaseUrls = new()
    {
        ["deepseek"] = "https://api.deepseek.com/v1",
        ["longcat"] = "https://api.longcat.chat/v1",
        ["xiaomi"] = "https://api.xiaomimimo.com/v1",
        ["aliyun"] = "https://dashscope.aliyuncs.com/compatible-mode/v1",
        ["zhipu"] = "https://open.bigmodel.cn/api/paas/v4",
        ["hunyuan"] = "https://api.hunyuan.cloud.tencent.com/v1",
        ["baidu"] = "https://qianfan.baidubce.com/v2",
        ["spark"] = "https://maas-api.cn-huabei-1.xf-yun.com/v2",
        ["siliconflow"] = "https://api.siliconflow.cn/v1",
        ["mofang"] = "https://ai.gitee.com/v1",
        ["nvidia"] = "https://integrate.api.nvidia.com/v1",
        ["modelscope"] = "https://api-inference.modelscope.cn/v1",
        ["bailing"] = "https://api.baichuan.com/v1",
        ["stepfun"] = "https://api.stepfun.com/v1",
        ["internlm"] = "https://internlm-chat.intern-ai.org.cn/api/twlp/v1",
        ["sensetime"] = "https://api.sensetime.com/v1",
        ["openrouter"] = "https://openrouter.ai/api/v1",
        ["dmxapi"] = "https://www.dmxapi.cn/v1",
        ["ollama"] = "http://localhost:11434/v1",
        ["openai"] = "https://api.openai.com/v1",
        ["anthropic"] = "https://api.anthropic.com/v1",
        ["groq"] = "https://api.groq.com/openai/v1",
        ["moonshot"] = "https://api.moonshot.cn/v1",
        ["gemini"] = "https://generativelanguage.googleapis.com/v1beta",
        ["volcengine"] = "https://ark.cn-beijing.volces.com/api/v3/chat/completions",
        ["minimax"] = "https://api.minimax.chat/v1",
        ["kiro"] = "https://api.kiro.ai/v1",
        ["opencode"] = "https://opencode.ai/zen/v1"
    };

    private static readonly Dictionary<string, string> BuiltInDefaultModels = new()
    {
        ["deepseek"] = "deepseek-chat",
        ["longcat"] = "LongCat-Flash-Lite",
        ["xiaomi"] = "mimo-v2-flash",
        ["aliyun"] = "qwen-turbo",
        ["zhipu"] = "glm-4-flash",
        ["hunyuan"] = "hunyuan-lite",
        ["baidu"] = "ernie-speed-128k",
        ["spark"] = "xdeepseekv3",
        ["siliconflow"] = "Qwen/Qwen2.5-7B-Instruct",
        ["mofang"] = "Qwen/Qwen2.5-7B-Instruct",
        ["nvidia"] = "deepseek-ai/deepseek-r1",
        ["modelscope"] = "Qwen/Qwen3-8B",
        ["bailing"] = "Baichuan4-Turbo",
        ["stepfun"] = "step-1-flash",
        ["internlm"] = "internlm2.5-7b-chat",
        ["sensetime"] = "SenseChat-Turbo",
        ["openrouter"] = "deepseek/deepseek-v4-flash:free",
        ["dmxapi"] = "gpt-5-mini",
        ["ollama"] = "qwen3.5:4b",
        ["openai"] = "gpt-4o",
        ["anthropic"] = "claude-3-5-sonnet-20241022",
        ["groq"] = "llama-3.3-70b-versatile",
        ["moonshot"] = "moonshot-v1-128k",
        ["gemini"] = "gemini-2.0-flash",
        ["minimax"] = "abab6.5s-chat",
        ["kiro"] = "claude-sonnet-4.5",
        ["opencode"] = "claude-sonnet-4.5"
    };

    private static readonly Dictionary<string, List<ProviderTierVariant>> BuiltInTierVariants = new()
    {
        ["siliconflow"] = new()
        {
            new("siliconflow-flash", "Qwen/Qwen2.5-7B-Instruct"),
            new("siliconflow-reasoning", "deepseek-ai/DeepSeek-R1-Distill-Qwen-7B"),
            new("siliconflow-pro", "deepseek-ai/DeepSeek-V3"),
            new("siliconflow-small", "Qwen/Qwen2.5-1.5B-Instruct")
        },
        ["mofang"] = new()
        {
            new("mofang-flash", "Qwen/Qwen2.5-7B-Instruct"),
            new("mofang-reasoning", "deepseek-ai/DeepSeek-R1-Distill-Qwen-7B"),
            new("mofang-pro", "deepseek-ai/DeepSeek-V3"),
            new("mofang-small", "Qwen/Qwen2.5-1.5B-Instruct")
        },
        ["nvidia"] = new()
        {
            new("nvidia-reasoning", "deepseek-ai/deepseek-r1"),
            new("nvidia-pro", "nvidia/llama-3.1-nemotron-ultra-253b-v1"),
            new("nvidia-flash", "meta/llama-3.3-70b-instruct"),
            new("nvidia-small", "microsoft/phi-3.5-mini-instruct")
        }
    };

    private static readonly Dictionary<string, List<string>> BuiltInCapabilities = new()
    {
        ["deepseek"] = new() { "code", "reasoning", "analysis" },
        ["openai"] = new() { "multimodal", "code", "reasoning", "vision" },
        ["anthropic"] = new() { "long_context", "safety", "code", "reasoning" },
        ["aliyun"] = new() { "chinese", "code", "reasoning" },
        ["zhipu"] = new() { "agent", "tools", "chinese", "code" },
        ["moonshot"] = new() { "long_text", "chinese" },
        ["gemini"] = new() { "vision", "search", "multimodal" },
        ["groq"] = new() { "code", "reasoning", "fast" },
        ["ollama"] = new() { "code", "reasoning", "privacy", "local" },
        ["hunyuan"] = new() { "chinese", "search" },
        ["minimax"] = new() { "chinese", "long_context" },
        ["volcengine"] = new() { "chinese", "reasoning" },
        ["stepfun"] = new() { "chinese", "multimodal" },
        ["baidu"] = new() { "chinese", "search", "multimodal" },
        ["spark"] = new() { "chinese", "multimodal" },
        ["bailing"] = new() { "chinese", "search" },
        ["xiaomi"] = new() { "chinese", "fast" },
        ["nvidia"] = new() { "code", "reasoning", "multimodal" },
        ["siliconflow"] = new() { "code", "chinese", "fast" },
        ["modelscope"] = new() { "chinese", "code", "free" },
        ["internlm"] = new() { "chinese", "long_context" },
        ["sensetime"] = new() { "chinese", "free" },
        ["dmxapi"] = new() { "reasoning", "fast" },
        ["mofang"] = new() { "chinese", "code", "free" },
        ["openrouter"] = new() { "multimodal", "code", "reasoning" },
        ["longcat"] = new() { "reasoning", "long_context" }
    };

    public ProviderRegistry()
    {
        _baseUrls = new ConcurrentDictionary<string, string>(BuiltInBaseUrls);
        _defaultModels = new ConcurrentDictionary<string, string>(BuiltInDefaultModels);
        _capabilities = new ConcurrentDictionary<string, List<string>>(
            BuiltInCapabilities.ToDictionary(kvp => kvp.Key, kvp => new List<string>(kvp.Value)));
        _tierVariants = new ConcurrentDictionary<string, List<ProviderTierVariant>>(
            BuiltInTierVariants.ToDictionary(kvp => kvp.Key, kvp => kvp.Value));
    }

    public string? GetBaseUrl(string provider) =>
        _baseUrls.TryGetValue(provider, out var url) ? url : null;

    public string? GetDefaultModel(string provider) =>
        _defaultModels.TryGetValue(provider, out var model) ? model : null;

    public List<string> GetCapabilities(string provider) =>
        _capabilities.TryGetValue(provider, out var caps) ? caps : new List<string>();

    public List<ProviderTierVariant> GetTierVariants(string provider) =>
        _tierVariants.TryGetValue(provider, out var variants) ? variants : new List<ProviderTierVariant>();

    public IEnumerable<string> AllProviders => _baseUrls.Keys;

    public void RegisterProvider(string name, string baseUrl, string defaultModel, List<string>? capabilities = null)
    {
        _baseUrls[name] = baseUrl;
        _defaultModels[name] = defaultModel;
        if (capabilities != null)
            _capabilities[name] = capabilities;
    }

    public ProviderConfig? CreateConfig(string provider, string apiKey)
    {
        var endpoint = GetBaseUrl(provider);
        if (endpoint == null) return null;

        return new ProviderConfig
        {
            Endpoint = endpoint,
            ApiKey = apiKey,
            Model = GetDefaultModel(provider) ?? ""
        };
    }

    public ProviderConfig? ResolveConfig(string provider, string model)
    {
        var endpoint = GetBaseUrl(provider);
        if (endpoint == null) return null;

        return new ProviderConfig
        {
            Endpoint = endpoint,
            ApiKey = string.Empty,
            Model = model
        };
    }

    public static string? DefaultProviderModel(string provider)
    {
        return BuiltInDefaultModels.TryGetValue(provider, out var model) ? model : null;
    }

    public static ProviderConfig? ResolveConfig(string provider, string model, string apiKey)
    {
        if (!BuiltInBaseUrls.TryGetValue(provider, out var endpoint))
            return null;

        return new ProviderConfig
        {
            Endpoint = endpoint,
            ApiKey = apiKey,
            Model = model
        };
    }
}

public sealed record ProviderTierVariant(string Name, string DefaultModel);

public static class ProviderRegistryExtensions
{
    public static ProviderConfig? ResolveLayer(
        this IProviderRegistry registry,
        AIConfig aiConfig,
        string layer)
    {
        var lc = aiConfig.GetLayerConfig(layer);
        if (!lc.IsConfigured) return null;

        if (aiConfig.Providers.TryGetValue(lc.Provider, out var configuredProvider))
        {
            return new ProviderConfig
            {
                Endpoint = configuredProvider.Endpoint,
                ApiKey = configuredProvider.ApiKey,
                Model = lc.Model
            };
        }

        return registry.ResolveConfig(lc.Provider, lc.Model);
    }
}
