using LTAI.Infra.Browser.Interfaces;
using LTAI.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LTAI.Infra.Browser;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAIBrowser(this IServiceCollection services)
    {
        // 注册 StealthBrowserAdapter，使用 LTAIOptions 中的配置
        services.AddSingleton<StealthBrowserAdapter>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<LTAIOptions>>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<StealthBrowserAdapter>>();
            return new StealthBrowserAdapter(options, logger);
        });

        // 注册 TLS 指纹配置
        services.AddSingleton<TlSFingerprintConfig>();

        // 注册 PlaywrightBrowserAgent，注入 StealthBrowserAdapter 和 StealthBrowserConfig
        services.AddSingleton<IBrowserAgent, PlaywrightBrowserAgent>(sp =>
        {
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PlaywrightBrowserAgent>>();
            var stealthAdapter = sp.GetService<StealthBrowserAdapter>();
            var options = sp.GetRequiredService<IOptions<LTAIOptions>>();
            return new PlaywrightBrowserAgent(logger, stealthAdapter, options.Value.StealthBrowser);
        });

        return services;
    }
}
