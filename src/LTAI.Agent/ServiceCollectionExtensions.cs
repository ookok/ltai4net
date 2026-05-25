using LTAI.DNA.Safety;
using LTAI.Models;
using HarnessProfile = LTAI.Core.Configuration.HarnessProfile;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LTAI.Agent.Agents;
using LTAI.Agent.Feedback;
using LTAI.Agent.Federation;
using LTAI.Agent.Middleware;
using LTAI.Agent.Prefetch;
using LTAI.Agent.Routing;
using LTAI.Agent.Tools;
using LTAI.Agent.Workflows;

namespace LTAI.Agent;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAIAgent(this IServiceCollection services)
    {
        services.AddSingleton<SkillRegistry>();
        services.AddSingleton<IntentRouter>();
        services.AddSingleton<UnifiedSemanticRouter>();
        services.AddSingleton<UnifiedIntentRouter>();
        services.AddSingleton<UniversalOrchestrator>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<UniversalOrchestrator>>();
            var router = sp.GetRequiredService<UnifiedSemanticRouter>();
            var harness = sp.GetService<HarnessProfile>();
            return new UniversalOrchestrator(logger, router, harness);
        });
        services.AddSingleton<ToolRetriever>();
        services.AddSingleton<PlannerCriticWorkflow>();
        services.AddSingleton<PlannerIntegration>();
        services.AddSingleton<UnifiedPlanningPipeline>();
        services.AddSingleton<AgentParliament>();
        services.AddSingleton<SentientParliament>();
        services.AddSingleton<ShadowRouter>();
        services.AddSingleton<HumanInTheLoopReview>();
        services.AddSingleton<FeedbackCollector>();
        services.AddSingleton<ABExperimentEngine>();
        services.AddSingleton<FederationCoordinator>();
        services.AddSingleton<BudgetTrackingMiddleware>();
        services.AddSingleton<AgentRegistry>();
        services.AddSingleton<AgentRegistryLock>();
        services.AddSingleton<IAgentFactory, AgentFactory>();
        services.AddSingleton<PredictivePrefetcher>();
        services.AddHostedService<ReflectiveIdlingService>();

        return services;
    }

    public static IServiceCollection AddLTAIAgentsFromYaml(this IServiceCollection services, string yamlPath)
    {
        var yaml = File.ReadAllText(yamlPath);
        var config = ParseYamlConfig(yaml);
        services.AddSingleton(config);

        foreach (var card in config.Agents)
        {
            services.AddKeyedScoped(card.Name, (sp, _) => CreateAgent(sp, card));
        }

        return services;
    }

    private static AIAgent CreateAgent(IServiceProvider sp, LTAIAgentCard card)
    {
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var chatClient = sp.GetRequiredService<IChatClient>();
        var skillRegistry = sp.GetRequiredService<SkillRegistry>();

        AIAgent agent = card.Type switch
        {
            AgentType.Chat => new ChatAgent(card, chatClient, skillRegistry,
                loggerFactory.CreateLogger<ChatAgent>()),
            AgentType.Code => new CodeAgent(card, chatClient, skillRegistry,
                loggerFactory.CreateLogger<CodeAgent>()),
            AgentType.EIA => new EIAAgent(card, chatClient, skillRegistry,
                loggerFactory.CreateLogger<EIAAgent>()),
            AgentType.Reasoning => new ReasoningAgent(card, chatClient, skillRegistry,
                loggerFactory.CreateLogger<ReasoningAgent>()),
            _ => new ChatAgent(card, chatClient, skillRegistry,
                loggerFactory.CreateLogger<ChatAgent>())
        };

        return ApplyMiddleware(agent, card, sp);
    }

    private static AIAgent ApplyMiddleware(AIAgent agent, LTAIAgentCard card, IServiceProvider sp)
    {
        var builder = agent.AsBuilder();

        foreach (var mwName in card.Middleware)
        {
            switch (mwName)
            {
                case "unified_safety":
                    var safetyGate = sp.GetRequiredService<UnifiedSafetyGate>();
                    builder.Use(async (messages, session, options, inner, ct) =>
                    {
                        var msgList = messages.ToList();
                        var userMsg = msgList.LastOrDefault(m => m.Role == ChatRole.User);
                        var sessionId = (session as LTAIAgentSession)?.SessionId ?? "anon";

                        var gateVerdict = await safetyGate.EvaluateInputAsync(
                            userMsg?.Text ?? "", sessionId, ct);

                        if (!gateVerdict.IsAllowed)
                            return new AgentResponse(new ChatMessage(ChatRole.Assistant,
                                $"[Safety] {gateVerdict.Reason}"));

                        if (gateVerdict.Action == GateAction.Warn)
                        {
                            var logger = sp.GetRequiredService<ILoggerFactory>()
                                .CreateLogger("UnifiedSafety");
                            logger.LogWarning("Safety warning for {Agent}: {Reason}", card.Name, gateVerdict.Reason);
                        }

                        var response = await inner.RunAsync(messages, session, options, ct).ConfigureAwait(false);

                        if (response.Text is not null)
                        {
                            var outputVerdict = await safetyGate.EvaluateOutputAsync(
                                response.Text, sessionId, ct).ConfigureAwait(false);
                            if (!outputVerdict.IsAllowed)
                                return new AgentResponse(new ChatMessage(ChatRole.Assistant,
                                    $"[Safety] Output filtered: {outputVerdict.Reason}"));
                        }

                        return response;
                    }, null);
                    break;
                case "budget_tracking":
                    var budgetTracking = sp.GetRequiredService<BudgetTrackingMiddleware>();
                    builder.Use(budgetTracking.InvokeAsync, null);
                    break;
            }
        }

        return builder.Build();
    }

    private static AgentConfig ParseYamlConfig(string yaml)
    {
        var config = new AgentConfig();
        var lines = yaml.Split('\n');
        LTAIAgentCard? currentAgent = null;
        string currentSection = "";
        var currentTools = new List<string>();
        var currentMiddleware = new List<string>();
        var currentOptions = new Dictionary<string, object?>();
        var instructionsBuilder = new System.Text.StringBuilder();
        bool inInstructions = false;
        bool inTools = false;
        bool inMiddleware = false;

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            if (trimmed.StartsWith("global:")) { currentSection = "global"; continue; }
            if (trimmed.StartsWith("agents:")) { currentSection = "agents"; continue; }

            if (trimmed.StartsWith("- name:") && currentSection == "agents")
            {
                FinalizeCurrentAgent();
                currentAgent = new LTAIAgentCard { Name = trimmed[8..].Trim() };
                currentTools.Clear();
                currentMiddleware.Clear();
                currentOptions.Clear();
                inInstructions = false;
                inTools = false;
                inMiddleware = false;
                instructionsBuilder.Clear();
                continue;
            }

            if (currentAgent is null) continue;
            var t = trimmed.TrimStart();

            if (t.StartsWith("type:"))
            {
                inInstructions = inTools = inMiddleware = false;
                currentAgent.Type = t[5..].Trim() switch
                {
                    "code_agent" => AgentType.Code,
                    "eia_agent" => AgentType.EIA,
                    "reasoning_agent" => AgentType.Reasoning,
                    _ => AgentType.Chat
                };
            }
            else if (t.StartsWith("model:"))
            {
                inInstructions = inTools = inMiddleware = false;
                currentAgent.Model = t[6..].Trim();
            }
            else if (t.StartsWith("instructions:") && t.Length > 15 && t[14] == '|')
            {
                inInstructions = true; inTools = false; inMiddleware = false;
            }
            else if (t.StartsWith("middleware:"))
            {
                inInstructions = false; inTools = false; inMiddleware = true;
            }
            else if (t.StartsWith("tools:"))
            {
                inInstructions = false; inTools = true; inMiddleware = false;
            }
            else if (t.StartsWith("options:"))
            {
                inInstructions = inTools = inMiddleware = false;
            }
            else if (inInstructions && (t.StartsWith("- ") || !t.Contains(":")))
            {
                var content = t.StartsWith("- ") ? t[2..] : t;
                instructionsBuilder.AppendLine(content);
            }
            else if (inTools && t.StartsWith("- "))
            {
                currentTools.Add(t[2..].Trim());
            }
            else if (inMiddleware && t.StartsWith("- "))
            {
                currentMiddleware.Add(t[2..].Trim());
            }
            else if (!inInstructions && !inTools && !inMiddleware && t.Contains(":"))
            {
                var colonIdx = t.IndexOf(':');
                var key = t[..colonIdx].Trim();
                var val = t[(colonIdx + 1)..].Trim();
                if (double.TryParse(val, out var dv)) currentOptions[key] = dv;
                else if (int.TryParse(val, out var iv)) currentOptions[key] = iv;
                else currentOptions[key] = val;
            }
        }

        FinalizeCurrentAgent();

        void FinalizeCurrentAgent()
        {
            if (currentAgent is null) return;
            currentAgent.Instructions = instructionsBuilder.ToString().Trim();
            currentAgent.Tools = new List<string>(currentTools);
            currentAgent.Middleware = new List<string>(currentMiddleware);
            currentAgent.Options = new Dictionary<string, object?>(currentOptions);
            config.Agents.Add(currentAgent);
        }

        return config;
    }

    public static void WirePlanningPipeline(IServiceProvider sp)
    {
        var plannerCritic = sp.GetRequiredService<PlannerCriticWorkflow>();
        var factory = sp.GetRequiredService<AgentFactory>();
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("LTAI.Agent");

        // Register default planner-critic-executor triads per domain
        foreach (var domain in new[] { "code", "eia", "general" })
        {
            try
            {
                var agent = factory.GetOrCreate(domain);
                var planner = factory.GetOrCreate($"{domain}_planner");
                var critic = factory.GetOrCreate($"{domain}_critic");

                plannerCritic.RegisterAgent(domain, agent);
                plannerCritic.RegisterAgent($"{domain}_planner", planner);
                plannerCritic.RegisterAgent($"{domain}_critic", critic);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to register planner-critic pair for domain '{Domain}'", domain);
            }
        }
    }
}
