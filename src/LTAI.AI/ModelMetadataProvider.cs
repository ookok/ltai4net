using System.Collections.Concurrent;
using System.Diagnostics;
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
    private readonly ProviderRegistry _registry;
    private readonly ILogger<ModelMetadataProvider> _logger;
    private readonly ConcurrentDictionary<string, ModelMetadata> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ProviderModels> _providerModels = new(StringComparer.OrdinalIgnoreCase);
    private readonly ActivitySource _activitySource = new("LTAI.AI.Models");
    private Timer? _refreshTimer;
    private bool _disposed;

    public ModelMetadataProvider(IHttpClientFactory httpFactory, ProviderRegistry registry, ILogger<ModelMetadataProvider> logger)
    {
        _httpFactory = httpFactory;
        _registry = registry;
        _logger = logger;
    }

    public IReadOnlyCollection<ModelMetadata> AllModels => _models.Values.ToList().AsReadOnly();
    public IReadOnlyCollection<ProviderModels> AllProviders => _providerModels.Values.ToList().AsReadOnly();

    public event Action? Refreshed;

    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        using var activity = _activitySource.StartActivity("RefreshModels", ActivityKind.Internal);

        var llmProviders = _registry.LlmProviders.ToList();
        var tasks = llmProviders
            .Where(p => !string.IsNullOrEmpty(SecretManager.Get(p.EnvVar)))
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

        // Fallback: use ProviderRegistry (models.dev data)
        var regModel = _registry.FindModel(modelId);
        if (regModel != null)
        {
            var meta = regModel.ToLegacy(provider);
            _models[modelId] = meta;
            return meta;
        }

        // Last resort: provider-level pricing from registry
        var prov = _registry.FindProvider(provider);
        if (prov != null)
        {
            var fallback = new ModelMetadata(
                modelId, provider, 64000, 4096,
                ModelCapability.Chat | ModelCapability.Streaming,
                prov.Models.FirstOrDefault()?.PriceInPerM,
                prov.Models.FirstOrDefault()?.PriceOutPerM,
                null, DateTime.MinValue);
            _models[modelId] = fallback;
            return fallback;
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
        var regModel = _registry.FindModel(modelId);
        if (regModel != null)
            return regModel.ToLegacy("").Capabilities.HasFlag(cap);
        return false;
    }

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

        // Fallback: search ProviderRegistry
        foreach (var regModel in _registry.GetAllModels())
        {
            if (regModel.ToLegacy("").Capabilities.HasFlag(required))
                return (regModel.ProviderId, regModel.ShortId);
        }

        return null;
    }

    public void StartBackgroundRefresh()
    {
        _refreshTimer?.Dispose();
        _refreshTimer = new Timer(async _ =>
        {
            try { await RefreshAllAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Background model refresh failed"); }
        }, null, RefreshInterval, RefreshInterval);
    }

    private async Task<ModelFetchResult?> FetchProviderModelsAsync(ProviderInfo provider, CancellationToken ct)
    {
        var apiKey = SecretManager.Get(provider.EnvVar);
        if (string.IsNullOrEmpty(apiKey)) return null;

        try
        {
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);

            Uri modelsUri;
            bool isAnthropic = provider.ApiFormat == ApiFormat.Anthropic;
            if (isAnthropic)
                modelsUri = new Uri(new Uri(provider.Endpoint!.TrimEnd('/')), "/v1/models");
            else if (provider.Id.Equals("doubao", StringComparison.OrdinalIgnoreCase) ||
                     provider.Id.Equals("spark", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Skipping /v1/models for {Provider}", provider.Name);
                return BuildFallbackResult(provider);
            }
            else
                modelsUri = new Uri(new Uri(provider.Endpoint!.TrimEnd('/')), "/models");

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
            _logger.LogDebug(ex, "Failed to fetch models from {Provider}", provider.Name);
            return BuildFallbackResult(provider);
        }
    }

    private ModelFetchResult ParseModelsResponse(string body, ProviderInfo provider)
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

                // Merge with ProviderRegistry (models.dev) data for capabilities + pricing
                var regModel = provider.FindModel(id);
                var caps = regModel?.ToLegacy(provider.Name).Capabilities
                    ?? ModelCapability.Chat | ModelCapability.Streaming;

                metadataList.Add(new ModelMetadata(
                    id, provider.Name, ctx ?? regModel?.ContextWindow, maxOut ?? regModel?.MaxOutput, caps,
                    regModel?.PriceInPerM, regModel?.PriceOutPerM, null, DateTime.UtcNow));
            }

            var pm = new ProviderModels(provider.Name, provider.Endpoint ?? "", provider.EnvVar, modelIds, DateTime.UtcNow);
            return new ModelFetchResult(pm, metadataList);
        }
        catch (JsonException)
        {
            return BuildFallbackResult(provider);
        }
    }

    private ModelFetchResult BuildFallbackResult(ProviderInfo provider)
    {
        var models = new List<ModelMetadata>();
        foreach (var m in provider.Models)
            models.Add(m.ToLegacy(provider.Name));

        var pm = new ProviderModels(provider.Name, provider.Endpoint ?? "", provider.EnvVar,
            provider.Models.Select(m => m.ShortId).ToList(), DateTime.MinValue);
        return new ModelFetchResult(pm, models);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshTimer?.Dispose();
        _activitySource.Dispose();
    }
}
