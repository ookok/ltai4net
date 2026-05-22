using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Workflows;

public sealed class AgentMeshWorkflow
{
    private readonly ILogger<AgentMeshWorkflow> _logger;
    private readonly Dictionary<string, AIAgent> _agents = new();

    public AgentMeshWorkflow(ILogger<AgentMeshWorkflow> logger)
    {
        _logger = logger;
    }

    public void RegisterAgent(string name, AIAgent agent)
    {
        _agents[name] = agent;
        _logger.LogInformation("AgentMeshWorkflow: Registered agent '{Name}'", name);
    }

    public async Task<AgentResponse> RouteAndExecuteAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        CancellationToken cancellationToken = default)
    {
        var msgList = messages.ToList();
        var userMsg = msgList.LastOrDefault(m => m.Role == ChatRole.User);

        if (userMsg?.Text is null)
        {
            return new AgentResponse(new ChatMessage(ChatRole.Assistant,
                "No message to process."));
        }

        var intent = ClassifyRouteIntent(userMsg.Text);
        var targetAgent = GetBestAgent(intent);

        _logger.LogInformation("AgentMeshWorkflow: Routing intent={Intent} to agent={Agent}",
            intent, targetAgent?.Name ?? "fallback");

        if (targetAgent is not null)
        {
            return await targetAgent.RunAsync(messages, session, null, cancellationToken);
        }

        if (_agents.TryGetValue("chat", out var chatAgent))
        {
            return await chatAgent.RunAsync(messages, session, null, cancellationToken);
        }

        return new AgentResponse(new ChatMessage(ChatRole.Assistant,
            "No agent available to handle this request."));
    }

    private AIAgent? GetBestAgent(string intent)
    {
        return intent switch
        {
            "code" => _agents.GetValueOrDefault("code"),
            "eia" => _agents.GetValueOrDefault("eia"),
            "reasoning" => _agents.GetValueOrDefault("reasoning"),
            _ => _agents.GetValueOrDefault("chat")
        };
    }

    private static string ClassifyRouteIntent(string text)
    {
        var lower = text.ToLowerInvariant();

        if (lower.Contains("code") || lower.Contains("programming") || lower.Contains("class ") ||
            lower.Contains("function ") || lower.Contains("debug") || lower.Contains("build") ||
            lower.Contains("test") || lower.Contains("refactor"))
            return "code";

        if (lower.Contains("环境") || lower.Contains("impact") || lower.Contains("emission") ||
            lower.Contains("environmental") || lower.Contains("gis") || lower.Contains("map") ||
            lower.Contains("spatial") || lower.Contains("ecological"))
            return "eia";

        if (lower.Contains("analyze") || lower.Contains("reason") || lower.Contains("think") ||
            lower.Contains("compare") || lower.Contains("evaluate") || lower.Contains("solve") ||
            lower.Contains("logic") || lower.Contains("为什么") || lower.Contains("如何"))
            return "reasoning";

        return "chat";
    }
}
