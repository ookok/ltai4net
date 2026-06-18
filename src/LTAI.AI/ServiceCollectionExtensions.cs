using LTAI.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.AI;

/// <summary>
/// DI registration for the LTAI.AI layer.
/// Registers: LLM router (MultiProviderChatClient), embedders, model metadata.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAIAI(this IServiceCollection services)
    {
        services.AddHttpClient("llm")
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                MaxConnectionsPerServer = int.TryParse(Environment.GetEnvironmentVariable("LTAI_HTTP_MAX_CONN"), out var mc) ? Math.Max(2, mc) : 6,
                PooledConnectionLifetime = TimeSpan.FromMinutes(
                    int.TryParse(Environment.GetEnvironmentVariable("LTAI_HTTP_POOL_LIFETIME_MIN"), out var pl) ? Math.Max(1, pl) : 10),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                EnableMultipleHttp2Connections = true,
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            });

        services.AddSingleton<IToolRegistry, ToolRegistry>();
        services.AddSingleton<ModelsDevClient>();
        services.AddSingleton<ProviderRegistry>(sp =>
        {
            var client = sp.GetRequiredService<ModelsDevClient>();
            var logger = sp.GetRequiredService<ILogger<ProviderRegistry>>();
            var registry = new ProviderRegistry(client, logger);
            registry.Initialize();
            UsageTracker.s_priceResolver = modelId =>
            {
                var model = registry.FindModel(modelId);
                if (model == null) return null;
                var rate = 7.2m;
                return (model.PriceInPerM * rate, model.PriceOutPerM * rate, model.PriceInPerM * rate * 0.1m);
            };
            client.StartBackgroundRefresh();
            return registry;
        });

        services.AddSingleton<ModelScoringEngine>();
        services.AddSingleton<ModelAutoSelector>(sp =>
        {
            var registry = sp.GetRequiredService<ProviderRegistry>();
            var scoring = sp.GetRequiredService<ModelScoringEngine>();
            var opts = sp.GetRequiredService<IOptionsMonitor<LTAIOptions>>();
            var logger = sp.GetRequiredService<ILogger<ModelAutoSelector>>();
            return new ModelAutoSelector(registry, scoring, opts, logger);
        });
        services.AddHostedService<ModelAutoSelectHostedService>();

        services.AddSingleton<ModelMetadataProvider>(sp =>
        {
            var provider = new ModelMetadataProvider(
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<ProviderRegistry>(),
                sp.GetRequiredService<ILogger<ModelMetadataProvider>>());
            Task.Run(async () =>
            {
                try
                {
                    await provider.RefreshAllAsync().ConfigureAwait(false);
                    provider.StartBackgroundRefresh();
                }
                catch (Exception ex)
                {
                    var log = sp.GetService<ILogger<ModelMetadataProvider>>();
                    log?.LogWarning(ex, "Model metadata refresh failed at startup");
                }
            });
            return provider;
        });

        // Register the three manager services, then the router
        services.AddSingleton<CircuitBreakerManager>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<LTAIOptions>>().Value;
            var esc = opts.Escalation;
            var breakerPath = opts.ResolveDataPath("circuit_breaker.db");
            var breakerStore = new CircuitBreakerStore(breakerPath);
            return new CircuitBreakerManager(
                esc.MaxFailuresBeforeCooldown,
                TimeSpan.FromSeconds(esc.CooldownDurationSeconds),
                breakerStore,
                sp.GetService<ILogger<CircuitBreakerManager>>());
        });

        services.AddSingleton<ResponseCacheManager>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<LTAIOptions>>().Value;
            return new ResponseCacheManager(
                opts.AI.ResponseCacheSize > 0 ? opts.AI.ResponseCacheSize : null,
                logger: sp.GetService<ILogger<ResponseCacheManager>>());
        });

        services.AddSingleton<ProviderClientManager>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<LTAIOptions>>().Value;
            var breaker = sp.GetRequiredService<CircuitBreakerManager>();
            var metadata = sp.GetService<ModelMetadataProvider>();
            return new ProviderClientManager(
                opts.AI.DefaultProvider ?? "",
                breaker,
                metadata,
                sp.GetService<ILogger<ProviderClientManager>>(),
                opts.AI.DegradationChain != null
                    ? new Dictionary<string, string>(opts.AI.DegradationChain, StringComparer.OrdinalIgnoreCase)
                    : null);
        });

        services.AddSingleton<MultiProviderChatClient>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<LTAIOptions>>().Value;
            var providers = sp.GetRequiredService<ProviderClientManager>();
            var breaker = sp.GetRequiredService<CircuitBreakerManager>();
            var cache = sp.GetRequiredService<ResponseCacheManager>();
            var logger = sp.GetService<ILogger<MultiProviderChatClient>>();
            var breakerStore = sp.GetService<CircuitBreakerStore>();
            var modelMetadata = sp.GetService<ModelMetadataProvider>();
            var registry = sp.GetRequiredService<ProviderRegistry>();

            var router = new MultiProviderChatClient(opts, providers, breaker, cache, breakerStore, modelMetadata, logger);

            // Resolve L1 provider
            var l1Cfg = opts.AI.L1;
            ProviderInfo? primaryProvider = null;
            if (l1Cfg != null && !string.IsNullOrEmpty(l1Cfg.Provider))
                primaryProvider = registry.FindByName(l1Cfg.Provider);
            primaryProvider ??= registry.ActiveProviders.FirstOrDefault();
            if (primaryProvider == null)
            {
                logger?.LogWarning("No active LLM provider found — router will be empty");
                return router;
            }

            var apiKey = SecretManager.Get(primaryProvider.EnvVar) ?? "";
            var endpoint = !string.IsNullOrEmpty(l1Cfg?.Endpoint) ? l1Cfg.Endpoint : primaryProvider.Endpoint!;
            var l1Model = !string.IsNullOrEmpty(l1Cfg?.Model) ? l1Cfg.Model : primaryProvider.Models[0].ShortId;

            // L1
            var l1ModelInfo = primaryProvider.Models.FirstOrDefault(m => m.ShortId == l1Model);
            var l1EnableThink = l1Cfg?.EnableThinking ?? l1ModelInfo?.Reasoning == true;
            var l1ThoughtInContent = l1Cfg?.ThoughtInContent ?? false;
            var l1Client = primaryProvider.ApiFormat == ApiFormat.Anthropic
                ? AnthropicChatClientFactory.Create(l1Model, apiKey)
                : OpenAIChatClientFactory.Create(endpoint, l1Model, apiKey);
            if (l1EnableThink)
                l1Client = new ThinkingChatClient(l1Client, true, l1ThoughtInContent);
            router.Register("l1", l1Client);
            logger?.LogInformation("L1: {Provider}/{Model} @ {Endpoint}{Think}", primaryProvider.Name, l1Model, endpoint, l1EnableThink ? " (thinking)" : "");

            // L2
            var l2Cfg = opts.AI.L2;
            string? l2Model = !string.IsNullOrEmpty(l2Cfg?.Model) ? l2Cfg.Model : null;
            if (l2Model == null) l2Model = ModelAutoSelectHostedService.LatestResult?.L2;
            if (l2Model == null)
            {
                var scoring = sp.GetRequiredService<ModelScoringEngine>();
                var (best, _) = scoring.SelectBestPair(primaryProvider.Models, ModelTierRequirements.L2);
                l2Model = best?.ShortId ?? l1Model;
            }
            var l2ModelInfo = primaryProvider.Models.FirstOrDefault(m => m.ShortId == l2Model);
            var l2EnableThink = l2Cfg?.EnableThinking ?? l2ModelInfo?.Reasoning == true;
            var l2ThoughtInContent = l2Cfg?.ThoughtInContent ?? false;
            var l2Client = primaryProvider.ApiFormat == ApiFormat.Anthropic
                ? AnthropicChatClientFactory.Create(l2Model, apiKey)
                : OpenAIChatClientFactory.Create(endpoint, l2Model, apiKey);
            if (l2EnableThink)
                l2Client = new ThinkingChatClient(l2Client, true, l2ThoughtInContent);
            router.Register("l2", l2Client);
            logger?.LogInformation("L2: {Provider}/{Model}{Think}", primaryProvider.Name, l2Model, l2EnableThink ? " (thinking)" : "");

            // L3
            var l3Cfg = opts.AI.L3;
            string? l3Model = !string.IsNullOrEmpty(l3Cfg?.Model) ? l3Cfg.Model : null;
            if (l3Model == null) l3Model = ModelAutoSelectHostedService.LatestResult?.L3;
            if (l3Model == null)
            {
                var scoring = sp.GetRequiredService<ModelScoringEngine>();
                var (best, _) = scoring.SelectBestPair(primaryProvider.Models, ModelTierRequirements.L3);
                l3Model = best?.ShortId ?? l1Model;
            }
            var l3ModelInfo = primaryProvider.Models.FirstOrDefault(m => m.ShortId == l3Model);
            var l3EnableThink = l3Cfg?.EnableThinking ?? l3ModelInfo?.Reasoning == true;
            var l3ThoughtInContent = l3Cfg?.ThoughtInContent ?? false;
            var l3Client = primaryProvider.ApiFormat == ApiFormat.Anthropic
                ? AnthropicChatClientFactory.Create(l3Model, apiKey)
                : OpenAIChatClientFactory.Create(endpoint, l3Model, apiKey);
            if (l3EnableThink)
                l3Client = new ThinkingChatClient(l3Client, true, l3ThoughtInContent);
            router.Register("l3", l3Client);
            logger?.LogInformation("L3: {Provider}/{Model}{Think}{Reuse}", primaryProvider.Name, l3Model,
                l3EnableThink ? " (thinking)" : "", l3Model == l1Model ? " (reuses L1)" : "");

            return router;
        });

        services.AddKeyedSingleton<IChatClient>("l3", (sp, _) =>
        {
            var router = sp.GetRequiredService<MultiProviderChatClient>();
            return router.GetL3Client();
        });

        services.AddKeyedSingleton<IChatClient>("l2", (sp, _) =>
        {
            var router = sp.GetRequiredService<MultiProviderChatClient>();
            return router.GetL2Client();
        });

        services.AddSingleton<IChatClient>(sp =>
        {
            var router = sp.GetRequiredService<MultiProviderChatClient>();
            var opts = sp.GetRequiredService<IOptions<LTAIOptions>>().Value;

            if (opts.AI.SkipSafetyChecks)
            {
                var env = sp.GetService<IHostEnvironment>();
                if (env?.IsDevelopment() != true)
                {
                    var warnLog = sp.GetService<ILogger<MultiProviderChatClient>>();
                    warnLog?.LogWarning("SkipSafetyChecks=true in non-Development environment — safety wrapper bypassed");
                }
                return router;
            }

            var logger = sp.GetService<ILogger<LTAI.Core.Safety.SafeChatClient>>();
            var registry = sp.GetRequiredService<ProviderRegistry>();
            var primaryProvider = registry.ActiveProviders.FirstOrDefault();
            var safetyModel = !string.IsNullOrEmpty(opts.AI.Model)
                ? opts.AI.Model
                : opts.AI.L1?.Model ?? primaryProvider?.Models.FirstOrDefault()?.ShortId;

            if (string.IsNullOrEmpty(safetyModel))
            {
                logger?.LogWarning("SafeChatClient: no safety model found, skipping safety wrapper");
                return router;
            }

            var safetyKey = opts.AI.ApiKeyEnv != null
                ? SecretManager.Get(opts.AI.ApiKeyEnv) ?? ""
                : primaryProvider != null ? SecretManager.Get(primaryProvider.EnvVar) ?? "" : "";
            if (string.IsNullOrEmpty(safetyKey))
            {
                logger?.LogWarning("SafeChatClient: no API key configured, skipping safety wrapper");
                return router;
            }

            var safetyEndpoint = primaryProvider?.Endpoint ?? "https://api.deepseek.com/v1";
            IChatClient safetyClient = OpenAIChatClientFactory.Create(safetyEndpoint, safetyModel, safetyKey);
            var wrapped = new LTAI.Core.Safety.SafeChatClient(router, safetyClient, logger);
            return new MetricsChatClient(wrapped, sp.GetService<ILogger<MetricsChatClient>>());
        });

        // Local ONNX embedder
        services.AddSingleton<LocalEmbedder>(sp =>
        {
            var embedOpts = sp.GetService<IOptions<LTAIOptions>>()?.Value.Embedding;
            if (embedOpts != null)
            {
                LocalEmbedder.Options = new EmbeddingOptions
                {
                    Gpu = embedOpts.Gpu,
                    Quantization = embedOpts.Quantization,
                    DeviceId = embedOpts.DeviceId,
                    Models = new Dictionary<string, string>(embedOpts.Models, StringComparer.OrdinalIgnoreCase),
                };
            }
            return new LocalEmbedder();
        });

        services.AddHostedService<EpProbeService>();

        services.AddSingleton<EmbeddingClient>(sp =>
            new EmbeddingClient(sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetService<LocalEmbedder>(),
                sp.GetService<ILogger<EmbeddingClient>>(),
                sp.GetService<RemoteEmbeddingCache>(),
                sp.GetService<Glove50Embedder>()));

        services.AddSingleton<RemoteEmbeddingCache>(sp =>
            new RemoteEmbeddingCache(
                ttl: TimeSpan.FromHours(24),
                logger: sp.GetService<ILogger<RemoteEmbeddingCache>>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RemoteEmbeddingCache>.Instance));

        services.AddSingleton<ToolEmbeddingCache>(sp =>
            new ToolEmbeddingCache(
                sp.GetRequiredService<EmbeddingClient>(),
                sp.GetService<ILogger<ToolEmbeddingCache>>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ToolEmbeddingCache>.Instance));

        services.AddHostedService(sp => new PreWarmEmbeddingModelsHostedService(
            sp.GetRequiredService<IOptions<LTAIOptions>>(),
            sp.GetService<ILogger<PreWarmEmbeddingModelsHostedService>>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PreWarmEmbeddingModelsHostedService>.Instance,
            sp.GetService<IHttpClientFactory>()));

        return services;
    }
}
