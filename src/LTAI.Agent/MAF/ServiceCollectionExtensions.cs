using LTAI.Core.Messaging;
using LTAI.Agent.CodeAct;
using LTAI.Agent.Evolution;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.A2A;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent;

public static class MAFServiceCollectionExtensions
{
    public static IServiceCollection AddLTAIMAF(this IServiceCollection services)
    {
        services.AddSingleton<LTAIAgent>();
        services.AddSingleton(sp =>
        {
            var rawAgent = sp.GetRequiredService<LTAIAgent>();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

            return rawAgent.AsBuilder()
                .WithToolGovernance(sp)
                .WithLTAIGovernance(sp)
                .UseLogging(loggerFactory)
                .UseOpenTelemetry("LTAI")
                .Build();
        });
        services.AddSingleton<LogiInputFilter>();
        var outputFilterType = typeof(LogiOutputFilter);
        services.AddSingleton(outputFilterType);


        services.AddSingleton<CodeActProvider>(sp =>
        {
            var config = LTAICodeActIntegration.CreateDefaultConfig();
            return new CodeActProvider(config);
        });

        services.AddA2AServer("LTAI");

        services.AddSingleton<HarnessSnapshot>();
        services.AddSingleton<ExperienceDebugger>();
        services.AddSingleton<DecisionLog>();
        services.AddSingleton<PluginRegistry>();
        services.AddSingleton<LTAI.Knowledge.Core.TokenSavingsTracker>();
        services.AddSingleton<HarnessEvolutionEngine>();

        return services;
    }

    public static async Task RegisterCodeActToolsAsync(this IServiceProvider sp, AIToolRegistry registry)
    {
        var codeAct = sp.GetService<CodeActProvider>();
        if (codeAct?.IsAvailable != true) return;

        var loggerFactory = sp.GetService<ILoggerFactory>();
        var logger = loggerFactory?.CreateLogger("LTAI.MAF.CodeAct");

        await registry.RegisterAsync("codeact_exec", async args =>
        {
            var code = args.TryGetValue("code", out var c) ? c?.ToString() ?? "" : "";
            try
            {
                var hyperlightFunction = codeAct.AsFunction();
                if (hyperlightFunction != null)
                {
                    return $"CodeAct micro-VM available. Code snippet of {code.Length} chars ready for execution.";
                }
                return "CodeAct hyperlight VM not available.";
            }
            catch (Exception ex)
            {
                return $"CodeAct error: {ex.Message}";
            }
        });

        logger?.LogInformation("CodeAct tool registered via Hyperlight");
    }
}
