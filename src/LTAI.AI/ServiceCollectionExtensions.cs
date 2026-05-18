using LTAI.AI.Governors;
using LTAI.AI.Providers;
using LTAI.Core.Interfaces;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LTAI.AI;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAIAI(this IServiceCollection services)
    {
        services.AddSingleton<ProviderEngine>();
        services.AddSingleton<IProviderEngine>(sp => sp.GetRequiredService<ProviderEngine>());

        services.AddSingleton<IChatClient>(sp =>
        {
            var engine = sp.GetRequiredService<ProviderEngine>();
            return new ChatClientBuilder(engine)
                .UseLogging(sp.GetRequiredService<ILoggerFactory>())
                .UseFunctionInvocation()
                .UseOpenTelemetry()
                .UseDistributedCache()
                .Build();
        });

        services.AddSingleton<InputGovernor>();
        services.AddSingleton<ContextGovernor>();
        services.AddSingleton<RoutingGovernor>();
        services.AddSingleton<CapabilityGovernor>();
        services.AddSingleton<StorageGovernor>();
        services.AddSingleton<OutputGovernor>();
        services.AddSingleton<CommunicationGovernor>();
        services.AddSingleton<TaskGovernor>();
        services.AddSingleton<SelfGovernor>();
        services.AddSingleton<EvolutionGovernor>();
        services.AddSingleton<SystemGuardian>();
        services.AddSingleton<LivingTreeSystem>();

        return services;
    }
}
