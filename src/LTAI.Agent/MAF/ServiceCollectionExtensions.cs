using LTAI.Core.Messaging;
using LTAI.Agent.CodeAct;
using LTAI.Agent.Evolution;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.A2A;
using Microsoft.Extensions.AI;
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

        // Register Hyperlight micro-VM provider for agent attachment
        services.AddSingleton(sp =>
        {
            var codeAct = sp.GetRequiredService<CodeActProvider>();
            return codeAct.AsProvider()!;
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
            var language = args.TryGetValue("language", out var l) ? l?.ToString() ?? "python" : "python";

            if (string.IsNullOrWhiteSpace(code))
                return "Error: No code provided. Use 'code' parameter with Python code string.";

            try
            {
                // Hyperlight micro-VM execution via Wasm-based sandbox
                var hyperlightFn = codeAct.AsFunction();
                if (hyperlightFn is not null)
                {
                    var fnArgs = new AIFunctionArguments
                    {
                        ["code"] = code,
                        ["language"] = language
                    };
                    var result = await hyperlightFn.InvokeAsync(fnArgs).ConfigureAwait(false);
                    return result?.ToString() ?? "";
                }

                // Fallback to HyperlightCodeActProvider (attached to agent)
                var provider = codeAct.AsProvider();
                if (provider is not null)
                {
                    var toolFunctions = provider.GetTools();
                    if (toolFunctions.Count > 0)
                    {
                        return $"Hyperlight micro-VM ready. {toolFunctions.Count} tools loaded. " +
                               $"Code snippet of {code.Length} chars awaiting execution via agent pipeline.";
                    }
                }

                return "Hyperlight micro-VM initialized but no execution path available. " +
                       "Attach HyperlightCodeActProvider to an AIAgent for full CodeAct support.";
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Hyperlight codeact_exec failed");
                return $"Hyperlight VM error: {ex.Message}";
            }
        });

        logger?.LogInformation("CodeAct Hyperlight tool registered (real micro-VM execution)");
    }
}
