using Microsoft.Extensions.DependencyInjection;

namespace LTAI.Sandbox;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAISandbox(this IServiceCollection services)
    {
        services.AddSingleton<ProcessSandbox>();
        services.AddSingleton<DockerSandbox>();
        services.AddSingleton<ISandbox>(sp => sp.GetRequiredService<ProcessSandbox>());
        services.AddSingleton<ISandbox>(sp => sp.GetRequiredService<DockerSandbox>());
        services.AddSingleton<SandboxOrchestrator>();
        return services;
    }
}
