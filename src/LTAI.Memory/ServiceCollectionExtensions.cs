using Microsoft.Extensions.DependencyInjection;

namespace LTAI.Memory;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAIMemory(this IServiceCollection services)
    {
        services.AddSingleton<EmotionalMemoryStore>();
        services.AddSingleton<PersonaMemory>();
        services.AddSingleton<UserModel>();
        services.AddSingleton<MemPOOptimizer>();
        services.AddSingleton<MemoryOrchestrator>();
        services.AddSingleton<UserTraitEvolutionTree>();
        return services;
    }
}
