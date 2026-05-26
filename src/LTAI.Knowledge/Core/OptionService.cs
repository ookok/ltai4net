using System.Collections.Concurrent;
using LTAI.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LTAI.Knowledge.Core;

public sealed class OptionService
{
    private static OptionService? _instance;
    public static OptionService Instance => _instance ?? throw new InvalidOperationException("OptionService not initialized. Call SetInstance first.");

    public static void SetInstance(OptionService svc) => _instance = svc;

    public static string? Get(string envVar, string? fallback = null)
    {
        if (_instance == null) return Environment.GetEnvironmentVariable(envVar) ?? fallback;
        var value = _instance.GetEnv(envVar);
        return value ?? fallback;
    }

    public static string GetOrThrow(string envVar)
    {
        if (_instance == null)
        {
            var v = Environment.GetEnvironmentVariable(envVar);
            if (!string.IsNullOrWhiteSpace(v)) return v;
            throw new InvalidOperationException($"Required environment variable {envVar} is not set.");
        }
        var value = _instance.GetEnv(envVar);
        if (!string.IsNullOrWhiteSpace(value)) return value;
        throw new InvalidOperationException($"Required configuration {envVar} is not set in config/*.md or environment.");
    }

    private readonly OptionLoader _loader;
    private readonly LTAI.Core.Configuration.LTAIOptions _defaults;
    private readonly IConfiguration? _configuration;
    private readonly ILogger<OptionService> _logger;
    private readonly string _configRoot;

    private readonly ConcurrentDictionary<string, OptionFile> _sections = new();
    private readonly ConcurrentDictionary<string, string> _resolved = new();
    private readonly ConcurrentDictionary<string, string> _envCache = new();
    private bool _loaded;

    public IReadOnlyDictionary<string, OptionFile> Sections => _sections;
    public bool IsLoaded => _loaded;
    public string ConfigRoot => _configRoot;

    public OptionService(
        OptionLoader loader,
        LTAI.Core.Configuration.LTAIOptions defaults,
        IConfiguration? configuration,
        ILogger<OptionService> logger,
        string? configRoot = null)
    {
        _loader = loader;
        _defaults = defaults;
        _configuration = configuration;
        _logger = logger;
        _configRoot = configRoot ?? Path.Combine(AppContext.BaseDirectory, "config");
    }

    public async Task LoadAllAsync(CancellationToken ct = default)
    {
        if (_loaded) return;
        if (!Directory.Exists(_configRoot))
        {
            Directory.CreateDirectory(_configRoot);
            await SeedDefaultsAsync(ct).ConfigureAwait(false);
        }

        var files = Directory.GetFiles(_configRoot, "*.md", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            var option = await _loader.LoadAsync(file, ct).ConfigureAwait(false);
            if (option != null)
                _sections[option.Name] = option;
        }

        foreach (var (name, section) in _sections)
        {
            foreach (var key in section.Keys)
            {
                var fullPath = string.IsNullOrEmpty(section.Section)
                    ? key.Name
                    : $"{section.Section}:{key.Name}";
                var value = ResolveValue(key);
                if (value != null)
                    _resolved[fullPath] = value;
            }
        }

        _loaded = true;
        SetInstance(this);
        LTAI.Core.Configuration.SecretVault.EnvResolver = key => Get(key);
        LTAI.Tools.Tools.OptionService.SetResolver((envVar, _) => Get(envVar));
        _logger.LogInformation("OptionService loaded {Count} config sections with {KeyCount} keys from {Dir}",
            _sections.Count, _sections.Sum(s => s.Value.Keys.Count), _configRoot);
    }

    public string? Resolve(string sectionName, string keyName)
    {
        var fullPath = $"{sectionName}:{keyName}";
        if (_resolved.TryGetValue(fullPath, out var value))
            return value;

        var key = FindKey(sectionName, keyName);
        if (key != null)
        {
            value = ResolveValue(key);
            if (value != null)
                _resolved[fullPath] = value;
        }
        return value;
    }

    public string? GetEnv(string envVar)
    {
        if (_envCache.TryGetValue(envVar, out var cached))
            return string.IsNullOrEmpty(cached) ? null : cached;

        var value = Environment.GetEnvironmentVariable(envVar);
        _envCache[envVar] = value ?? "";
        return value;
    }

    public string? GetEnvOrDefault(string envVar, string? fallback = null)
    {
        var value = GetEnv(envVar);
        return value ?? fallback;
    }

    public T? BindValue<T>(string sectionName, string keyName) where T : struct
    {
        var value = Resolve(sectionName, keyName);
        if (value == null) return null;
        try { return (T)Convert.ChangeType(value, typeof(T)); }
        catch { return null; }
    }

    public string? GetConfigValue(IConfigurationSection section, string key)
    {
        var configValue = section[key];
        return string.IsNullOrEmpty(configValue) ? null : configValue;
    }

    public void Register(string sectionName, OptionFile file)
    {
        _sections[sectionName] = file;
        foreach (var key in file.Keys)
        {
            var value = ResolveValue(key);
            if (value != null)
            {
                var fullPath = string.IsNullOrEmpty(file.Section)
                    ? key.Name
                    : $"{file.Section}:{key.Name}";
                _resolved[fullPath] = value;
            }
        }
    }

    public async Task<OptionFile> CreateAndSaveAsync(
        string name, string section, string description,
        List<OptionKey> keys, List<string>? tags = null,
        CancellationToken ct = default)
    {
        var file = new OptionFile
        {
            Name = name,
            Section = section,
            Description = description,
            Keys = keys,
            Tags = tags ?? new List<string>()
        };
        await _loader.SaveAsync(file, _configRoot, ct).ConfigureAwait(false);
        Register(name, file);
        return file;
    }

    public List<OptionFile> FindByTag(string tag) =>
        _sections.Values.Where(s => s.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)).ToList();

    public List<OptionKey> SearchKeys(string query) =>
        _sections.Values
            .SelectMany(s => s.Keys)
            .Where(k => k.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || (k.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();

    private OptionKey? FindKey(string sectionName, string keyName)
    {
        if (!_sections.TryGetValue(sectionName, out var section)) return null;
        return section.Keys.FirstOrDefault(k =>
            k.Name.Equals(keyName, StringComparison.OrdinalIgnoreCase));
    }

    private string? ResolveValue(OptionKey key)
    {
        if (key.EnvVar != null)
        {
            var env = Environment.GetEnvironmentVariable(key.EnvVar);
            if (!string.IsNullOrWhiteSpace(env)) return env;
        }

        if (_configuration != null)
        {
            var configValue = _configuration.GetSection(key.Name).Value;
            if (!string.IsNullOrWhiteSpace(configValue))
                return OptionLoader.ExpandVariables(configValue);
        }

        return key.Default != null ? OptionLoader.ExpandVariables(key.Default) : null;
    }

    private async Task SeedDefaultsAsync(CancellationToken ct)
    {
        var defaults = new List<OptionFile>
        {
            new()
            {
                Name = "provider_endpoints", Section = "LTAI:AI:Providers",
                Description = "AI provider endpoint URLs and default model names. API keys are read from env vars, not stored here.",
                Tags = new() { "provider", "endpoint", "model" },
                Keys = new()
                {
                    new() { Name = "deepseek.endpoint", Default = "https://api.deepseek.com", EnvVar = "DEEPSEEK_ENDPOINT", Type = "string", Description = "DeepSeek API base URL" },
                    new() { Name = "deepseek.model", Default = "deepseek-v4-pro", EnvVar = "DEEPSEEK_MODEL", Type = "string", Description = "Default model" },
                    new() { Name = "deepseek.fast.endpoint", Default = "https://api.deepseek.com", EnvVar = "DEEPSEEK_FAST_ENDPOINT", Type = "string", Description = "Fast-tier endpoint" },
                    new() { Name = "deepseek.fast.model", Default = "deepseek-v4-flash", EnvVar = "DEEPSEEK_FAST_MODEL", Type = "string", Description = "Fast-tier model" },
                    new() { Name = "openai.endpoint", Default = "https://api.openai.com/v1", EnvVar = "OPENAI_ENDPOINT", Type = "string", Description = "OpenAI API base URL" },
                    new() { Name = "openai.model", Default = "gpt-4o", EnvVar = "OPENAI_MODEL", Type = "string", Description = "Default model" },
                    new() { Name = "anthropic.endpoint", Default = "https://api.anthropic.com/v1", EnvVar = "ANTHROPIC_ENDPOINT", Type = "string", Description = "Anthropic API base URL" },
                    new() { Name = "anthropic.model", Default = "claude-sonnet-4-20250514", EnvVar = "ANTHROPIC_MODEL", Type = "string", Description = "Default model" },
                    new() { Name = "dashscope.endpoint", Default = "https://dashscope.aliyuncs.com/compatible-mode/v1", EnvVar = "DASHSCOPE_ENDPOINT", Type = "string", Description = "Alibaba DashScope API" },
                    new() { Name = "dashscope.model", Default = "qwen-max", EnvVar = "DASHSCOPE_MODEL", Type = "string", Description = "Default model" },
                    new() { Name = "siliconflow.endpoint", Default = "https://api.siliconflow.cn/v1", EnvVar = "SILICONFLOW_ENDPOINT", Type = "string", Description = "SiliconFlow API" },
                    new() { Name = "siliconflow.model", Default = "deepseek-ai/DeepSeek-V3", EnvVar = "SILICONFLOW_MODEL", Type = "string", Description = "Default model" },
                    new() { Name = "groq.endpoint", Default = "https://api.groq.com/openai/v1", EnvVar = "GROQ_ENDPOINT", Type = "string", Description = "Groq API" },
                    new() { Name = "groq.model", Default = "llama-4-maverick-128k-instruct", EnvVar = "GROQ_MODEL", Type = "string", Description = "Default model" },
                    new() { Name = "openrouter.endpoint", Default = "https://openrouter.ai/api/v1", EnvVar = "OPENROUTER_ENDPOINT", Type = "string", Description = "OpenRouter API" },
                    new() { Name = "openrouter.model", Default = "openai/gpt-4o", EnvVar = "OPENROUTER_MODEL", Type = "string", Description = "Default model" },
                    new() { Name = "ollama.endpoint", Default = "http://localhost:11434/v1", EnvVar = "OLLAMA_ENDPOINT", Type = "string", Description = "Ollama local API" },
                    new() { Name = "ollama.model", Default = "qwen3", EnvVar = "OLLAMA_MODEL", Type = "string", Description = "Default model" },
                    new() { Name = "azure.endpoint", Default = "", EnvVar = "AZURE_AI_ENDPOINT", Type = "string", Description = "Azure OpenAI endpoint" },
                    new() { Name = "azure.model", Default = "gpt-4o", EnvVar = "AZURE_AI_MODEL", Type = "string", Description = "Default model" },
                    new() { Name = "kiro.endpoint", Default = "https://api.kiro.cn/v1", EnvVar = "KIRO_ENDPOINT", Type = "string", Description = "Kiro API" },
                    new() { Name = "kiro.model", Default = "kiro-latest", EnvVar = "KIRO_MODEL", Type = "string", Description = "Default model" },
                    new() { Name = "xiaomi.endpoint", Default = "https://api.xiaomi-ai.com/v1", EnvVar = "XIAOMI_ENDPOINT", Type = "string", Description = "Xiaomi API" },
                    new() { Name = "xiaomi.model", Default = "mi-ai-large", EnvVar = "XIAOMI_MODEL", Type = "string", Description = "Default model" },
                    new() { Name = "stepfun.endpoint", Default = "https://api.stepfun.com/v1", EnvVar = "STEPFUN_ENDPOINT", Type = "string", Description = "StepFun API" },
                    new() { Name = "stepfun.model", Default = "step-2-16k", EnvVar = "STEPFUN_MODEL", Type = "string", Description = "Default model" },
                    new() { Name = "internlm.endpoint", Default = "https://api.internlm.com/v1", EnvVar = "INTERNLM_ENDPOINT", Type = "string", Description = "InternLM API" },
                    new() { Name = "internlm.model", Default = "internlm3-8b", EnvVar = "INTERNLM_MODEL", Type = "string", Description = "Default model" },
                    new() { Name = "sensetime.endpoint", Default = "https://api.sensetime.com/v1", EnvVar = "SENSETIME_ENDPOINT", Type = "string", Description = "SenseTime API" },
                    new() { Name = "sensetime.model", Default = "sensechat-5", EnvVar = "SENSETIME_MODEL", Type = "string", Description = "Default model" },
                    new() { Name = "longcat.endpoint", Default = "https://api.longcat.ai/v1", EnvVar = "LONGCAT_ENDPOINT", Type = "string", Description = "LongCat API" },
                    new() { Name = "longcat.model", Default = "longcat-flash", EnvVar = "LONGCAT_MODEL", Type = "string", Description = "Default model" },
                    new() { Name = "dmxapi.endpoint", Default = "https://api.dmxapi.com/v1", EnvVar = "DMXAPI_ENDPOINT", Type = "string", Description = "DMXAPI" },
                    new() { Name = "dmxapi.model", Default = "gpt-4o", EnvVar = "DMXAPI_MODEL", Type = "string", Description = "Default model" },
                    new() { Name = "xai.endpoint", Default = "https://api.x.ai/v1", EnvVar = "XAI_ENDPOINT", Type = "string", Description = "xAI (Grok) API" },
                    new() { Name = "xai.model", Default = "grok-3", EnvVar = "XAI_MODEL", Type = "string", Description = "Default model" },
                    new() { Name = "opencode.endpoint", Default = "https://api.opencode.ai/v1", EnvVar = "OPENCODE_ENDPOINT", Type = "string", Description = "OpenCode API" },
                    new() { Name = "opencode.model", Default = "deepseek-v4-pro", EnvVar = "OPENCODE_MODEL", Type = "string", Description = "Default model" },
                    new() { Name = "kunlun.endpoint", Default = "https://api.skylark.cn/v1", EnvVar = "KUNLUN_ENDPOINT", Type = "string", Description = "Kunlun Skylark API" },
                    new() { Name = "kunlun.model", Default = "skylark-4", EnvVar = "KUNLUN_MODEL", Type = "string", Description = "Default model" },
                }
            },
            new()
            {
                Name = "model_routing", Section = "LTAI:AI:Routing",
                Description = "Model tier assignments and degradation chain. L0=local, L1=fast cloud, L2=deep cloud.",
                Tags = new() { "model", "routing", "tier" },
                Keys = new()
                {
                    new() { Name = "routing.l0.provider", Default = "onnx", EnvVar = "LTAI_L0_PROVIDER", Type = "string", Description = "L0 provider (local inference)" },
                    new() { Name = "routing.l0.model", Default = "model.onnx", EnvVar = "LTAI_L0_MODEL", Type = "string", Description = "L0 model file/ID" },
                    new() { Name = "routing.l1.provider", Default = "deepseek-fast", EnvVar = "LTAI_L1_PROVIDER", Type = "string", Description = "L1 fast cloud provider" },
                    new() { Name = "routing.l1.model", Default = "deepseek-v4-flash", EnvVar = "LTAI_L1_MODEL", Type = "string", Description = "L1 fast model" },
                    new() { Name = "routing.l2.provider", Default = "deepseek", EnvVar = "LTAI_L2_PROVIDER", Type = "string", Description = "L2 deep cloud provider" },
                    new() { Name = "routing.l2.model", Default = "deepseek-v4-pro", EnvVar = "LTAI_L2_MODEL", Type = "string", Description = "L2 deep model" },
                    new() { Name = "routing.default.provider", Default = "deepseek", EnvVar = "LTAI_DEFAULT_PROVIDER", Type = "string", Description = "Default provider" },
                    new() { Name = "routing.max_tokens", Default = "4096", EnvVar = "LTAI_MAX_TOKENS", Type = "int", Description = "Max response tokens" },
                    new() { Name = "routing.temperature", Default = "0.3", EnvVar = "LTAI_TEMPERATURE", Type = "float" },
                    new() { Name = "routing.daily_budget_usd", Default = "10.00", EnvVar = "LTAI_DAILY_BUDGET", Type = "decimal" },
                    new() { Name = "routing.circuit_breaker_failures", Default = "5", EnvVar = "LTAI_CIRCUIT_BREAKER", Type = "int" },
                    new() { Name = "routing.circuit_cooldown_sec", Default = "30", EnvVar = "LTAI_CIRCUIT_COOLDOWN", Type = "int" },
                    new() { Name = "routing.timeout_ms", Default = "60000", EnvVar = "LTAI_TIMEOUT_MS", Type = "int" },
                }
            },
            new()
            {
                Name = "paths", Section = "",
                Description = "Directory path configuration. Root is AppContext.BaseDirectory. Edit to relocate skills/tools/prompts/memory/models directories.",
                Tags = new() { "paths", "directories" },
                Keys = new()
                {
                    new() { Name = "DataDirectory", Default = ".livingtree", Type = "string", Description = "Data and DB root" },
                    new() { Name = "SkillsDirectory", Default = "skills", Type = "string", Description = "User skills directory" },
                    new() { Name = "ToolsDirectory", Default = "tools", Type = "string", Description = "MD tool definitions" },
                    new() { Name = "PromptsDirectory", Default = "prompts", Type = "string", Description = "Prompt templates" },
                    new() { Name = "MemoryDirectory", Default = "memory", Type = "string", Description = "Memory files" },
                    new() { Name = "ModelsDirectory", Default = "models", Type = "string", Description = "Local model files" },
                    new() { Name = "LogsDirectory", Default = "logs", Type = "string", Description = "Log output" },
                    new() { Name = "ConfigDirectory", Default = "config", Type = "string", Description = "Config .md files" },
                    new() { Name = "OutputDirectory", Default = "output", Type = "string", Description = "Generated output" },
                    new() { Name = "AssetsDirectory", Default = "assets", Type = "string", Description = "Static assets" },
                    new() { Name = "WorkspaceRoot", EnvVar = "LTAI_WORKSPACE", Type = "string", Description = "Workspace root (fallback: CWD)" },
                }
            },
            new()
            {
                Name = "web_config", Section = "LTAI:Web",
                Description = "Web server host, port, and rate limiting configuration",
                Tags = new() { "web", "hosting" },
                Keys = new()
                {
                    new() { Name = "LTAI:Web:Host", Default = "0.0.0.0", Type = "string", Description = "Listen host" },
                    new() { Name = "LTAI:Web:Port", Default = "8080", Type = "int", Description = "Listen port" },
                    new() { Name = "LTAI:Web:RateLimitPerMinute", Default = "60", Type = "int", Description = "API rate limit" },
                }
            },
        };

        foreach (var option in defaults)
            await _loader.SaveAsync(option, _configRoot, ct).ConfigureAwait(false);

        _logger.LogInformation("Seeded {Count} default config sections to {Dir} — browse config/*.md to see all settings",
            defaults.Count, _configRoot);
    }
}
