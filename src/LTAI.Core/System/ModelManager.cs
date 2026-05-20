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
        var vault = SecretVault.Instance;
        var providers = new List<object>();

        foreach (var (name, config) in aiConfig.Providers)
        {
            var hasKey = !string.IsNullOrWhiteSpace(config.ApiKey) ||
                         !string.IsNullOrWhiteSpace(vault.Get($"{name}_api_key"));
            providers.Add(new
            {
                name,
                endpoint = config.Endpoint,
                default_model = config.Model,
                api_key_configured = hasKey
            });
        }

        return new
        {
            configured_providers = providers,
            layers = new
            {
                l0 = new { aiConfig.L0.Provider, aiConfig.L0.Model },
                l1 = new { aiConfig.L1.Provider, aiConfig.L1.Model, aiConfig.L1.Temperature },
                l2 = new { aiConfig.L2.Provider, aiConfig.L2.Model, aiConfig.L2.Temperature }
            },
            default_provider = aiConfig.DefaultProvider,
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
