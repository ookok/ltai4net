using System.Text.Json;
using System.Text.Json.Serialization;

namespace LTAI.Core.Configuration;

public sealed class SecretVault
{
    private static readonly Lazy<SecretVault> _instance = new(() => new SecretVault());
    public static SecretVault Instance => _instance.Value;

    private readonly Dictionary<string, string> _cache = new();
    private readonly Dictionary<string, string> _envMap = new();
    private readonly object _lock = new();

    private SecretVault()
    {
        InitializeEnvMap();
    }

    public string Get(string key, string defaultValue = "")
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var cached) && !string.IsNullOrEmpty(cached))
                return cached;

            if (_envMap.TryGetValue(key, out var envVar))
            {
                var envValue = Environment.GetEnvironmentVariable(envVar);
                if (!string.IsNullOrEmpty(envValue))
                {
                    _cache[key] = envValue;
                    return envValue;
                }
            }

            return defaultValue;
        }
    }

    public bool TryGet(string key, out string value)
    {
        var result = Get(key);
        value = result;
        return !string.IsNullOrEmpty(result);
    }

    public void Set(string key, string value)
    {
        lock (_lock) { _cache[key] = value; }
    }

    public Dictionary<string, string> GetAll()
    {
        lock (_lock)
        {
            var result = new Dictionary<string, string>();
            foreach (var (key, envVar) in _envMap)
            {
                var envValue = Environment.GetEnvironmentVariable(envVar);
                if (!string.IsNullOrEmpty(envValue))
                    result[key] = envValue;
            }
            foreach (var (key, value) in _cache)
                result[key] = value;
            return result;
        }
    }

    public string ExportToJson()
    {
        lock (_lock)
        {
            var all = GetAll();
            var export = new SecretsExport
            {
                Version = 1,
                ExportedAt = DateTime.UtcNow,
                Secrets = all
            };
            return JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true });
        }
    }

    public static void LoadFromJsonFile(string filePath)
    {
        if (!File.Exists(filePath)) return;

        try
        {
            var json = File.ReadAllText(filePath);
            var export = JsonSerializer.Deserialize<SecretsExport>(json);
            if (export?.Secrets == null) return;

            foreach (var (key, value) in export.Secrets)
            {
                if (string.IsNullOrEmpty(value)) continue;
                var envName = key.ToUpperInvariant();
                try { Environment.SetEnvironmentVariable(envName, value, EnvironmentVariableTarget.User); }
                catch { Environment.SetEnvironmentVariable(envName, value, EnvironmentVariableTarget.Process); }
            }

            Instance.ImportFromJson(json, out _);
        }
        catch { }
    }

    public bool ImportFromJson(string json, out string error)
    {
        error = "";
        try
        {
            var export = JsonSerializer.Deserialize<SecretsExport>(json);
            if (export?.Secrets == null) { error = "Invalid format"; return false; }

            lock (_lock)
            {
                foreach (var (key, value) in export.Secrets)
                    if (!string.IsNullOrEmpty(value))
                        _cache[key] = value;
            }
            return true;
        }
        catch (JsonException ex) { error = ex.Message; return false; }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    public Dictionary<string, object> GetStats()
    {
        lock (_lock)
        {
            var envCount = _envMap.Keys.Count(k => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(_envMap[k])));
            return new Dictionary<string, object>
            {
                ["memory_secrets"] = _cache.Count,
                ["env_secrets"] = envCount,
                ["total_keys"] = _envMap.Count + _cache.Keys.Count(k => !_envMap.ContainsKey(k))
            };
        }
    }

    private void InitializeEnvMap()
    {
        _envMap["deepseek_api_key"] = "DEEPSEEK_API_KEY";
        _envMap["openai_api_key"] = "OPENAI_API_KEY";
        _envMap["anthropic_api_key"] = "ANTHROPIC_API_KEY";
        _envMap["siliconflow_api_key"] = "SILICONFLOW_API_KEY";
        _envMap["mofang_api_key"] = "MOFANG_API_KEY";
        _envMap["nvidia_api_key"] = "NVIDIA_API_KEY";
        _envMap["aliyun_api_key"] = "DASHSCOPE_API_KEY";
        _envMap["zhipu_api_key"] = "ZHIPU_API_KEY";
        _envMap["hunyuan_api_key"] = "HUNYUAN_API_KEY";
        _envMap["baidu_api_key"] = "BAIDU_API_KEY";
        _envMap["spark_api_key"] = "SPARK_API_KEY";
        _envMap["bailing_api_key"] = "BAILING_API_KEY";
        _envMap["stepfun_api_key"] = "STEPFUN_API_KEY";
        _envMap["internlm_api_key"] = "INTERNLM_API_KEY";
        _envMap["sensetime_api_key"] = "SENSETIME_API_KEY";
        _envMap["modelscope_api_key"] = "MODELSCOPE_API_KEY";
        _envMap["openrouter_api_key"] = "OPENROUTER_API_KEY";
        _envMap["xiaomi_api_key"] = "XIAOMI_API_KEY";
        _envMap["longcat_api_key"] = "LONGCAT_API_KEY";
        _envMap["dmxapi_api_key"] = "DMXAPI_API_KEY";
        _envMap["volcengine_api_key"] = "VOLCENGINE_API_KEY";
        _envMap["moonshot_api_key"] = "MOONSHOT_API_KEY";
        _envMap["gemini_api_key"] = "GEMINI_API_KEY";
        _envMap["minimax_api_key"] = "MINIMAX_API_KEY";
        _envMap["groq_api_key"] = "GROQ_API_KEY";
        _envMap["kiro_api_key"] = "KIRO_API_KEY";
        _envMap["opencode_api_key"] = "OPENCODE_API_KEY";
        _envMap["tianditu_key"] = "TIANDITU_KEY";
        _envMap["baidu_map_ak"] = "BAIDU_MAP_AK";
        _envMap["baidu_map_sk"] = "BAIDU_MAP_SK";
        _envMap["tencent_map_key"] = "TENCENT_MAP_KEY";
        _envMap["amap_key"] = "AMAP_KEY";
        _envMap["openweathermap_api_key"] = "OPENWEATHERMAP_API_KEY";
        _envMap["qweather_api_key"] = "QWEATHER_API_KEY";
        _envMap["baidu_translate_appid"] = "BAIDU_TRANSLATE_APPID";
        _envMap["baidu_translate_key"] = "BAIDU_TRANSLATE_KEY";
        _envMap["unsplash_access_key"] = "UNSPLASH_ACCESS_KEY";
        _envMap["unsplash_secret_key"] = "UNSPLASH_SECRET_KEY";
        _envMap["pixabay_api_key"] = "PIXABAY_API_KEY";
        _envMap["github_token"] = "GITHUB_TOKEN";
        _envMap["smtp_user"] = "SMTP_USER";
        _envMap["smtp_pass"] = "SMTP_PASS";
        _envMap["smtp_host"] = "SMTP_HOST";
        _envMap["smtp_port"] = "SMTP_PORT";
        _envMap["ollama_host"] = "OLLAMA_HOST";
        _envMap["a2a_bearer_token"] = "A2A_BEARER_TOKEN";
    }
}

internal sealed class SecretsExport
{
    [JsonPropertyName("version")]
    public int Version { get; init; }

    [JsonPropertyName("exported_at")]
    public DateTime ExportedAt { get; init; }

    [JsonPropertyName("secrets")]
    public Dictionary<string, string> Secrets { get; init; } = new();
}
