using LTAI.AI.Governors;
using LTAI.AI.Providers;
using LTAI.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace LTAI.AI;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAIAI(this IServiceCollection services)
    {
        services.AddSingleton<IProviderEngine, ProviderEngine>();

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
