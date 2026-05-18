using LTAI.Core.Execution;
using LTAI.Core.Interfaces;
using LTAI.Core.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace LTAI.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAICore(this IServiceCollection services)
    {
        services.AddSingleton<ICognitiveMesh, CognitiveMesh>();
        services.AddSingleton<IToolRegistry, ToolRegistry>();
        services.AddSingleton<TaskJournal>();
        return services;
    }
}
