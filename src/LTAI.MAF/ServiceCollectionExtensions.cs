using LTAI.MAF.Evolution;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.A2A;
using Microsoft.Extensions.DependencyInjection;

namespace LTAI.MAF;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAIMAF(this IServiceCollection services)
    {
        services.AddSingleton<LTAIAgent>();
        services.AddSingleton(sp =>
        {
            var rawAgent = sp.GetRequiredService<LTAIAgent>();
            return (AIAgent)rawAgent
                .WithLTAIGovernance(sp)
                .WithToolGovernance(sp);
        });
        services.AddSingleton<LTAIInputFilter>();
        services.AddSingleton<LTAIOutputFilter>();

        services.AddA2AServer("LTAI");

        services.AddSingleton<HarnessSnapshot>();
        services.AddSingleton<ExperienceDebugger>();
        services.AddSingleton<DecisionLog>();
        services.AddSingleton<PluginRegistry>();
        services.AddSingleton<LTAI.Vector.Knowledge.TokenSavingsTracker>();
        services.AddSingleton<HarnessEvolutionEngine>();

        return services;
    }
}
