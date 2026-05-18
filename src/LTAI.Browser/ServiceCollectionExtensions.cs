using LTAI.Browser.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace LTAI.Browser;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAIBrowser(this IServiceCollection services)
    {
        services.AddSingleton<IBrowserAgent, BrowserAgent>();
        return services;
    }
}
