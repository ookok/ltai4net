using LTAI.Network.Acceleration;
using LTAI.Network.Bridge;
using LTAI.Network.Consensus;
using LTAI.Network.Discovery;
using LTAI.Network.Infrastructure;
using LTAI.Network.Interfaces;
using LTAI.Network.Links;
using LTAI.Network.Messaging;
using LTAI.Network.Perception;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LTAI.Network;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAINetwork(this IServiceCollection services)
    {
        services.AddSingleton<IP2PNode, P2PNode>();
        services.AddSingleton<ServiceDiscovery>();
        services.AddSingleton<SmartDnsResolver>();

        services.AddMassTransit(x =>
        {
            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host("localhost", "/", h =>
                {
                    h.Username("guest");
                    h.Password("guest");
                });

                cfg.Message<LTAINetworkMessage>(m => m.SetEntityName("ltai-network"));
                cfg.ConfigureEndpoints(context);
            });
        });

        services.AddSingleton<IMessageBus, MassTransitMessageBus>();

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
