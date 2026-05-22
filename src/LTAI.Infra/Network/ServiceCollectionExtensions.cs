using LTAI.Core.Configuration;
using LTAI.Infra.Network.Acceleration;
using LTAI.Infra.Network.Bridge;
using LTAI.Infra.Network.Consensus;
using LTAI.Infra.Network.Discovery;
using LTAI.Infra.Network.Infrastructure;
using LTAI.Infra.Network.Interfaces;
using LTAI.Infra.Network.Links;
using LTAI.Infra.Network.Messaging;
using LTAI.Infra.Network.Perception;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.Infra.Network;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAINetwork(this IServiceCollection services)
    {
        services.AddHttpClient("p2p");

        services.AddSingleton<PersistentMessageQueue>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<LTAIOptions>>();
            var logger = sp.GetRequiredService<ILogger<PersistentMessageQueue>>();
            var queuePath = options.Value.Network.QueuePath;
            return new PersistentMessageQueue(queuePath, logger);
        });

        services.AddSingleton<IP2PNode, P2PNode>();
        services.AddSingleton<ServiceDiscovery>();
        services.AddSingleton<SmartDnsResolver>();

        services.AddSingleton(sp => DistributedConsciousness.Instance);
        services.AddSingleton(sp => SwarmCoordinator.Instance);
        services.AddSingleton(sp => CollectiveConsciousness.Instance);
        services.AddSingleton(sp => NATTraverser.Instance);
        services.AddSingleton(sp => Reputation.Instance);
        services.AddSingleton(sp => DualMode.Instance);
        services.AddSingleton(sp => BiometricRegistry.Instance);
        services.AddSingleton(sp => SpatialAwareness.Instance);
        services.AddSingleton(sp => P2PPresence.Instance);
        services.AddSingleton(sp => ReachGateway.Instance);
        services.AddSingleton(sp => ChannelBridge.Instance);
        services.AddSingleton(sp => NetworkResilience.Instance);
        services.AddSingleton(sp => ExternalAccess.Instance);

        services.AddSingleton<Bridge.A2aP2pBridge>();

        return services;
    }

    public static IServiceCollection AddLTAINetworkMinimal(this IServiceCollection services)
    {
        services.AddSingleton<IP2PNode, P2PNode>();
        services.AddSingleton<ServiceDiscovery>();
        services.AddSingleton<SmartDnsResolver>();
        return services;
    }
}
