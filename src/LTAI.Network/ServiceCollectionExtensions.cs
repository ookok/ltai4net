using LTAI.Core.Configuration;
using LTAI.Network.Acceleration;
using LTAI.Network.Bridge;
using LTAI.Network.Consensus;
using LTAI.Network.Discovery;
using LTAI.Network.Infrastructure;
using LTAI.Network.Interfaces;
using LTAI.Network.Links;
using LTAI.Network.Messaging;
using LTAI.Network.Perception;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.Network;

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

        services.AddSingleton(sp => new BiometricRegistry(sp.GetRequiredService<ILogger<BiometricRegistry>>()));
        services.AddSingleton(sp => new SpatialAwareness(sp.GetRequiredService<ILogger<SpatialAwareness>>()));
        services.AddSingleton(sp => new P2PPresence(sp.GetRequiredService<ILogger<P2PPresence>>()));
        services.AddSingleton(sp => new ReachGateway(sp.GetRequiredService<ILogger<ReachGateway>>()));
        services.AddSingleton(sp => new ChannelBridge(sp.GetRequiredService<ILogger<ChannelBridge>>()));
        services.AddSingleton(sp => new NetworkResilience(sp.GetRequiredService<ILogger<NetworkResilience>>()));
        services.AddSingleton(sp => new ExternalAccess(sp.GetRequiredService<ILogger<ExternalAccess>>()));

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
