using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace LTAI.MAF;

public static class AgentToolExtensions
{
    public static AIFunction AsTool(this AIAgent agent, string? name = null, string? description = null)
    {
        return agent.AsAIFunction(new AIFunctionFactoryOptions
        {
            Name = name ?? $"agent_{agent.Name}",
            Description = description ?? $"Delegate to the '{agent.Name}' agent"
        });
    }

    public static void RegisterAgentTools(this IServiceCollection services)
    {
        services.AddKeyedSingleton("code", (sp, _) =>
            sp.GetRequiredKeyedService<AIAgent>("code").AsTool("code_agent", "Code analysis and generation agent"));
        services.AddKeyedSingleton("eia", (sp, _) =>
            sp.GetRequiredKeyedService<AIAgent>("eia").AsTool("eia_agent", "Environmental impact assessment agent"));
    }
}
