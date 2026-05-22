using LTAI.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LTAI.Agent.Agents;
using LTAI.Agent.Middleware;
using LTAI.Agent.Workflows;

namespace LTAI.Agent;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAIAgent(this IServiceCollection services)
    {
        services.AddSingleton<AgentMeshWorkflow>();
        services.AddSingleton<PromptShieldMiddleware>();
        services.AddSingleton<InputClassifierMiddleware>();
        services.AddSingleton<DNASafetyMiddleware>();
        services.AddSingleton<OutputReviewMiddleware>();
        services.AddSingleton<AgentRegistry>();
        services.AddSingleton<IAgentFactory, AgentFactory>();

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

    private static AIAgent CreateAgent(IServiceProvider sp, AgentCard card)
    {
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var chatClient = sp.GetRequiredService<IChatClient>();
        var tools = ResolveTools(sp, card.Tools);

        AIAgent agent = card.Type switch
        {
            AgentType.Chat => new ChatAgent(chatClient, card, tools,
                loggerFactory.CreateLogger<ChatAgent>()),
            AgentType.Code => new CodeAgent(chatClient, card, tools,
                loggerFactory.CreateLogger<CodeAgent>()),
            AgentType.EIA => new EIAAgent(chatClient, card, tools,
                loggerFactory.CreateLogger<EIAAgent>()),
            AgentType.Reasoning => new ReasoningAgent(chatClient, card, tools,
                loggerFactory.CreateLogger<ReasoningAgent>()),
            _ => new ChatAgent(chatClient, card, tools,
                loggerFactory.CreateLogger<ChatAgent>())
        };

        ApplyMiddleware(agent, card, sp);
        return agent;
    }

    private static void ApplyMiddleware(AIAgent agent, AgentCard card, IServiceProvider sp)
    {
        foreach (var mwName in card.Middleware)
        {
            AgentMiddleware? middleware = mwName switch
            {
                "prompt_shield" => sp.GetRequiredService<PromptShieldMiddleware>(),
                "input_classifier" => sp.GetRequiredService<InputClassifierMiddleware>(),
                "dna_safety" => sp.GetRequiredService<DNASafetyMiddleware>(),
                "output_review" => sp.GetRequiredService<OutputReviewMiddleware>(),
                _ => null
            };
            if (middleware is not null)
                agent.Use(middleware);
        }
    }

    private static AITool[] ResolveTools(IServiceProvider sp, List<string> toolNames)
    {
        var allTools = sp.GetRequiredService<IEnumerable<AITool>>().ToArray();
        if (toolNames.Count == 0) return allTools;

        return allTools
            .Where(t => toolNames.Any(n =>
                t.Name.Contains(n, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    private static AgentConfig ParseYamlConfig(string yaml)
    {
        var config = new AgentConfig();
        var lines = yaml.Split('\n');
        AgentCard? currentAgent = null;
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
                currentAgent = new AgentCard { Name = trimmed[8..].Trim() };
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
}
