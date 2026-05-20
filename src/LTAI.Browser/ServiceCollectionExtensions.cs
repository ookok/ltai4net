using LTAI.Browser.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace LTAI.Browser;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAIBrowser(this IServiceCollection services)
    {
        services.AddSingleton<StealthBrowserConfig>(sp =>
        {
            var config = new StealthBrowserConfig();
            return config;
        });

        services.AddSingleton<StealthBrowserAdapter>(sp =>
        {
            var config = sp.GetRequiredService<StealthBrowserConfig>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<StealthBrowserAdapter>>();
            return new StealthBrowserAdapter(config, logger);
        });

        services.AddSingleton<TlSFingerprintConfig>();
        services.AddSingleton<IBrowserAgent, PlaywrightBrowserAgent>();
        services.AddSingleton<PlaywrightBrowserAgent>();
        return services;
    }
}
