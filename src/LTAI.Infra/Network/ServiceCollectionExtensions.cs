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
using LTAI.Models;
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

        services.AddSingleton<P2PNode>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<LTAIOptions>>();
            var logger = sp.GetRequiredService<ILogger<P2PNode>>();
            var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
            var queue = sp.GetRequiredService<PersistentMessageQueue>();
            var skillExchange = sp.GetService<ISkillExchangeProvider>();
            var node = new P2PNode(httpFactory, options, logger, queue);
            node.SkillExchangeProvider = skillExchange;
            return node;
        });
        services.AddSingleton<IP2PNode>(sp => sp.GetRequiredService<P2PNode>());
        services.AddSingleton<ServiceDiscovery>();
        services.AddSingleton<SmartDnsResolver>();

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

        services.AddSingleton<GossipDiscovery>(sp =>
        {
            var p2pNode = (P2PNode)sp.GetRequiredService<IP2PNode>();
            var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
            var logger = sp.GetRequiredService<ILogger<GossipDiscovery>>();
            var skillExchange = sp.GetService<ISkillExchangeProvider>();
            var gossipDiscovery = new GossipDiscovery(p2pNode, httpFactory, logger, skillExchange);

            p2pNode.GossipReceiver = request =>
            {
                var peers = request.Peers.Select(p => (
                    !string.IsNullOrEmpty(p.Id) ? p.Id : $"{p.Address}:{p.Port}",
                    p.Address,
                    p.Port
                )).ToList();
                gossipDiscovery.ReceiveGossip(request.From, peers);
            };

            return gossipDiscovery;
        });

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
