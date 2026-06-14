using System.Text.Json;
using LTAI.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.AI;

/// <summary>
/// Loads provider/model metadata from the local dataset file.
///
/// Data sources (priority):
///   1. <c>models/models-dev-providers.json</c> — bundled snapshot (252KB, 8 providers × 500+ models)
///   2. <c>https://models.dev/api.json</c> — background refresh (24h TTL), updates the local file
///
/// The bundled file is the single source of truth. A background timer refreshes it
/// from the models.dev API so new models and pricing changes are picked up automatically.
/// </summary>
public sealed class ModelsDevClient
{
    private const string ApiUrl = "https://models.dev/api.json";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(24);

    private readonly HttpClient _http;
    private readonly string _primaryPath;
    private readonly string _cachePath;
    private readonly ILogger<ModelsDevClient> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // LTAI provider ID → (display name override, API key management URL)
    private static readonly Dictionary<string, (string Name, string? KeyUrl)> LtaISupplements = new(StringComparer.OrdinalIgnoreCase)
    {
        ["deepseek"] = ("DeepSeek", "https://platform.deepseek.com/api_keys"),
        ["siliconflow"] = ("SiliconFlow", "https://cloud.siliconflow.cn/account/ak"),
        ["alibaba"] = ("Aliyun(Qwen)", "https://bailian.console.aliyun.com/#/api-key"),
        ["zhipuai"] = ("Zhipu(GLM)", "https://open.bigmodel.cn/usercenter/apikeys"),
        ["openai"] = ("OpenAI", "https://platform.openai.com/api-keys"),
        ["anthropic"] = ("Anthropic", "https://console.anthropic.com/settings/keys"),
        ["openrouter"] = ("OpenRouter", "https://openrouter.ai/keys"),
        ["stepfun"] = ("StepFun", "https://platform.stepfun.com/console/apikey"),
        ["ollama"] = ("Ollama", null),
        ["llamacpp"] = ("llama.cpp", null),
        ["vllm"] = ("vLLM", null),
        ["lmstudio"] = ("LM Studio", null),
        ["koboldcpp"] = ("KoboldCPP", null),
    };

    public ModelsDevClient(HttpClient http, IOptions<LTAIOptions> options, ILogger<ModelsDevClient> logger)
    {
        _http = http;
        _logger = logger;
        _primaryPath = Path.Combine(AppContext.BaseDirectory, "models", "models-dev-providers.json");
        _cachePath = options.Value.ResolveDataPath("models-dev-cache.json");
    }

    /// <summary>
    /// Loads providers from the primary bundled file. Never fails — falls back
    /// to the API-cached file if the primary file is missing.
    /// </summary>
    public ProviderInfo[] LoadProviders()
    {
        // 1. Primary: bundled snapshot in models/
        if (File.Exists(_primaryPath))
        {
            var providers = ParseFile(_cachePath);
        if (providers.Length == 0)
            providers = ParseFile(_primaryPath);
            if (providers.Length > 0)
            {
                _logger.LogInformation("Loaded {Count} providers from {Path}", providers.Length, _primaryPath);
                return providers;
            }
        }

        // 2. Merge with edge inference providers (Ollama, vLLM, llama.cpp, etc.)
        var primaryProviders = ParseFile(_primaryPath).ToList();
        var edgePath = Path.Combine(AppContext.BaseDirectory, "models", "edge-providers.json");
        if (File.Exists(edgePath))
        {
            var edgeProviders = ParseFile(edgePath);
            if (edgeProviders.Length > 0)
            {
                _logger.LogInformation("Loaded {Count} edge providers from {Path}", edgeProviders.Length, edgePath);
                foreach (var ep in edgeProviders)
                {
                    var idx = primaryProviders.FindIndex(p => string.Equals(p.Id, ep.Id, StringComparison.OrdinalIgnoreCase));
                    if (idx >= 0)
                        primaryProviders[idx] = ep;  // override with edge version
                    else
                        primaryProviders.Add(ep);
                }
            }
        }

        if (primaryProviders.Count > 0)
            return primaryProviders.ToArray();

        // 3. Fallback: API-fetched cache in .livingtree/
        if (File.Exists(_cachePath))
        {
            var providers = ParseFile(_cachePath);
            if (providers.Length > 0)
            {
                _logger.LogInformation("Loaded {Count} providers from cache {Path}", providers.Length, _cachePath);
                return providers;
            }
        }

        _logger.LogError("No provider data found at {Primary} or {Cache}", _primaryPath, _cachePath);
        return [];
    }

    /// <summary>
    /// Background refresh: fetches from models.dev API and updates the local file.
    /// Called periodically (24h) and on manual trigger.
    /// </summary>
    public async Task RefreshFromApiAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var providers = await FetchAndParseAsync(ct).ConfigureAwait(false);
            if (providers.Length == 0) return;

            var json = SerializeProviders(providers);
            var dir = Path.GetDirectoryName(_cachePath);
            if (dir != null) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(_cachePath, json, ct).ConfigureAwait(false);

            _logger.LogInformation("Refreshed {Count} providers from models.dev → {Path}", providers.Length, _cachePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Background refresh from models.dev failed");
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Starts a background timer that refreshes the local file from models.dev API.
    /// </summary>
    public Timer StartBackgroundRefresh()
    {
        return new Timer(_ =>
        {
            _ = Task.Run(async () =>
            {
                try { await RefreshFromApiAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogDebug(ex, "Background models.dev refresh error"); }
            });
        }, null, RefreshInterval, RefreshInterval);
    }

    // ── File I/O ──────────────────────────────────────────────────

    private ProviderInfo[] ParseFile(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return [];
            return ProviderRegistry.ParseApiJson(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse {Path}", path);
            return [];
        }
    }

    private static string SerializeProviders(ProviderInfo[] providers)
    {
        // Reconstruct api.json format for compatibility
        var dict = new Dictionary<string, object>();
        foreach (var p in providers)
        {
            var models = new Dictionary<string, object>();
            foreach (var m in p.Models)
            {
                models[m.ShortId] = new Dictionary<string, object?>
                {
                    ["id"] = m.Id, ["name"] = m.Name, ["family"] = m.Family,
                    ["tool_call"] = m.ToolCall, ["reasoning"] = m.Reasoning,
                    ["structured_output"] = m.StructuredOutput, ["attachment"] = m.Attachment,
                    ["temperature"] = m.Temperature,
                    ["modalities"] = new { input = m.InputModalities, output = m.OutputModalities },
                    ["limit"] = new { context = m.ContextWindow, output = m.MaxOutput },
                    ["cost"] = new { input = m.PriceInPerM, output = m.PriceOutPerM },
                    ["knowledge"] = m.KnowledgeCutoff, ["release_date"] = m.ReleaseDate,
                    ["open_weights"] = m.OpenWeights,
                };
            }
            dict[p.Id] = new Dictionary<string, object?>
            {
                ["id"] = p.Id, ["name"] = p.Name,
                ["env"] = p.EnvVars, ["npm"] = ApiFormatToNpm(p.ApiFormat),
                ["api"] = p.Endpoint, ["doc"] = p.DocUrl,
                ["models"] = models,
            };
        }
        return JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = false });
    }

    private static string? ApiFormatToNpm(ApiFormat f) => f switch
    {
        ApiFormat.OpenAICompatible => "@ai-sdk/openai-compatible",
        ApiFormat.Anthropic => "@ai-sdk/anthropic",
        _ => null,
    };

    // ── Fetch from API ────────────────────────────────────────────

    private async Task<ProviderInfo[]> FetchAndParseAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync(ApiUrl, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var raw = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);

        var now = DateTime.UtcNow;
        var providers = new List<ProviderInfo>();

        foreach (var prop in raw.RootElement.EnumerateObject())
        {
            var e = prop.Value;
            if (e.ValueKind != JsonValueKind.Object) continue;

            var id = prop.Name;
            var modelsObj = e.TryGetProperty("models", out var m) ? m : default;
            if (modelsObj.ValueKind != JsonValueKind.Object) continue;

            var name = GetStr(e, "name") ?? id;
            var env = GetArr(e, "env") ?? [ProviderRegistry.DefaultEnvVar(id)];
            var npm = GetStr(e, "npm");
            var api = GetStr(e, "api");
            var docUrl = GetStr(e, "doc");
            var apiFormat = ProviderRegistry.NpmToApiFormat(npm);
            var supplement = LtaISupplements.GetValueOrDefault(id);

            var models = ParseModels(modelsObj);
            if (models.Count == 0) continue;

            providers.Add(new ProviderInfo(
                Id: id,
                Name: supplement.Name ?? name,
                EnvVars: env,
                Endpoint: api,
                ApiFormat: apiFormat,
                DocUrl: docUrl,
                KeyUrl: supplement.KeyUrl,
                Models: models.ToArray(),
                FetchedAt: now));
        }
        return providers.ToArray();
    }

    private static List<ModelInfo> ParseModels(JsonElement modelsObj)
    {
        var models = new List<ModelInfo>();
        var now = DateTime.UtcNow;
        foreach (var prop in modelsObj.EnumerateObject())
        {
            var m = prop.Value;
            if (m.ValueKind != JsonValueKind.Object) continue;

            var modelId = GetStr(m, "id") ?? prop.Name;
            var limit = m.TryGetProperty("limit", out var l) ? l : default;
            var cost = m.TryGetProperty("cost", out var c) ? c : default;
            var modalities = m.TryGetProperty("modalities", out var mod) ? mod : default;

            models.Add(new ModelInfo(
                Id: modelId, Name: GetStr(m, "name") ?? modelId, Family: GetStr(m, "family") ?? "",
                ToolCall: GetBool(m, "tool_call"), Reasoning: GetBool(m, "reasoning"),
                StructuredOutput: GetBool(m, "structured_output"), Attachment: GetBool(m, "attachment"),
                Temperature: GetBool(m, "temperature"),
                InputModalities: GetArr(modalities, "input") ?? ["text"],
                OutputModalities: GetArr(modalities, "output") ?? ["text"],
                ContextWindow: GetInt(limit, "context") ?? 64000,
                MaxOutput: GetInt(limit, "output") ?? 4096,
                PriceInPerM: GetDec(cost, "input"), PriceOutPerM: GetDec(cost, "output"),
                KnowledgeCutoff: GetStr(m, "knowledge"), ReleaseDate: GetStr(m, "release_date"),
                Benchmarks: ParseBenchmarks(m), OpenWeights: GetBool(m, "open_weights")));
        }
        return models;
    }

    private static ModelBenchmark[]? ParseBenchmarks(JsonElement m)
    {
        if (!m.TryGetProperty("benchmarks", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        var list = new List<ModelBenchmark>();
        foreach (var b in arr.EnumerateArray())
            list.Add(new(GetStr(b, "name") ?? "", GetDbl(b, "score"), GetStr(b, "metric") ?? "", GetStr(b, "source") ?? "", GetStr(b, "date")));
        return list.Count > 0 ? list.ToArray() : null;
    }

    private static string? GetStr(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool GetBool(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v is { ValueKind: JsonValueKind.True or JsonValueKind.False } && v.GetBoolean();
    private static int? GetInt(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var x) ? x : null;
    private static decimal GetDec(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : 0m;
    private static double GetDbl(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : 0;
    private static string[]? GetArr(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        return arr.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToArray();
    }
}
