using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Workflows;

/// <summary>
/// Task decomposition orchestrator.
/// Routes tasks to specialist agents via direct agent invocation.
/// </summary>
public sealed class WorkflowOrchestrator
{
    private readonly ILogger<WorkflowOrchestrator> _logger;
    private readonly Dictionary<string, AIAgent> _specialists;
    private readonly AIAgent _defaultAgent;

    public WorkflowOrchestrator(
        IEnumerable<AIAgent> allAgents,
        AIAgent defaultAgent,
        ILogger<WorkflowOrchestrator> logger)
    {
        _logger = logger;
        _defaultAgent = defaultAgent;
        _specialists = allAgents
            .Where(a => !string.Equals(a.Name, defaultAgent.Name, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(a => a.Name!, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Route a task to a specialist agent by name.
    /// The orchestrator decides which agent to use based on the task description.
    /// </summary>
    public async Task<AgentResponse> ExecuteHandoffAsync(
        string task,
        CancellationToken ct = default)
    {
        // Use orchestrator agent to decide routing
        var routingMessages = new List<ChatMessage>
        {
            new(ChatRole.System, $"""
                You are the orchestrator. Available specialists:
                {string.Join("\n", _specialists.Select(s => $"  - {s.Key}"))}
                
                Analyze the user's request and respond with:
                HANDOFF TO <agent-name>: <task-for-specialist>
                or answer directly if the request is simple.
                """),
            new(ChatRole.User, task)
        };

        var routingResponse = await _defaultAgent.RunAsync(routingMessages, cancellationToken: ct);
        var decision = routingResponse.Messages?.LastOrDefault()?.Text ?? "";

        // Check if the orchestrator decided to hand off
        foreach (var (name, agent) in _specialists)
        {
            if (decision.Contains($"HANDOFF TO {name}", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Handoff '{Task}' → {Agent}", task, name);
                
                // Extract the task for the specialist
                var specialistTask = ExtractHandoffTask(decision, name) ?? task;
                
                var result = await agent.RunAsync(
                    [new ChatMessage(ChatRole.User, specialistTask)],
                    cancellationToken: ct);
                return result;
            }
        }

        // No handoff - orchestrator answered directly
        _logger.LogInformation("Direct answer (no handoff): {Task}", task);
        return routingResponse;
    }

    /// <summary>
    /// Execute agents sequentially, each receiving previous output.
    /// </summary>
    public async Task<string> ExecuteSequentialAsync(
        string[] agentNames,
        string task,
        CancellationToken ct = default)
    {
        var agents = ResolveAgents(agentNames);
        if (agents.Length == 0) return "No valid agents specified.";

        _logger.LogInformation("Sequential: {Agents} → {Task}",
            string.Join(" → ", agents.Select(a => a.Name)), task);

        var messages = new List<ChatMessage> { new(ChatRole.User, task) };

        foreach (var agent in agents)
        {
            var response = await agent.RunAsync(messages, cancellationToken: ct);
            var text = response.Messages?.LastOrDefault()?.Text ?? "(no output)";
            messages = [new ChatMessage(ChatRole.User, text)];
        }

        return messages[0].Text ?? "";
    }

    /// <summary>
    /// Execute agents concurrently, combine results.
    /// </summary>
    public async Task<string> ExecuteConcurrentAsync(
        string[] agentNames,
        string task,
        CancellationToken ct = default)
    {
        var agents = ResolveAgents(agentNames);
        if (agents.Length == 0) return "No valid agents specified.";

        _logger.LogInformation("Concurrent: {Agents} on: {Task}",
            string.Join(", ", agents.Select(a => a.Name)), task);

        var results = await Task.WhenAll(agents.Select(agent =>
            agent.RunAsync([new ChatMessage(ChatRole.User, task)], cancellationToken: ct)
        ));

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Concurrent Results\n");
        for (int i = 0; i < agents.Length; i++)
        {
            var text = results[i].Messages?.LastOrDefault()?.Text ?? "(no response)";
            sb.AppendLine($"### {agents[i].Name}");
            sb.AppendLine(text);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private AIAgent[] ResolveAgents(string[] names)
    {
        return names
            .Select(n => string.Equals(n, _defaultAgent.Name, StringComparison.OrdinalIgnoreCase)
                ? _defaultAgent
                : _specialists.GetValueOrDefault(n))
            .Where(a => a != null)
            .Cast<AIAgent>()
            .ToArray();
    }

    private static string? ExtractHandoffTask(string decision, string agentName)
    {
        // Look for text after "HANDOFF TO <name>:" or ":"
        var marker = $"HANDOFF TO {agentName}";
        var idx = decision.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var after = decision[(idx + marker.Length)..].Trim();
        // Strip leading punctuation
        if (after.StartsWith(':')) after = after[1..].Trim();
        return string.IsNullOrEmpty(after) ? null : after;
    }
}
