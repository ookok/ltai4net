using LTAI.Models;
using LTAI.Agent.Middleware;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent;

public interface IAgentFactory
{
    AIAgent Create(string agentName);
    AIAgent GetOrCreate(string agentName);
    IEnumerable<AIAgent> ListAll();
}

public sealed class AgentFactory : IAgentFactory
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<AgentFactory> _logger;
    private readonly Dictionary<string, AIAgent> _cache = new();

    public AgentFactory(IServiceProvider sp, ILogger<AgentFactory> logger)
    {
        _sp = sp;
        _logger = logger;
    }

    public AIAgent Create(string agentName)
    {
        var config = _sp.GetRequiredService<AgentConfig>();
        var card = config.Agents.FirstOrDefault(a =>
            a.Name.Equals(agentName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Agent '{agentName}' not found.");

        var chatClient = _sp.GetRequiredService<Microsoft.Extensions.AI.IChatClient>();
        var tools = _sp.GetServices<AITool>()
            .Where(t => card.Tools.Count == 0 || card.Tools.Any(n =>
                t.Name.Contains(n, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        var loggerFactory = _sp.GetRequiredService<ILoggerFactory>();

        AIAgent agent = card.Type switch
        {
            AgentType.Code => new Agents.CodeAgent(chatClient, card, tools,
                loggerFactory.CreateLogger<Agents.CodeAgent>()),
            AgentType.EIA => new Agents.EIAAgent(chatClient, card, tools,
                loggerFactory.CreateLogger<Agents.EIAAgent>()),
            AgentType.Reasoning => new Agents.ReasoningAgent(chatClient, card, tools,
                loggerFactory.CreateLogger<Agents.ReasoningAgent>()),
            _ => new Agents.ChatAgent(chatClient, card, tools,
                loggerFactory.CreateLogger<Agents.ChatAgent>())
        };

        agent = ApplyMiddleware(agent, card);
        _logger.LogInformation("AgentFactory: Created '{Name}' type={Type}", card.Name, card.Type);
        return agent;
    }

    public AIAgent GetOrCreate(string agentName)
    {
        if (_cache.TryGetValue(agentName, out var cached)) return cached;
        var agent = Create(agentName);
        _cache[agentName] = agent;
        return agent;
    }

    public IEnumerable<AIAgent> ListAll()
    {
        var config = _sp.GetRequiredService<AgentConfig>();
        return config.Agents.Select(card => GetOrCreate(card.Name));
    }

    private AIAgent ApplyMiddleware(AIAgent agent, LTAIAgentCard card)
    {
        var builder = agent.AsBuilder();

        foreach (var mwName in card.Middleware)
        {
            switch (mwName)
            {
                case "prompt_shield":
                    var promptShield = _sp.GetRequiredService<PromptShieldMiddleware>();
                    builder.Use(promptShield.InvokeAsync, null);
                    break;
                case "input_classifier":
                    var inputClassifier = _sp.GetRequiredService<InputClassifierMiddleware>();
                    builder.Use(inputClassifier.InvokeAsync, null);
                    break;
                case "dna_safety":
                    var dnaSafety = _sp.GetRequiredService<DNASafetyMiddleware>();
                    builder.Use(dnaSafety.InvokeAsync, null);
                    break;
                case "output_review":
                    var outputReview = _sp.GetRequiredService<OutputReviewMiddleware>();
                    builder.Use(outputReview.InvokeAsync, null);
                    break;
            }
        }

        return builder.Build();
    }
}
