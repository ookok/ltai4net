using LTAI.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LTAI.Infra.Sandbox;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAISandbox(this IServiceCollection services)
    {
        services.AddSingleton<ProcessSandbox>();
        services.AddSingleton<DockerSandbox>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISandbox>(
            sp => sp.GetRequiredService<ProcessSandbox>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISandbox>(
            sp => sp.GetRequiredService<DockerSandbox>()));
        services.AddSingleton<SandboxOrchestrator>();
        services.AddSingleton<ISandboxExecutor, SandboxExecutorAdapter>();
        return services;
    }
}
