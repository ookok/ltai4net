using LTAI.Core.Caching;
using LTAI.Core.Configuration;
using LTAI.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace LTAI.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAICore(this IServiceCollection services,
        bool enableOpenTelemetry = true, bool enableAspNetCoreInstrumentation = false)
    {
        services.AddHttpClient();
        services.AddHttpClient("safety", client => { client.Timeout = TimeSpan.FromSeconds(15); });

        // Validate LTAIOptions at startup (catches misconfiguration early)
        services.AddOptions<LTAIOptions>().ValidateOnStart();

        // First Scoped service: per-request chat scope.
        // Establishes the Scoped pattern for future per-request state.
        services.AddScoped<ChatScope>();

        // ConfigHotReload — watches appsettings.json, triggers IOptionsMonitor.OnChange
        services.AddSingleton<ConfigHotReloadService>();
        services.AddHostedService(sp => sp.GetRequiredService<ConfigHotReloadService>());

        // Unified caching infrastructure
        services.AddLTAICache();

        // Unified database manager — single registry for all SQLite databases
        services.AddSingleton<UnifiedDbManager>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<LTAIOptions>>().Value;
            var logger = sp.GetService<ILogger<UnifiedDbManager>>();
            var mgr = new UnifiedDbManager(logger);
            mgr.Register("kg", opts.ResolveDataPath("kg.db"));
            mgr.Register("cg", opts.ResolveDataPath("cg.db"));
            mgr.Register("deltas", opts.ResolveDataPath("deltas.db"));
            mgr.Register("circuit_breaker", opts.ResolveDataPath("circuit_breaker.db"));
            mgr.Register("refs_search", opts.ResolveDataPath("refs_search.db"));
            return mgr;
        });

        if (enableOpenTelemetry)
        {
            services.AddOpenTelemetry()
                .WithTracing(tracing =>
                {
                    if (enableAspNetCoreInstrumentation)
                        tracing.AddAspNetCoreInstrumentation();
                    tracing
                        .AddHttpClientInstrumentation()
                        .AddSource("LTAI.*")
                        .AddSource("Microsoft.Agents.AI.*");
                })
                .WithMetrics(metrics =>
                {
                    if (enableAspNetCoreInstrumentation)
                        metrics.AddAspNetCoreInstrumentation();
                    metrics.AddHttpClientInstrumentation();
                });
        }

        return services;
    }
}
