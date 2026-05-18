using Microsoft.Extensions.DependencyInjection;

namespace LTAI.MAF;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAIMAF(this IServiceCollection services)
    {
        services.AddSingleton<LTAIAgent>();
        services.AddSingleton<LTAIInputFilter>();
        services.AddSingleton<LTAIOutputFilter>();
        services.AddSingleton<A2AHost>();
        return services;
    }
}
