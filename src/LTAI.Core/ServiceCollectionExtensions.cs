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
        bool enableOpenTelemetry = true, bool enableAspNetCoreInstrumentation = false)
    {
        services.AddHttpClient();
        services.AddHttpClient("safety", client => { client.Timeout = TimeSpan.FromSeconds(15); });

        // Validate LTAIOptions at startup (catches misconfiguration early)
        services.AddSingleton<IValidateOptions<LTAIOptions>, LTAIOptionsValidator>();
        services.AddOptions<LTAIOptions>().ValidateOnStart();

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
