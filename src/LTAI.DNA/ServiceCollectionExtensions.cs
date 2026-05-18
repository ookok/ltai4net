using LTAI.DNA.Consciousness;
using LTAI.DNA.Evolution;
using LTAI.DNA.Life;
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
        services.AddSingleton<DNAOrchestrator>();
        return services;
    }
}
