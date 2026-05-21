using LTAI.Browser.Interfaces;
using LTAI.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LTAI.Browser;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAIBrowser(this IServiceCollection services)
    {
        services.AddSingleton<StealthBrowserAdapter>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<LTAIOptions>>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<StealthBrowserAdapter>>();
            return new StealthBrowserAdapter(options, logger);
        });

        services.AddSingleton<TlSFingerprintConfig>();
        services.AddSingleton<IBrowserAgent, PlaywrightBrowserAgent>();
        services.AddSingleton<PlaywrightBrowserAgent>();
        return services;
    }
}
