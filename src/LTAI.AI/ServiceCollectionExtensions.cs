using LTAI.AI.Governors;
using LTAI.AI.Interfaces;
using LTAI.AI.Providers;
using LTAI.Core.Configuration;
using LTAI.Core.Governors;
using LTAI.Core.Interfaces;
using LTAI.Core.Messaging;
using LTAI.Core.System;
using LTAI.Knowledge.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.AI;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAIAI(this IServiceCollection services)
    {
        // Core pipeline governors
        services.AddSingleton<InputGovernor>();
        services.AddSingleton<ContextGovernor>();
        services.AddSingleton<RoutingGovernor>();
        services.AddSingleton<OutputGovernor>();
        services.AddSingleton<SelfGovernor>();

        // LivingTree system
        services.AddSingleton<ILivingTreeSystem, LivingTreeSystem>();

        // Cross-run evolution store (needed by DebugObservability)
        services.AddSingleton<ICrossRunEvolutionStore, CrossRunEvolutionStore>();

        return services;
    }
}
