using LTAI.Core.Acceleration;
using LTAI.Core.Configuration;
using LTAI.Core.Execution;
using LTAI.Core.Interfaces;
using LTAI.Core.Life;
using LTAI.Core.Messaging;
using LTAI.Core.Multimodal;
using LTAI.Core.Network;
using LTAI.Core.Prefs;
using LTAI.Core.Resilience;
using LTAI.Core.System;
using LTAI.Core.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace LTAI.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAICore(this IServiceCollection services)
    {
        services.AddHttpClient();

        services.AddSingleton(sp => new HttpAccelerator(sp.GetRequiredService<IOptions<LTAIOptions>>().Value.HttpAccelerator));
        services.AddSingleton(sp => HttpAccelerator.CreateAcceleratedClient(
            sp.GetRequiredService<IOptions<LTAIOptions>>().Value.HttpAccelerator));

        services.AddSingleton(SecretVault.Instance);
        services.AddSingleton<DataPathResolver>();

        services.AddSingleton<IProviderRegistry, ProviderRegistry>();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<LTAIOptions>>().Value.Harness);
        services.AddSingleton<ICognitiveMesh, CognitiveMesh>();
        services.AddSingleton<AIToolRegistry>();
        services.AddSingleton<TaskJournal>();

        services.AddSingleton(sp => HardwareAcceleration.Instance);
        services.AddSingleton(sp => ResponseCache.Instance);

        services.AddSingleton(sp => DigitalTwin.Instance);
        services.AddSingleton(sp => AutonomousGrowth.Instance);
        services.AddSingleton(sp => SynapticPlasticity.Instance);

        services.AddSingleton(sp => ResilienceBrain.Instance);
        services.AddSingleton(sp => SystemHealth.Instance);

        services.AddSingleton(sp => ShellEnv.Instance);
        services.AddSingleton(sp => PromptShield.Instance);
        services.AddSingleton(sp => ResourceTree.Instance);
        services.AddSingleton(sp => UniversalScanner.Instance);
        services.AddSingleton(sp => AtomicModification.Instance);

        services.AddSingleton<SocialLoadModel>();
        services.AddSingleton(sp => DpoPrefs.Instance);

        services.AddSingleton<ServiceManager>();
        services.AddSingleton<ModelManager>();
        services.AddSingleton<DaemonManager>();
        services.AddSingleton<Wsl2Manager>();
        services.AddSingleton<ResourceGuard>();

        services.AddSingleton<HotPathObjectPool>();

        services.AddSingleton<IEventBusV2, EventBusV2>();
        services.AddSingleton<IEventBus, EventBus>();
        services.AddSingleton<ISessionStore, InMemorySessionStore>();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITextClassifier>(
            ClassificationRegistry.EndpointCategory));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITextClassifier>(
            ClassificationRegistry.Intent));

        return services;
    }
}
