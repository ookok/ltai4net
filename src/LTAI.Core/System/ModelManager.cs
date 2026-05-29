using System.Text.Json;
using LTAI.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.System;

public sealed class ModelManager
{
    private readonly IProviderRegistry _registry;
    private readonly ILogger<ModelManager> _logger;

    public ModelManager(IProviderRegistry registry, ILogger<ModelManager>? logger = null)
    {
        _registry = registry;
        _logger = logger ?? NullLogger<ModelManager>.Instance;
    }

    public List<ModelInfo> ListAll()
    {
        var models = new List<ModelInfo>();
        foreach (var provider in _registry.AllProviders)
        {
            var baseUrl = _registry.GetBaseUrl(provider) ?? "";
            var defaultModel = _registry.GetDefaultModel(provider) ?? "";
            var caps = _registry.GetCapabilities(provider);
            var tiers = _registry.GetTierVariants(provider);

            if (tiers.Count > 0)
            {
                foreach (var tier in tiers)
                {
                    models.Add(new ModelInfo
                    {
                        Provider = provider,
                        ModelName = tier.DefaultModel,
                        TierName = tier.Name,
                        BaseUrl = baseUrl,
                        Capabilities = caps
                    });
                }
            }
            else
            {
                models.Add(new ModelInfo
                {
                    Provider = provider,
                    ModelName = defaultModel,
                    TierName = "default",
                    BaseUrl = baseUrl,
                    Capabilities = caps
                });
            }
        }
        return models;
    }

    public ModelInfo? Show(string providerOrModel)
    {
        var all = ListAll();
        return all.FirstOrDefault(m =>
            m.Provider.Equals(providerOrModel, StringComparison.OrdinalIgnoreCase) ||
            m.ModelName.Equals(providerOrModel, StringComparison.OrdinalIgnoreCase) ||
            m.TierName.Equals(providerOrModel, StringComparison.OrdinalIgnoreCase));
    }

    public List<ModelInfo> Search(string keyword)
    {
        var all = ListAll();
        var lower = keyword.ToLowerInvariant();
        return all.Where(m =>
            m.Provider.Contains(lower, StringComparison.OrdinalIgnoreCase) ||
            m.ModelName.Contains(lower, StringComparison.OrdinalIgnoreCase) ||
            m.Capabilities.Any(c => c.Contains(lower, StringComparison.OrdinalIgnoreCase))
        ).ToList();
    }

    public object SyncInfo()
    {
        return new
        {
            total_providers = _registry.AllProviders.Count(),
            providers = _registry.AllProviders.Select(p => new
            {
                name = p,
                base_url = _registry.GetBaseUrl(p) ?? "",
                default_model = _registry.GetDefaultModel(p) ?? "",
                capabilities = _registry.GetCapabilities(p),
                tier_variants = _registry.GetTierVariants(p).Select(t => new { t.Name, t.DefaultModel }).ToList()
            }).ToList()
        };
    }

    public object Status(AIConfig aiConfig)
    {
        var providers = new List<object>();

        foreach (var (name, config) in aiConfig.Providers)
        {
            var hasKey = !string.IsNullOrWhiteSpace(config.ApiKey) ||
                         !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable($"{name.ToUpperInvariant()}_API_KEY"));
            providers.Add(new
            {
                name,
                endpoint = config.Endpoint,
                default_model = config.Model,
                api_key_configured = hasKey
            });
        }

        var resolvedFast = aiConfig.GetLayerConfig("fast");
        var resolvedDeep = aiConfig.GetLayerConfig("deep");
        var resolvedEmbed = aiConfig.GetLayerConfig("embedding");

        return new
        {
            configured_providers = providers,
            layers = new
            {
                l0 = new { resolvedEmbed.Provider, resolvedEmbed.Model },
                l1 = new { resolvedFast.Provider, resolvedFast.Model, Temperature = resolvedFast.Temperature },
                l2 = new { resolvedDeep.Provider, resolvedDeep.Model, Temperature = resolvedDeep.Temperature }
            },
            default_provider = aiConfig.Provider,
            daily_budget_usd = aiConfig.DailyBudgetUsd,
            total_registered = _registry.AllProviders.Count()
        };
    }
}

public sealed class ModelInfo
{
    public string Provider { get; init; } = "";
    public string ModelName { get; init; } = "";
    public string TierName { get; init; } = "";
    public string BaseUrl { get; init; } = "";
    public List<string> Capabilities { get; init; } = new();
}
