using LTAI.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

        // ConfigHotReload — watches appsettings.json, triggers IOptionsMonitor.OnChange
        services.AddSingleton<ConfigHotReloadService>();
        services.AddHostedService(sp => sp.GetRequiredService<ConfigHotReloadService>());

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
