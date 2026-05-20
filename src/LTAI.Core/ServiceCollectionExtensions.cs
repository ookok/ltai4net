using LTAI.Core.Acceleration;
using LTAI.Core.Configuration;
using LTAI.Core.Execution;
using LTAI.Core.Interfaces;
using LTAI.Core.Life;
using LTAI.Core.Messaging;
using LTAI.Core.Prefs;
using LTAI.Core.Resilience;
using LTAI.Core.System;
using Microsoft.Extensions.DependencyInjection;

namespace LTAI.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAICore(this IServiceCollection services)
    {
        services.AddSingleton<IProviderRegistry, ProviderRegistry>();

        services.AddSingleton<ICognitiveMesh, CognitiveMesh>();
        services.AddSingleton<IToolRegistry, ToolRegistry>();
        services.AddSingleton<TaskJournal>();

        services.AddSingleton(sp => HardwareAcceleration.Instance);
        services.AddSingleton(sp => ResponseCache.Instance);

        services.AddSingleton(sp => DigitalTwin.Instance);
        services.AddSingleton(sp => AutonomousGrowth.Instance);
        services.AddSingleton(sp => SynapticPlasticity.Instance);

        services.AddSingleton(sp => ResilienceBrain.Instance);
        services.AddSingleton(sp => SystemHealth.Instance);
        services.AddSingleton(sp => GreenScheduler.Instance);

        services.AddSingleton(sp => ShellEnv.Instance);
        services.AddSingleton(sp => PromptShield.Instance);
        services.AddSingleton(sp => ResourceTree.Instance);
        services.AddSingleton(sp => UniversalScanner.Instance);
        services.AddSingleton(sp => AtomicModification.Instance);
        services.AddSingleton(sp => AsyncDisk.Instance);
        services.AddSingleton(sp => ConcurrencyGuard.Instance);

        services.AddSingleton(sp => DpoPrefs.Instance);

        services.AddSingleton<ServiceManager>();
        services.AddSingleton<ModelManager>();

        return services;
    }
}
