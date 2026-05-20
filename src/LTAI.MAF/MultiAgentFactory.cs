using LTAI.AI.Governors;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LTAI.MAF;

public static class MultiAgentFactory
{
    public static AIAgent CreateCodeAgent(IServiceProvider sp)
    {
        var raw = sp.GetRequiredService<LTAIAgent>();
        return raw.AsBuilder().Build();
    }

    public static AIAgent CreateEiaAgent(IServiceProvider sp)
    {
        var raw = sp.GetRequiredService<LTAIAgent>();
        return raw.AsBuilder().Build();
    }

    public static AIAgent CreateGeneralAgent(IServiceProvider sp)
    {
        return sp.GetRequiredService<AIAgent>();
    }

    public static void RegisterSpecializedAgents(this IServiceCollection services)
    {
        services.AddKeyedSingleton("code", (sp, _) => CreateCodeAgent(sp));
        services.AddKeyedSingleton("eia", (sp, _) => CreateEiaAgent(sp));
        services.AddKeyedSingleton("general", (sp, _) => CreateGeneralAgent(sp));

        services.AddA2AServer("code");
        services.AddA2AServer("eia");
    }

    public static void MapSpecializedA2AEndpoints(this Microsoft.AspNetCore.Routing.IEndpointRouteBuilder app)
    {
        app.MapA2AHttpJson("code", "/a2a/code");
        app.MapA2AHttpJson("eia", "/a2a/eia");
    }
}
