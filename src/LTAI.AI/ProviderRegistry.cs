using System.Collections.Concurrent;
using System.Text.Json;
using LTAI.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.AI;

/// <summary>
/// Central registry of all known AI providers and their models.
///
/// Data sources (priority order):
///   1. models.dev api.json (via <see cref="ModelsDevClient"/>) — live data with 24h cache
///   2. <c>models/models-dev-providers.json</c> — offline fallback snapshot
/// </summary>
public sealed class ProviderRegistry
{
    private readonly ConcurrentDictionary<string, ProviderInfo> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ReaderWriterLockSlim _rwLock = new();
    private readonly ModelsDevClient _modelsDev;
    private readonly ILogger<ProviderRegistry> _logger;

    public ProviderRegistry(ModelsDevClient modelsDev, ILogger<ProviderRegistry> logger)
    {
        _modelsDev = modelsDev;
        _logger = logger;
    }

/// <summary>
/// Initializes the registry. Loads from the bundled models-dev-providers.json file.
/// Call RefreshFromApiAsync() periodically to update from the live API.
/// Thread-safe: readers are protected by a RW lock, so concurrent reads
/// during refresh see either the old or the new complete provider set — never partial.
/// </summary>
public void Initialize()
{
    var providers = _modelsDev.LoadProviders();
    if (providers.Length == 0)
    {
        _logger.LogError("ProviderRegistry: no providers available — check models/models-dev-providers.json");
        return;
    }

    var updated = new Dictionary<string, ProviderInfo>(StringComparer.OrdinalIgnoreCase);
    foreach (var p in providers)
        updated[p.Id] = p;

    _rwLock.EnterWriteLock();
    try
    {
        _providers.Clear();
        foreach (var kv in updated)
            _providers[kv.Key] = kv.Value;
    }
    finally { _rwLock.ExitWriteLock(); }

    _logger.LogInformation("ProviderRegistry ready: {Count} providers, {ModelCount} models",
        _providers.Count, _providers.Values.Sum(p => p.Models.Length));
}

/// <summary>
/// Triggers a background refresh from the models.dev API.
/// Thread-safe: uses the same RW lock as Initialize() for swap semantics.
/// </summary>
public async Task RefreshAsync(CancellationToken ct)
{
    await _modelsDev.RefreshFromApiAsync(ct).ConfigureAwait(false);
    // Re-load after refresh
    Initialize();
}

    /// <summary>
    /// Parses models.dev api.json format into <see cref="ProviderInfo"/>[].
    /// </summary>
    public static ProviderInfo[] ParseApiJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var now = DateTime.UtcNow;
        var providers = new List<ProviderInfo>();

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var entry = prop.Value;
            if (entry.ValueKind != JsonValueKind.Object) continue;

            var id = prop.Name;
            var name = GetString(entry, "name") ?? id;
            var env = GetStringArray(entry, "env") ?? [DefaultEnvVar(id)];
            var npm = GetString(entry, "npm");
            var api = GetString(entry, "api");
            var docUrl = GetString(entry, "doc");
            var apiFormat = NpmToApiFormat(npm);

            var modelsObj = entry.TryGetProperty("models", out var m) ? m : default;
            if (modelsObj.ValueKind != JsonValueKind.Object) continue;

            var models = ParseModels(modelsObj);
            if (models.Count == 0) continue;

            // Parse optional kv_cache config (edge providers only)
            KvCacheConfig? kvCache = null;
            if (entry.TryGetProperty("kv_cache", out var kv) && kv.ValueKind == JsonValueKind.Object)
            {
                kvCache = new KvCacheConfig(
                    Engine: GetString(kv, "engine"),
                    Description: GetString(kv, "description"),
                    ServerArgs: GetString(kv, "server_args"),
                    EnvVar: GetString(kv, "env_var"),
                    Recommended: GetString(kv, "recommended"),
                    Params: ParseKvCacheParams(kv, "params"),
                    PerRequestSupported: GetBool(kv, "per_request_supported"),
                    PerRequestParams: ParseKvCacheParams(kv, "per_request_params"));
            }

            providers.Add(new ProviderInfo(
                Id: id,
                Name: name,
                EnvVars: env,
                Endpoint: api,
                ApiFormat: apiFormat,
                DocUrl: docUrl,
                KeyUrl: null,
                Models: models.ToArray(),
                FetchedAt: now,
                KvCache: kvCache));
        }
        return providers.ToArray();
    }

    // ── Query API ────────────────────────────────────────────────

    public IReadOnlyCollection<ProviderInfo> Providers => _providers.Values.ToList().AsReadOnly();
    public IEnumerable<ProviderInfo> LlmProviders => _providers.Values.Where(p => p.IsLlmProvider);

    /// <summary>Remote API providers — require API keys (non-empty EnvVars).</summary>
    public IEnumerable<ProviderInfo> RemoteProviders =>
        _providers.Values.Where(p => p.EnvVars is { Length: > 0 } && !string.IsNullOrEmpty(p.EnvVars[0]));

    /// <summary>Edge/local providers — no API keys needed (empty EnvVars).</summary>
    public IEnumerable<ProviderInfo> EdgeProviders =>
        _providers.Values.Where(p => p.EnvVars is null or { Length: 0 } || string.IsNullOrEmpty(p.EnvVars[0]));

    /// <summary>Active remote providers — LLM providers with configured API keys.</summary>
    public IEnumerable<ProviderInfo> ActiveProviders =>
        _providers.Values.Where(p => p.IsLlmProvider && p.EnvVars.Any(e => !string.IsNullOrEmpty(SecretManager.Get(e))));

    /// <summary>Active edge providers — LLM edge providers (always "active", no keys needed).</summary>
    public IEnumerable<ProviderInfo> ActiveEdgeProviders =>
        EdgeProviders.Where(p => p.IsLlmProvider);

    public ProviderInfo? FindProvider(string id) =>
        _providers.TryGetValue(id, out var p) ? p : null;

    public ProviderInfo? FindByName(string name) =>
        _providers.Values.FirstOrDefault(p =>
            string.Equals(p.Id, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public ModelInfo? FindModel(string shortId) =>
        _providers.Values.Select(p => p.FindModel(shortId)).FirstOrDefault(m => m != null);

    public ModelInfo? FindModelById(string fullId) =>
        _providers.Values.Select(p => p.FindModelById(fullId)).FirstOrDefault(m => m != null);

    public IEnumerable<ModelInfo> GetAllModels(IEnumerable<string>? providerIds = null)
    {
        var providers = providerIds != null
            ? providerIds.Select(id => FindProvider(id)).Where(p => p != null).Select(p => p!)
            : _providers.Values;
        return providers.SelectMany(p => p.Models);
    }

    public string? GetEnvVar(string providerId) => FindProvider(providerId)?.EnvVar;
    public string? GetEndpoint(string providerId) => FindProvider(providerId)?.Endpoint;

    // ── JSON Parsing (public for ModelsDevClient) ────────────────

    private static List<ModelInfo> ParseModels(JsonElement modelsObj)
    {
        var models = new List<ModelInfo>();
        foreach (var prop in modelsObj.EnumerateObject())
        {
            var m = prop.Value;
            if (m.ValueKind != JsonValueKind.Object) continue;

            var modelId = GetString(m, "id") ?? prop.Name;
            var limit = m.TryGetProperty("limit", out var l) ? l : default;
            var cost = m.TryGetProperty("cost", out var c) ? c : default;
            var modalities = m.TryGetProperty("modalities", out var mod) ? mod : default;

            models.Add(new ModelInfo(
                Id: modelId,
                Name: GetString(m, "name") ?? modelId,
                Family: GetString(m, "family") ?? "",
                ToolCall: GetBool(m, "tool_call"),
                Reasoning: GetBool(m, "reasoning"),
                StructuredOutput: GetBool(m, "structured_output"),
                Attachment: GetBool(m, "attachment"),
                Temperature: GetBool(m, "temperature"),
                InputModalities: GetStringArray(modalities, "input") ?? ["text"],
                OutputModalities: GetStringArray(modalities, "output") ?? ["text"],
                ContextWindow: GetInt(limit, "context") ?? 64000,
                MaxOutput: GetInt(limit, "output") ?? 4096,
                PriceInPerM: GetDecimal(cost, "input"),
                PriceOutPerM: GetDecimal(cost, "output"),
                KnowledgeCutoff: GetString(m, "knowledge"),
                ReleaseDate: GetString(m, "release_date"),
                Benchmarks: ParseBenchmarks(m),
                OpenWeights: GetBool(m, "open_weights")));
        }
        return models;
    }

    private static Dictionary<string, object>? ParseKvCacheParams(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var obj) || obj.ValueKind != JsonValueKind.Object)
            return null;
        var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in obj.EnumerateObject())
        {
            dict[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number when prop.Value.TryGetInt32(out var i) => i,
                JsonValueKind.Number when prop.Value.TryGetDouble(out var d) => d,
                JsonValueKind.String => prop.Value.GetString() ?? "",
                _ => prop.Value.GetRawText(),
            };
        }
        return dict.Count > 0 ? dict : null;
    }

    private static ModelBenchmark[]? ParseBenchmarks(JsonElement m)
    {
        if (!m.TryGetProperty("benchmarks", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;
        var list = new List<ModelBenchmark>();
        foreach (var b in arr.EnumerateArray())
        {
            list.Add(new ModelBenchmark(
                GetString(b, "name") ?? "",
                GetDouble(b, "score"),
                GetString(b, "metric") ?? "",
                GetString(b, "source") ?? "",
                GetString(b, "date")));
        }
        return list.Count > 0 ? list.ToArray() : null;
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool GetBool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False && v.GetBoolean();

    private static int? GetInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static decimal GetDecimal(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : 0m;

    private static double GetDouble(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : 0;

    private static string[]? GetStringArray(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object) return null;
        if (!parent.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        return arr.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToArray();
    }

    public static ApiFormat NpmToApiFormat(string? npm) => npm switch
    {
        not null when npm.StartsWith("@ai-sdk/openai", StringComparison.OrdinalIgnoreCase) => ApiFormat.OpenAICompatible,
        not null when npm.StartsWith("@ai-sdk/anthropic", StringComparison.OrdinalIgnoreCase) => ApiFormat.Anthropic,
        not null when npm.StartsWith("@openrouter", StringComparison.OrdinalIgnoreCase) => ApiFormat.OpenAICompatible,
        not null when npm.StartsWith("@ai-sdk/", StringComparison.OrdinalIgnoreCase) => ApiFormat.OpenAICompatible,
        _ => ApiFormat.Unknown,
    };

    public static string DefaultEnvVar(string providerId) => providerId.ToUpperInvariant() switch
    {
        "ALIBABA" => "DASHSCOPE_API_KEY",
        _ => $"{providerId.ToUpperInvariant()}_API_KEY",
    };
}
