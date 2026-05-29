using LTAI.DNA.Regulation;
using LTAI.DNA.Safety;
using Microsoft.Extensions.DependencyInjection;

namespace LTAI.DNA;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAIDNA(this IServiceCollection services)
    {
        // Core modules (retained)
        services.AddSingleton<SafetyCoordinator>();
        services.AddSingleton<UnifiedSafetyGate>();
        services.AddSingleton<PolicyAsCode>();
        services.AddSingleton<SelfEvolution>();
        services.AddSingleton<WorldModel>();
        services.AddSingleton<PredictiveEngine>();
        services.AddSingleton<MentalTimeTravel>();
        services.AddSingleton<RLVRMonitor>();
        services.AddSingleton<DNAOrchestrator>();
        services.AddSingleton<RegulationVersionStore>();
        services.AddSingleton<IRegulationProvider>(sp => sp.GetRequiredService<RegulationVersionStore>());
        return services;
    }
}
