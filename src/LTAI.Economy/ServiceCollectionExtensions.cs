using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;

namespace LTAI.Economy;

public static class EconomyServiceCollectionExtensions
{
    public static IServiceCollection AddLTAIEconomy(this IServiceCollection services)
    {
        services.AddSingleton<HardwareProfiler>();
        services.AddSingleton<PromptPool>();

        services.AddSingleton<TieredEvaluator>(sp =>
            new TieredEvaluator(sp.GetRequiredService<HardwareProfiler>()));

        services.AddSingleton<EvolutionEngine>(sp =>
            new EvolutionEngine(
                new EvolutionConfig(),
                sp.GetRequiredService<PromptPool>(),
                sp.GetRequiredService<TieredEvaluator>(),
                sp.GetRequiredService<IChatClient>()));

        services.AddSingleton<TraceEfficiencyReward>();
        services.AddSingleton<CostAwareEvaluator>(sp =>
            new CostAwareEvaluator(sp.GetRequiredService<TraceEfficiencyReward>()));

        services.AddSingleton<EconomicOrchestrator>();
        services.AddSingleton<InverseRewardModel>();
        services.AddSingleton<MetabolismEngine>();

        return services;
    }
}
