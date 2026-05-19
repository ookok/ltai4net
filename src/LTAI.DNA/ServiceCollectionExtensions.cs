using LTAI.DNA.Consciousness;
using LTAI.DNA.Evolution;
using LTAI.DNA.Life;
using LTAI.DNA.Meta;
using LTAI.DNA.Safety;
using Microsoft.Extensions.DependencyInjection;

namespace LTAI.DNA;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAIDNA(this IServiceCollection services)
    {
        services.AddSingleton<DualConsciousness>();
        services.AddSingleton<EvolutionDriver>();
        services.AddSingleton<SwarmEvolution>();
        services.AddSingleton<SafetyCoordinator>();
        services.AddSingleton<LifeEngine>();

        services.AddSingleton<SelfEvolution>();
        services.AddSingleton<WorldModel>();
        services.AddSingleton<PredictiveEngine>();
        services.AddSingleton<MentalTimeTravel>();
        services.AddSingleton<ForesightGovernance>();
        services.AddSingleton<EntropyDrive>();
        services.AddSingleton<FocusDilution>();
        services.AddSingleton<GodelianSelf>();

        services.AddSingleton<PhenomenalConsciousness>();
        services.AddSingleton<ConsciousnessEmergence>();
        services.AddSingleton<SheshaHeads>();
        services.AddSingleton<PlayEngine>();
        services.AddSingleton<MultiStreamEngine>();
        services.AddSingleton<SurpriseGatedMemory>();
        services.AddSingleton<MetaMemory>();
        services.AddSingleton<MetaOptimizer>();
        services.AddSingleton<MetaStrategy>();
        services.AddSingleton<MetaStrategyEngine>();
        services.AddSingleton<LivingCompiler>();
        services.AddSingleton<RLVRMonitor>();

        services.AddSingleton<HormoneNetwork>();
        services.AddSingleton<BiorhythmEngine>();
        services.AddSingleton<ImmuneDefense>();
        services.AddSingleton<IdentityNarrative>();
        services.AddSingleton<Personality>();
        services.AddSingleton<ContextEngineer>();
        services.AddSingleton<LocalIntelligence>();
        services.AddSingleton<LivingPresence>();

        services.AddSingleton<DNAOrchestrator>();
        return services;
    }
}
