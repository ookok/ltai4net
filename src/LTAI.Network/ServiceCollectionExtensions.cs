using LTAI.Network.Discovery;
using LTAI.Network.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace LTAI.Network;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAINetwork(this IServiceCollection services)
    {
        services.AddSingleton<IP2PNode, P2PNode>();
        services.AddSingleton<ServiceDiscovery>();
        return services;
    }
}
