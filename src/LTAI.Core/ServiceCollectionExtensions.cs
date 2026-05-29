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
        bool enableOpenTelemetry = true)
    {
        services.AddHttpClient();

        // Validate LTAIOptions at startup (catches misconfiguration early)
        services.AddSingleton<IValidateOptions<LTAIOptions>, LTAIOptionsValidator>();

        if (enableOpenTelemetry)
        {
            services.AddOpenTelemetry()
                .WithTracing(tracing => tracing
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddSource("LTAI.*")
                    .AddSource("Microsoft.Agents.AI.*")
                )
                .WithMetrics(metrics => metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                );
        }

        return services;
    }
}
