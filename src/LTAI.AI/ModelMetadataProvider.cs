using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using LTAI.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace LTAI.AI;

public sealed class ModelMetadataProvider : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ModelMetadataProvider> _logger;
    private readonly ConcurrentDictionary<string, ModelMetadata> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ProviderModels> _providerModels = new(StringComparer.OrdinalIgnoreCase);
    private readonly ActivitySource _activitySource = new("LTAI.AI.Models");
    private Timer? _refreshTimer;
    private bool _disposed;

    public ModelMetadataProvider(IHttpClientFactory httpFactory, ILogger<ModelMetadataProvider> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public IReadOnlyCollection<ModelMetadata> AllModels => (IReadOnlyCollection<ModelMetadata>)_models.Values;
    public IReadOnlyCollection<ProviderModels> AllProviders => (IReadOnlyCollection<ProviderModels>)_providerModels.Values;

    public event Action? Refreshed;

    /// <summary>Query all configured providers' /v1/models API, merge with hardcoded defaults.</summary>
    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        using var activity = _activitySource.StartActivity("RefreshModels", ActivityKind.Internal);

        var providers = KnownKeys.GetDefaultProviders();
        var tasks = providers
            .Where(p => !string.IsNullOrEmpty(SecretManager.Get(p.envVar)))
            .Select(p => FetchProviderModelsAsync(p, ct));

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        foreach (var result in results)
        {
            if (result == null) continue;
            _providerModels[result.Provider.Name] = result.Provider;

            foreach (var model in result.Models)
                _models[model.Id] = model;
        }

        _logger.LogInformation("ModelMetadataProvider: refreshed {Count} models from {ProviderCount} providers",
            _models.Count, _providerModels.Count);

        Refreshed?.Invoke();
    }

    public ModelMetadata? GetModelInfo(string provider, string modelId)
    {
        if (_models.TryGetValue(modelId, out var m))
            return m;

        if (KnownCapabilities.All.TryGetValue(modelId, out var known))
        {
            var pricing = KnownKeys.All.FirstOrDefault(k =>
                k.Service.Equals(provider, StringComparison.OrdinalIgnoreCase));
            var meta = new ModelMetadata(
                modelId, provider, known.ContextWindow, known.MaxOutput, known.Caps,
                pricing?.PriceInPerM, pricing?.PriceOutPerM, DateTime.MinValue);
            _models[modelId] = meta;
            return meta;
        }

        return null;
    }

    public int GetContextWindow(string provider, string modelId, int fallback = 64000)
    {
        var info = GetModelInfo(provider, modelId);
        return info?.ContextWindow ?? fallback;
    }

    public bool SupportsCapability(string modelId, ModelCapability cap)
    {
        if (_models.TryGetValue(modelId, out var m))
            return m.Capabilities.HasFlag(cap);
        if (KnownCapabilities.All.TryGetValue(modelId, out var known))
            return known.Item3.HasFlag(cap);
        return false;
    }

    /// <summary>Best model match for a given capability, preferring the default provider.</summary>
    public (string Provider, string Model)? RecommendModel(
        ModelCapability required, string? preferProvider = null)
    {
        var best = _models.Values
            .Where(m => m.Capabilities.HasFlag(required))
            .OrderByDescending(m =>
            {
                var score = 0;
                if (preferProvider != null &&
                    m.Provider.Equals(preferProvider, StringComparison.OrdinalIgnoreCase))
                    score += 100;
                score += m.ContextWindow.HasValue ? m.ContextWindow.Value switch
                {
                    >= 1_000_000 => 50,
                    >= 128_000 => 40,
                    >= 64_000 => 30,
                    >= 32_000 => 20,
                    _ => 10
                } : 0;
                return score;
            })
            .FirstOrDefault();

        if (best != null)
            return (best.Provider, best.Id);

        if (KnownCapabilities.All.FirstOrDefault(kvp => kvp.Value.Item3.HasFlag(required)) is { Key: var id, Value: var cap })
        {
            var provider = KnownKeys.All.FirstOrDefault(k =>
                k.Model != null && id.StartsWith(k.Model.Split('/')[0], StringComparison.OrdinalIgnoreCase))?.Service ?? "Unknown";
            if (!string.IsNullOrEmpty(id))
                return (provider, id);
        }

        return null;
    }

    /// <summary>Start background refresh timer.</summary>
    public void StartBackgroundRefresh()
    {
        _refreshTimer?.Dispose();
        _refreshTimer = new Timer(async _ =>
        {
            try { await RefreshAllAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Background model refresh failed"); }
        }, null, RefreshInterval, RefreshInterval);
    }

    private async Task<ModelFetchResult?> FetchProviderModelsAsync(
        (string envVar, string endpoint, string model, string name) provider, CancellationToken ct)
    {
        var apiKey = SecretManager.Get(provider.envVar);
        if (string.IsNullOrEmpty(apiKey)) return null;

        try
        {
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);

            // Anthropic uses ISO date, different base path
            Uri modelsUri;
            bool isAnthropic = provider.name.Equals("Anthropic", StringComparison.OrdinalIgnoreCase);
            if (isAnthropic)
                modelsUri = new Uri(new Uri(provider.endpoint.TrimEnd('/')), "/v1/models");
            else if (provider.name.Equals("Doubao", StringComparison.OrdinalIgnoreCase))
            {
                // Doubao/Volcengine uses ARK endpoint — /v1/models may not work
                _logger.LogDebug("Skipping /v1/models for Doubao (ARK endpoint)");
                return BuildFallbackResult(provider);
            }
            else if (provider.name.Equals("Spark", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Skipping /v1/models for Spark (WebSocket endpoint)");
                return BuildFallbackResult(provider);
            }
            else if (provider.name.Equals("Baidu(ERNIE)", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Skipping /v1/models for Baidu (custom IAM auth)");
                return BuildFallbackResult(provider);
            }
            else
                modelsUri = new Uri(new Uri(provider.endpoint.TrimEnd('/')), "/models");

            var req = new HttpRequestMessage(HttpMethod.Get, modelsUri);
            req.Headers.Authorization = new("Bearer", apiKey);

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                return BuildFallbackResult(provider);

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseModelsResponse(body, provider);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch models from {Provider}", provider.name);
            return BuildFallbackResult(provider);
        }
    }

    private ModelFetchResult ParseModelsResponse(string body,
        (string envVar, string endpoint, string model, string name) provider)
    {
        try
        {
            using var json = JsonDocument.Parse(body);

            if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return BuildFallbackResult(provider);

            var modelIds = new List<string>();
            var metadataList = new List<ModelMetadata>();

            foreach (var entry in data.EnumerateArray())
            {
                var id = entry.GetProperty("id").GetString();
                if (string.IsNullOrEmpty(id)) continue;
                modelIds.Add(id);

                int? ctx = null;
                if (entry.TryGetProperty("max_context_length", out var ctxProp))
                    ctx = ctxProp.GetInt32();
                else if (entry.TryGetProperty("context_length", out var ctx2))
                    ctx = ctx2.GetInt32();

                int? maxOut = null;
                if (entry.TryGetProperty("max_output", out var outProp))
                    maxOut = outProp.GetInt32();

                var caps = ModelCapability.Chat | ModelCapability.Streaming;
                if (KnownCapabilities.All.TryGetValue(id, out var known))
                    caps = known.Item3;

                var pricing = KnownKeys.All.FirstOrDefault(k =>
                    k.Service.Equals(provider.name, StringComparison.OrdinalIgnoreCase));

                metadataList.Add(new ModelMetadata(
                    id, provider.name, ctx ?? known.Item1, maxOut ?? known.Item2, caps,
                    pricing?.PriceInPerM, pricing?.PriceOutPerM, DateTime.UtcNow));
            }

            var pm = new ProviderModels(provider.name, provider.endpoint, provider.envVar, modelIds, DateTime.UtcNow);
            return new ModelFetchResult(pm, metadataList);
        }
        catch (JsonException)
        {
            return BuildFallbackResult(provider);
        }
    }

    private ModelFetchResult BuildFallbackResult(
        (string envVar, string endpoint, string model, string name) provider)
    {
        var pricing = KnownKeys.All.FirstOrDefault(k =>
            k.Service.Equals(provider.name, StringComparison.OrdinalIgnoreCase));

        int? ctx = null;
        int? maxOut = null;
        var caps = ModelCapability.Chat | ModelCapability.Streaming;
        if (KnownCapabilities.All.TryGetValue(provider.model, out var kc))
        {
            ctx = kc.Item1;
            maxOut = kc.Item2;
            caps = kc.Item3;
        }

        var meta = new ModelMetadata(
            provider.model, provider.name, ctx, maxOut, caps,
            pricing?.PriceInPerM, pricing?.PriceOutPerM, DateTime.MinValue);

        var pm = new ProviderModels(provider.name, provider.endpoint, provider.envVar, [provider.model], DateTime.MinValue);
        return new ModelFetchResult(pm, [meta]);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshTimer?.Dispose();
        _activitySource.Dispose();
    }
}
