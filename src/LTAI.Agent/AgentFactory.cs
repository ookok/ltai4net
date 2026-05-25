using System.Diagnostics;
using LTAI.DNA.Consciousness;
using LTAI.DNA.Safety;
using LTAI.Models;
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
    private readonly UnifiedSafetyGate _safetyGate;
    private readonly Agents.SkillRegistry _skillRegistry;

    public AgentFactory(IServiceProvider sp, ILogger<AgentFactory> logger)
    {
        _sp = sp;
        _logger = logger;
        _safetyGate = sp.GetRequiredService<UnifiedSafetyGate>();
        _skillRegistry = sp.GetRequiredService<Agents.SkillRegistry>();
    }

    public AIAgent Create(string agentName)
    {
        var config = _sp.GetRequiredService<AgentConfig>();
        var card = config.Agents.FirstOrDefault(a =>
            a.Name.Equals(agentName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Agent '{agentName}' not found.");

        var chatClient = _sp.GetRequiredService<IChatClient>();
        var loggerFactory = _sp.GetRequiredService<ILoggerFactory>();

        AIAgent agent = card.Type switch
        {
            AgentType.Code => new Agents.CodeAgent(card, chatClient, _skillRegistry,
                loggerFactory.CreateLogger<Agents.CodeAgent>()),
            AgentType.EIA => new Agents.EIAAgent(card, chatClient, _skillRegistry,
                loggerFactory.CreateLogger<Agents.EIAAgent>()),
            AgentType.Reasoning => new Agents.ReasoningAgent(card, chatClient, _skillRegistry,
                loggerFactory.CreateLogger<Agents.ReasoningAgent>()),
            _ => new Agents.ChatAgent(card, chatClient, _skillRegistry,
                loggerFactory.CreateLogger<Agents.ChatAgent>(),
                _sp.GetService<Personality>())
        };

        agent = ApplyUnifiedSafety(agent, card);
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

    private AIAgent ApplyUnifiedSafety(AIAgent agent, LTAIAgentCard card)
    {
        var builder = agent.AsBuilder();

        builder.Use(async (messages, session, options, inner, ct) =>
        {
            using var span = new ActivitySource("LTAI.Safety").StartActivity("unified_safety.middleware");
            var msgList = messages.ToList();
            var userMsg = msgList.LastOrDefault(m => m.Role == ChatRole.User);
            var sessionId = (session as LTAIAgentSession)?.SessionId ?? "anon";
            span?.SetTag("safety.session", sessionId);

            var gateVerdict = await _safetyGate.EvaluateInputAsync(
                userMsg?.Text ?? "", sessionId, ct);

            if (!gateVerdict.IsAllowed)
            {
                span?.SetTag("safety.blocked", true);
                span?.SetTag("safety.reason", gateVerdict.Reason);
                _logger.LogWarning("AgentFactory [{Agent}]: Input blocked by UnifiedSafetyGate: {Reason}",
                    card.Name, gateVerdict.Reason);
                return new AgentResponse(new ChatMessage(ChatRole.Assistant,
                    $"[Safety] {gateVerdict.Reason}"));
            }

            if (gateVerdict.Action == GateAction.Warn)
                _logger.LogWarning("AgentFactory [{Agent}]: Safety warning: {Reason}",
                    card.Name, gateVerdict.Reason);

            var response = await inner.RunAsync(messages, session, options, ct).ConfigureAwait(false);

            if (response.Text is not null)
            {
                var outputVerdict = await _safetyGate.EvaluateOutputAsync(
                    response.Text, sessionId, ct).ConfigureAwait(false);

                if (!outputVerdict.IsAllowed)
                {
                    _logger.LogWarning("AgentFactory [{Agent}]: Output blocked: {Reason}",
                        card.Name, outputVerdict.Reason);
                    return new AgentResponse(new ChatMessage(ChatRole.Assistant,
                        $"[Safety] Output filtered: {outputVerdict.Reason}"));
                }
            }

            return response;
        }, null);

        return builder.Build();
    }
}
