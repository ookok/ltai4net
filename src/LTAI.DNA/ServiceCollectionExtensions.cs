using LTAI.DNA.Consciousness;
using LTAI.DNA.Evolution;
using LTAI.DNA.Life;
using LTAI.DNA.Meta;
using LTAI.DNA.Regulation;
using LTAI.DNA.Safety;
using Microsoft.Extensions.DependencyInjection;

namespace LTAI.DNA;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAIDNA(this IServiceCollection services)
    {
        services.AddSingleton<DualConsciousness>();
        services.AddSingleton<SafetyCoordinator>();
        services.AddSingleton<UnifiedSafetyGate>();
        services.AddSingleton<PolicyAsCode>();
        services.AddSingleton<LifeEngine>();

        services.AddSingleton<SelfEvolution>();
        services.AddSingleton<WorldModel>();
        services.AddSingleton<PredictiveEngine>();
        services.AddSingleton<MentalTimeTravel>();

        services.AddSingleton<PhenomenalConsciousness>();
        services.AddSingleton<MultiStreamEngine>();
        services.AddSingleton<SurpriseGatedMemory>();
        services.AddSingleton<MetaMemory>();
        services.AddSingleton<MetaOptimizer>();
        services.AddSingleton<LivingCompiler>();
        services.AddSingleton<RLVRMonitor>();

        services.AddSingleton<IdentityNarrative>();
        services.AddSingleton<Personality>();
        services.AddSingleton<ContextEngineer>();
        services.AddSingleton<LocalIntelligence>();

        services.AddSingleton<DNAOrchestrator>();

        services.AddSingleton<RegulationVersionStore>();
        services.AddSingleton<IRegulationProvider>(sp => sp.GetRequiredService<RegulationVersionStore>());
        return services;
    }
}
