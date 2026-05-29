using LTAI.Agent.Agents;
using LTAI.Core.Configuration;
using LTAI.Core.Governors;
using LTAI.Core.System;
using LTAI.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Workflows;

public enum OrchestrationMode { Direct, Handoff, Sequential, FanOut, Parliament }

public sealed class UniversalOrchestrator
{
    private readonly ILogger<UniversalOrchestrator> _logger;
    private readonly IntentRouter _router;
    private readonly HarnessProfile _harness;
    private readonly IConcurrencyGuard? _concurrencyGuard;
    private readonly Dictionary<string, BaseAgent> _agents = new();
    private const int MaxRecursionDepth = 3;
    private const int FanOutTimeoutMs = 60_000;
    private const int DefaultMaxConcurrent = 3;

    public UniversalOrchestrator(
        ILogger<UniversalOrchestrator> logger,
        HarnessProfile? harness = null,
        IConcurrencyGuard? concurrencyGuard = null)
    {
        _logger = logger;
        _router = new IntentRouter();
        _harness = harness ?? HarnessProfile.For(HarnessMode.Hybrid);
        _concurrencyGuard = concurrencyGuard;
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
            HarnessMode.Evolutionary => OrchestrationMode.FanOut,
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

        // Resource-aware concurrency limit
        var availableSlots = GetAvailableConcurrencySlots();
        var maxConcurrent = Math.Min(availableSlots, DefaultMaxConcurrent);

        var selected = routes
            .Where(r => _agents.ContainsKey(r.TargetAgent.ToString().ToLowerInvariant()))
            .Take(maxConcurrent)
            .ToList();

        if (selected.Count == 0)
        {
            _logger.LogWarning("FanOut: no agents available for {Count} routes (slots={Slots})",
                routes.Count, availableSlots);
            return new(new ChatMessage(ChatRole.Assistant,
                $"No agents available. Current concurrency: {DefaultMaxConcurrent - availableSlots}/{DefaultMaxConcurrent}"));
        }

        _logger.LogInformation("FanOut: executing {Count} agents concurrently (slots={Slots}/{Max})",
            selected.Count, availableSlots, DefaultMaxConcurrent);

        // Timed parallel execution
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(FanOutTimeoutMs);
        var timeoutToken = timeoutCts.Token;

        var agentTasks = selected
            .Select(r => _agents[r.TargetAgent.ToString().ToLowerInvariant()]
                .RunAsync(messages, session, null, timeoutToken))
            .ToList();

        AgentResponse[] results;
        try
        {
            var completed = await Task.WhenAll(agentTasks).ConfigureAwait(false);
            results = completed;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("FanOut: {Count}/{Total} agents timed out after {Timeout}ms",
                agentTasks.Count(t => !t.IsCompletedSuccessfully), agentTasks.Count, FanOutTimeoutMs);
            results = agentTasks
                .Select(t => t.IsCompletedSuccessfully ? t.Result
                    : new AgentResponse(new ChatMessage(ChatRole.Assistant,
                        $"[Agent timeout after {FanOutTimeoutMs}ms]")))
                .ToArray();
        }

        var merged = string.Join("\n\n---\n\n",
            results.Select((r, i) => $"### Response {i + 1}\n{r.Text}"));
        return new(new ChatMessage(ChatRole.Assistant, merged + $"\n\n---\n**{results.Length} agents contributed**"));
    }

    private async Task<AgentResponse> ExecuteSequentialAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, CancellationToken ct)
    {
        var msgList = messages.ToList();
        var userMsg = msgList.LastOrDefault(m => m.Role == ChatRole.User);
        var routes = await _router.RouteAllAsync(userMsg?.Text ?? "", ct);
        var results = new List<string>();

        // Resource-aware sequential limit
        var availableSlots = GetAvailableConcurrencySlots();
        var maxSteps = Math.Min(availableSlots, DefaultMaxConcurrent);
        var step = 0;

        foreach (var route in routes)
        {
            if (step >= maxSteps) break;
            var key = route.TargetAgent.ToString().ToLowerInvariant();
            if (!_agents.TryGetValue(key, out var agent)) continue;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(FanOutTimeoutMs);

            try
            {
                _logger.LogInformation("Sequential: step {Step}/{Max} → {Agent}",
                    step + 1, maxSteps, route.TargetAgent);
                var response = await agent.RunAsync(messages, session, null, timeoutCts.Token)
                    .ConfigureAwait(false);
                results.Add($"[{route.TargetAgent}]: {response.Text}");
                messages = messages.Append(new ChatMessage(ChatRole.Assistant, response.Text ?? ""));
                step++;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Sequential: agent {Agent} timed out after {Timeout}ms",
                    route.TargetAgent, FanOutTimeoutMs);
                results.Add($"[{route.TargetAgent}]: [Timed out after {FanOutTimeoutMs}ms]");
                step++;
            }
        }

        var merged = results.Count > 0
            ? string.Join("\n\n", results)
            : "No agents available for sequential execution.";
        return new(new ChatMessage(ChatRole.Assistant, merged));
    }

    private static AgentResponse MergeWithCritic(AgentResponse original, AgentResponse critic)
    {
        var text = $"{original.Text}\n\n---\n## Critic Review\n{critic.Text}";
        return new(new ChatMessage(ChatRole.Assistant, text));
    }

    private async Task<AgentResponse> ExecuteParliamentAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, CancellationToken ct)
    {
        _logger.LogWarning("Orchestrator: Parliament removed, falling back to FanOut");
        return await ExecuteFanOutAsync(messages, session, ct).ConfigureAwait(false);
    }



    private int GetAvailableConcurrencySlots()
    {
        if (_concurrencyGuard == null)
            return DefaultMaxConcurrent;

        var stats = _concurrencyGuard.Stats();
        var running = stats.TryGetValue("running", out var r) ? Convert.ToInt32(r) : 0;
        var available = DefaultMaxConcurrent - running;
        return Math.Max(0, available);
    }

    private static string CompressContext(List<ChatMessage> messages, string lastResponse)
    {
        var userText = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
        var query = userText.Length > 200 ? userText[..200] + "..." : userText;
        var resp = lastResponse.Length > 300 ? lastResponse[..300] + "..." : lastResponse;
        return $"User: {query}\nPrevious: {resp}";
    }
}
