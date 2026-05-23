using LTAI.Agent.Agents;
using LTAI.Agent.Routing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Workflows;

public enum OrchestrationMode { Direct, Handoff, Sequential, FanOut, Parliament }

public sealed class UniversalOrchestrator
{
    private readonly ILogger<UniversalOrchestrator> _logger;
    private readonly UnifiedSemanticRouter _router;
    private readonly Dictionary<string, BaseAgent> _agents = new();
    private const int MaxRecursionDepth = 3;

    public UniversalOrchestrator(
        ILogger<UniversalOrchestrator> logger,
        UnifiedSemanticRouter router)
    {
        _logger = logger;
        _router = router;
    }

    public void RegisterAgent(string name, BaseAgent agent)
    {
        _agents[name] = agent;
        _logger.LogInformation("Orchestrator: registered agent '{Name}'", name);
    }

    public async Task<AgentResponse> ExecuteAsync(
        OrchestrationMode mode,
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        CancellationToken ct = default)
    {
        return mode switch
        {
            OrchestrationMode.Direct => await ExecuteDirectAsync(messages, session, 0, ct),
            OrchestrationMode.Handoff => await ExecuteHandoffAsync(messages, session, 0, ct),
            OrchestrationMode.FanOut => await ExecuteFanOutAsync(messages, session, ct),
            _ => await ExecuteDirectAsync(messages, session, 0, ct)
        };
    }

    private async Task<AgentResponse> ExecuteDirectAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, int depth, CancellationToken ct)
    {
        var msgList = messages.ToList();
        if (msgList.Count == 0 || msgList.All(m => string.IsNullOrWhiteSpace(m.Text)))
        {
            _logger.LogWarning("Orchestrator: empty message at depth {Depth} — rejected", depth);
            return new(new ChatMessage(ChatRole.Assistant, "[Orchestrator] Empty input. Please provide a valid request."));
        }

        if (depth >= MaxRecursionDepth)
            return new(new ChatMessage(ChatRole.Assistant, "[Orchestrator] Loop detected — max recursion reached."));

        var userMsg = msgList.LastOrDefault(m => m.Role == ChatRole.User);
        var route = await _router.RouteAsync(userMsg?.Text ?? "", ct);

        if (route.ShouldBlock)
            return new(new ChatMessage(ChatRole.Assistant, $"[Router] Unable to classify intent. Please rephrase."));

        if (_agents.TryGetValue(route.TargetAgent, out var target))
        {
            _logger.LogInformation("Orchestrator: routed to {Agent} (conf={Conf:F2})", route.TargetAgent, route.FinalConfidence);
            var response = await target.RunAsync(messages, session, null, ct);

            if (_agents.TryGetValue($"{route.TargetAgent}_critic", out var critic))
            {
                var review = await critic.RunAsync(
                    [new(ChatRole.User, $"Review this {route.TargetAgent} output:\n{response.Text}")],
                    session, null, ct);
                return MergeWithCritic(response, review);
            }
            return response;
        }

        if (_agents.TryGetValue("chat", out var chat))
            return await chat.RunAsync(messages, session, null, ct);

        return new(new ChatMessage(ChatRole.Assistant, "No agent available for this request."));
    }

    private async Task<AgentResponse> ExecuteHandoffAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, int depth, CancellationToken ct)
    {
        if (depth >= MaxRecursionDepth)
            return new(new ChatMessage(ChatRole.Assistant, "[Orchestrator] Handoff loop detected — circuit breaker tripped."));

        var result = await ExecuteDirectAsync(messages, session, depth, ct);

        if (result.Text?.Contains("[HANDOFF:", StringComparison.OrdinalIgnoreCase) == true)
        {
            _logger.LogInformation("Orchestrator: handoff requested at depth {Depth}", depth + 1);
            var msgList = messages.ToList();
            var summary = CompressContext(msgList, result.Text);
            var handoffMsgs = new List<ChatMessage>(messages) { new(ChatRole.System, $"[Handoff]: {summary}") };
            return await ExecuteHandoffAsync(handoffMsgs, session, depth + 1, ct);
        }
        return result;
    }

    private async Task<AgentResponse> ExecuteFanOutAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, CancellationToken ct)
    {
        var msgList = messages.ToList();
        var userMsg = msgList.LastOrDefault(m => m.Role == ChatRole.User);
        var routes = await _router.RouteAllAsync(userMsg?.Text ?? "", ct);

        var tasks = routes.Take(3)
            .Where(r => _agents.ContainsKey(r.TargetAgent))
            .Select(r => _agents[r.TargetAgent].RunAsync(messages, session, null, ct));

        var results = await Task.WhenAll(tasks);
        var merged = string.Join("\n\n---\n\n",
            results.Select((r, i) => $"### Response {i + 1}\n{r.Text}"));
        return new(new ChatMessage(ChatRole.Assistant, merged + $"\n\n---\n**{results.Length} agents contributed**"));
    }

    private static AgentResponse MergeWithCritic(AgentResponse original, AgentResponse critic)
    {
        var text = $"{original.Text}\n\n---\n## Critic Review\n{critic.Text}";
        return new(new ChatMessage(ChatRole.Assistant, text));
    }

    private static string CompressContext(List<ChatMessage> messages, string lastResponse)
    {
        var userText = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
        var query = userText.Length > 200 ? userText[..200] + "..." : userText;
        var resp = lastResponse.Length > 300 ? lastResponse[..300] + "..." : lastResponse;
        return $"User: {query}\nPrevious: {resp}";
    }
}
