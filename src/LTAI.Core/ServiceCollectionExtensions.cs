using LTAI.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace LTAI.Core;

/// <summary>
/// DI registration extensions for the LTAI.Core library.
/// Registers: HttpClient factory, LTAIOptions validation, OpenTelemetry tracing/metrics.
/// <b>Callers:</b> Desktop/Program.cs, TUI/Program.cs (top-level host setup).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register Core services:
    ///   1. IHttpClientFactory (via AddHttpClient)
    ///   2. LTAIOptionsValidator as IValidateOptions&lt;LTAIOptions&gt; (startup validation)
    ///   3. OpenTelemetry tracing/metrics with ASP.NET Core + HttpClient instrumentation
    ///      and "LTAI.*" / "Microsoft.Agents.AI.*" activity sources
    /// </summary>
    /// <param name="enableOpenTelemetry">Set false in unit tests to avoid telemetry initialization overhead.</param>
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
