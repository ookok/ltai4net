using Microsoft.Extensions.DependencyInjection;

namespace LTAI.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAICore(this IServiceCollection services)
    {
        services.AddHttpClient();
        return services;
    }
}
