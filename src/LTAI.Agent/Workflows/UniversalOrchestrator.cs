using LTAI.Agent.Agents;
using LTAI.Agent.Routing;
using LTAI.Core.Configuration;
using LTAI.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Workflows;

public enum OrchestrationMode { Direct, Handoff, Sequential, FanOut, Parliament }

public sealed class UniversalOrchestrator
{
    private readonly ILogger<UniversalOrchestrator> _logger;
    private readonly UnifiedSemanticRouter _router;
    private readonly HarnessProfile _harness;
    private readonly SentientParliament? _parliament;
    private readonly Dictionary<string, BaseAgent> _agents = new();
    private const int MaxRecursionDepth = 3;

    public UniversalOrchestrator(
        ILogger<UniversalOrchestrator> logger,
        UnifiedSemanticRouter router,
        HarnessProfile? harness = null,
        SentientParliament? parliament = null)
    {
        _logger = logger;
        _router = router;
        _harness = harness ?? HarnessProfile.For(HarnessMode.Hybrid);
        _parliament = parliament;
    }

    public void RegisterAgent(string name, BaseAgent agent)
    {
        _agents[name] = agent;
        _logger.LogInformation("Orchestrator: registered agent '{Name}'", name);
    }

    public HarnessProfile Profile => _harness;

    public OrchestrationMode ResolveMode(OrchestrationMode? explicitMode = null)
    {
        if (explicitMode.HasValue) return explicitMode.Value;
        return _harness.Mode switch
        {
            HarnessMode.Controlled => OrchestrationMode.Direct,
            HarnessMode.Evolutionary => OrchestrationMode.Parliament,
            _ => OrchestrationMode.FanOut
        };
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
            OrchestrationMode.FanOut => await ExecuteFanOutAsync(messages, session, ct).ConfigureAwait(false),
            OrchestrationMode.Parliament => await ExecuteParliamentAsync(messages, session, ct).ConfigureAwait(false),
            OrchestrationMode.Sequential => await ExecuteSequentialAsync(messages, session, ct).ConfigureAwait(false),
            _ => await ExecuteDirectAsync(messages, session, 0, ct).ConfigureAwait(false)
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

        var agentKey = route.TargetAgent.ToString().ToLowerInvariant();
        if (_agents.TryGetValue(agentKey, out var target))
        {
            _logger.LogInformation("Orchestrator: routed to {Agent} (conf={Conf:F2})", route.TargetAgent, route.FinalConfidence);
            var response = await target.RunAsync(messages, session, null, ct).ConfigureAwait(false);

            if (_agents.TryGetValue($"{agentKey}_critic", out var critic))
            {
                var review = await critic.RunAsync(
                    [new(ChatRole.User, $"Review this {route.TargetAgent} output:\n{response.Text}")],
                    session, null, ct);
                return MergeWithCritic(response, review);
            }
            return response;
        }

        if (_agents.TryGetValue(AgentType.Chat.ToString().ToLowerInvariant(), out var chat))
            return await chat.RunAsync(messages, session, null, ct).ConfigureAwait(false);

        return new(new ChatMessage(ChatRole.Assistant, "No agent available for this request."));
    }

    private async Task<AgentResponse> ExecuteHandoffAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, int depth, CancellationToken ct)
    {
        if (depth >= MaxRecursionDepth)
            return new(new ChatMessage(ChatRole.Assistant, "[Orchestrator] Handoff loop detected — circuit breaker tripped."));

        var result = await ExecuteDirectAsync(messages, session, depth, ct).ConfigureAwait(false);

        if (result.Text?.Contains("[HANDOFF:", StringComparison.OrdinalIgnoreCase) == true)
        {
            _logger.LogInformation("Orchestrator: handoff requested at depth {Depth}", depth + 1);
            var msgList = messages.ToList();
            var summary = CompressContext(msgList, result.Text);
            var handoffMsgs = new List<ChatMessage>(messages) { new(ChatRole.System, $"[Handoff]: {summary}") };
            return await ExecuteHandoffAsync(handoffMsgs, session, depth + 1, ct).ConfigureAwait(false);
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
            .Where(r => _agents.ContainsKey(r.TargetAgent.ToString().ToLowerInvariant()))
            .Select(r => _agents[r.TargetAgent.ToString().ToLowerInvariant()].RunAsync(messages, session, null, ct));

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var merged = string.Join("\n\n---\n\n",
            results.Select((r, i) => $"### Response {i + 1}\n{r.Text}"));
        return new(new ChatMessage(ChatRole.Assistant, merged + $"\n\n---\n**{results.Length} agents contributed**"));
    }

    private static AgentResponse MergeWithCritic(AgentResponse original, AgentResponse critic)
    {
        var text = $"{original.Text}\n\n---\n## Critic Review\n{critic.Text}";
        return new(new ChatMessage(ChatRole.Assistant, text));
    }

    private async Task<AgentResponse> ExecuteParliamentAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, CancellationToken ct)
    {
        if (_parliament == null)
        {
            _logger.LogWarning("Orchestrator: Parliament mode requested but SentientParliament not available, falling back to FanOut");
            return await ExecuteFanOutAsync(messages, session, ct).ConfigureAwait(false);
        }

        var msgList = messages.ToList();
        var userMsg = msgList.LastOrDefault(m => m.Role == ChatRole.User);
        var query = userMsg?.Text ?? "";

        var result = await _parliament.DeliberateAsync(query, msgList, session, ct).ConfigureAwait(false);

        _logger.LogInformation("Orchestrator: Parliament verdict={Verdict} passed={Passed}/{Total} consensus={Consensus:F2}",
            result.Verdict, result.PassedVotes, result.TotalAgents, result.ConsensusScore);

        if (result.Verdict == ParliamentVerdict.Passed && !string.IsNullOrEmpty(result.FinalResponse))
        {
            return new(new ChatMessage(ChatRole.Assistant, result.FinalResponse));
        }

        return await ExecuteFanOutAsync(messages, session, ct).ConfigureAwait(false);
    }

    private async Task<AgentResponse> ExecuteSequentialAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, CancellationToken ct)
    {
        var msgList = messages.ToList();
        var userMsg = msgList.LastOrDefault(m => m.Role == ChatRole.User);
        var routes = await _router.RouteAllAsync(userMsg?.Text ?? "", ct);
        var results = new List<string>();

        foreach (var route in routes.Take(3))
        {
            var key = route.TargetAgent.ToString().ToLowerInvariant();
            if (!_agents.TryGetValue(key, out var agent)) continue;

            var response = await agent.RunAsync(messages, session, null, ct).ConfigureAwait(false);
            results.Add($"[{route.TargetAgent}]: {response.Text}");

            messages = messages.Append(new ChatMessage(ChatRole.Assistant, response.Text ?? ""));
        }

        var merged = results.Count > 0
            ? string.Join("\n\n", results)
            : "No agents available for sequential execution.";
        return new(new ChatMessage(ChatRole.Assistant, merged));
    }

    private static string CompressContext(List<ChatMessage> messages, string lastResponse)
    {
        var userText = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
        var query = userText.Length > 200 ? userText[..200] + "..." : userText;
        var resp = lastResponse.Length > 300 ? lastResponse[..300] + "..." : lastResponse;
        return $"User: {query}\nPrevious: {resp}";
    }
}
