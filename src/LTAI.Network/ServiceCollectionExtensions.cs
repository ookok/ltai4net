using LTAI.Network.Discovery;
using LTAI.Network.Infrastructure;
using LTAI.Network.Interfaces;
using LTAI.Network.Messaging;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;

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
