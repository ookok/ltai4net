using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LTAI.AI;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Declarative;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Workflows;

/// <summary>
/// Multi-agent orchestrator built on MAF workflow primitives:
/// <list type="bullet">
///   <item>Handoff — <see cref="HandoffWorkflowBuilder"/>: router agent picks a specialist via function-call handoff.</item>
///   <item>Sequential — <see cref="AgentWorkflowBuilder.CreateSequentialBuilderWith"/>: pipeline of agents, each receives previous output.</item>
///   <item>Concurrent — <see cref="AgentWorkflowBuilder.CreateConcurrentBuilderWith"/>: fan-out + fan-in aggregator.</item>
/// </list>
/// <para>
/// As of P16.1, Sequential/Concurrent agent lists can be overridden by hot-editable
/// YAML/JSON configs in <c>ltai-workflows/sequential.json</c> and
/// <c>concurrent.json</c>, loaded via <see cref="YAMLWorkflowRegistry"/>.
/// Users can edit these files to change pipeline composition without recompiling.
/// </para>
/// <para>
/// Replaces the legacy <c>WorkflowOrchestrator</c> (text/JSON handoff markers,
/// circuit breaker, retry+fallback, vector top-K, concurrency throttle). The
/// greeting fast-path is now driven by the MAF <c>Workflows.Declarative</c>
/// YAML file <c>ltai-workflows/greeting.yaml</c> (see
/// <see cref="YAMLWorkflowHost"/>), editable without recompiling.
/// </para>
/// </summary>
public sealed class AgentWorkflows
{
    private readonly ILogger<AgentWorkflows> _logger;
    private readonly Dictionary<string, AIAgent> _specialists;
    private readonly AIAgent _router;
    private readonly DecisionTreeRouter _router2;
    private readonly YAMLWorkflowRegistry? _workflowRegistry;

    public AgentWorkflows(
        IEnumerable<AIAgent> allAgents,
        AIAgent router,
        ILogger<AgentWorkflows> logger,
        DecisionTreeRouter? router2 = null,
        YAMLWorkflowRegistry? workflowRegistry = null)
    {
        _logger = logger;
        _router = router;
        _router2 = router2 ?? new DecisionTreeRouter(
            embedder: null,
            logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<DecisionTreeRouter>.Instance);
        _workflowRegistry = workflowRegistry;
        _specialists = allAgents
            .Where(a => !string.Equals(a.Name, router.Name, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(a => a.Name!, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Handoff routing: the <c>router</c> agent receives the user task, emits a
    /// handoff function call to the chosen specialist, and the specialist's
    /// response is returned. Uses MAF <see cref="HandoffWorkflowBuilder"/>
    /// (function-call protocol, replaces the legacy text/JSON marker protocol).
    /// </summary>
    public async Task<AgentResponse> RunHandoffAsync(
        string task,
        string? traceId = null,
        CancellationToken ct = default)
    {
        // ── Greeting fast-path (YAML workflow, editable without recompile) ──
        // D69: try the registry first (P15 hot-editable); fall back to the
        // static YAMLWorkflowHost when the registry has no greeting loaded
        // (preserves the C# fallback path that has shipped since P7.5).
        var canned = await TryRunGreetingAsync(task, ct).ConfigureAwait(false);
        if (canned != null)
        {
            _logger.LogInformation("Greeting fast path (YAML): \"{Task}\" -> \"{Reply}\"", task, canned);
            return new AgentResponse { Messages = [new ChatMessage(ChatRole.Assistant, canned)] };
        }

        // ── P7.7 Decision-tree routing ──
        // Stage 1: embedding top-K by cosine similarity (default K=3)
        // Stage 2: confidence margin (top-1 − top-2)
        // Stage 3: confident → use top-K; ambiguous → fall back to all specialists
        var routing = await _router2.RouteAsync(task, _specialists.Keys.ToArray(), ct).ConfigureAwait(false);
        var candidateNames = routing.Candidates;

        // Expose routing diagnostics via the OTel bag (DecisionTreeRouter already logs them).
        if (routing.Branch == BranchKind.ConfidentTopK)
        {
            _logger.LogDebug("Vector router: confident top-{K}, margin={M:F3}, top={T:F3}",
                routing.Candidates.Count, routing.Margin, routing.TopScore);
        }

        var candidates = candidateNames
            .Select(n => _specialists.TryGetValue(n, out var a) ? a : null)
            .Where(a => a != null)
            .Cast<AIAgent>()
            .ToList();
        if (candidates.Count == 0)
        {
            _logger.LogWarning("No specialist candidates available for task: {Task}", task);
            return new AgentResponse
            {
                Messages = [new ChatMessage(ChatRole.Assistant, "No specialists available to handle this task.")]
            };
        }

        // ── Build MAF Handoff workflow ──
        var builder = AgentWorkflowBuilder.CreateHandoffBuilderWith(_router);
        foreach (var specialist in candidates)
        {
            builder.WithHandoff(_router, specialist);
        }
        // Terminate after the first non-handoff response (one-shot delegation).
        builder.WithTerminationCondition(messages => new ValueTask<bool>(true));
        builder.EmitAgentResponseEvents();

        var workflow = builder.Build();
        var input = new List<ChatMessage> { new(ChatRole.User, task) };

        // ── Execute and collect the final agent response ──
        _logger.LogInformation("Handoff workflow start: {Task} [trace={Trace}]", task, traceId ?? "");
        await using var run = await InProcessExecution.RunStreamingAsync(workflow, input, cancellationToken: ct)
                                                 .ConfigureAwait(false);

        AgentResponse? lastResponse = null;
        await foreach (var evt in run.WatchStreamAsync(ct).ConfigureAwait(false))
        {
            switch (evt)
            {
                case AgentResponseEvent responseEvt when responseEvt.Response is { } resp:
                    lastResponse = resp;
                    break;
                case WorkflowErrorEvent errEvt:
                    _logger.LogError(errEvt.Exception, "Handoff workflow error [trace={Trace}]", traceId ?? "");
                    return new AgentResponse
                    {
                        Messages = [new ChatMessage(ChatRole.Assistant,
                            $"Handoff failed: {errEvt.Exception?.Message ?? "unknown error"}")]
                    };
            }
        }

        return lastResponse ?? new AgentResponse
        {
            Messages = [new ChatMessage(ChatRole.Assistant, "(workflow produced no agent response)")]
        };
    }

    /// <summary>
    /// Sequential pipeline: each agent receives the previous agent's output as
    /// the next user message. Uses MAF <see cref="SequentialWorkflowBuilder"/>.
    /// Agent names can come from a YAML preset (P16.1) or be specified at runtime.
    /// </summary>
    public async Task<string> RunSequentialAsync(
        string[] agentNames,
        string task,
        string? traceId = null,
        CancellationToken ct = default)
    {
        // P16.1: try a YAML preset if no runtime names given or if the first
        // element matches a known preset name in the workflow registry.
        if (agentNames.Length == 1 && _workflowRegistry != null)
        {
            var preset = _workflowRegistry.TryGetPipelineConfig(agentNames[0])
                         ?? _workflowRegistry.TryGetPipelineConfig("sequential");
            if (preset?.Type == "sequential" && preset.Agents.Count > 0)
            {
                agentNames = preset.Agents.ToArray();
                _logger.LogInformation("Using sequential preset: {Agents}",
                    string.Join(", ", agentNames));
            }
        }

        var agents = ResolveAgents(agentNames);
        if (agents.Length == 0) return "No valid agents specified.";

        _logger.LogInformation("Sequential: {Agents} → {Task} [trace={Trace}]",
            string.Join(" → ", agents.Select(a => a.Name)), task, traceId ?? "");

        var workflow = AgentWorkflowBuilder.CreateSequentialBuilderWith(agents).Build();
        var input = new List<ChatMessage> { new(ChatRole.User, task) };

        await using var run = await InProcessExecution.RunStreamingAsync(workflow, input, cancellationToken: ct)
                                                 .ConfigureAwait(false);

        var sb = new StringBuilder();
        await foreach (var evt in run.WatchStreamAsync(ct).ConfigureAwait(false))
        {
            switch (evt)
            {
                case WorkflowOutputEvent outputEvt when outputEvt.Data is List<ChatMessage> messages:
                    AppendTranscript(sb, messages);
                    break;
                case WorkflowErrorEvent errEvt:
                    _logger.LogError(errEvt.Exception, "Sequential workflow error [trace={Trace}]", traceId ?? "");
                    sb.AppendLine($"[Error: {errEvt.Exception?.Message ?? "unknown"}]");
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Concurrent fan-out: every agent processes the same task in parallel and
    /// results are combined. Uses MAF <see cref="ConcurrentWorkflowBuilder"/>
    /// with a custom aggregator that formats per-agent results as markdown.
    /// Agent names can come from a YAML preset (P16.1) or be specified at runtime.
    /// </summary>
    public async Task<string> RunConcurrentAsync(
        string[] agentNames,
        string task,
        string? traceId = null,
        CancellationToken ct = default)
    {
        // P16.1: try a YAML preset if no runtime names given or if the first
        // element matches a known preset name.
        if (agentNames.Length == 1 && _workflowRegistry != null)
        {
            var preset = _workflowRegistry.TryGetPipelineConfig(agentNames[0])
                         ?? _workflowRegistry.TryGetPipelineConfig("concurrent");
            if (preset?.Type == "concurrent" && preset.Agents.Count > 0)
            {
                agentNames = preset.Agents.ToArray();
                _logger.LogInformation("Using concurrent preset: {Agents}",
                    string.Join(", ", agentNames));
            }
        }

        var agents = ResolveAgents(agentNames);
        if (agents.Length == 0) return "No valid agents specified.";

        _logger.LogInformation("Concurrent: {Agents} on: {Task} [trace={Trace}]",
            string.Join(", ", agents.Select(a => a.Name)), task, traceId ?? "");

        var builder = AgentWorkflowBuilder.CreateConcurrentBuilderWith(agents);
        builder.WithAggregator(static lists =>
        {
            // Per-agent ordered output, last assistant message wins.
            var sb = new StringBuilder();
            sb.AppendLine("## Concurrent Results\n");
            foreach (var list in lists)
            {
                if (list.Count == 0) continue;
                var last = list[^1];
                var name = !string.IsNullOrEmpty(last.AuthorName) ? last.AuthorName : "(unnamed)";
                sb.AppendLine($"### {name}");
                sb.AppendLine(last.Text);
                sb.AppendLine();
            }
            return [new ChatMessage(ChatRole.Assistant, sb.ToString())];
        });
        var workflow = builder.Build();
        var input = new List<ChatMessage> { new(ChatRole.User, task) };

        await using var run = await InProcessExecution.RunStreamingAsync(workflow, input, cancellationToken: ct)
                                                 .ConfigureAwait(false);

        var collected = new StringBuilder();
        await foreach (var evt in run.WatchStreamAsync(ct).ConfigureAwait(false))
        {
            switch (evt)
            {
                case WorkflowOutputEvent outputEvt when outputEvt.Data is List<ChatMessage> messages:
                    AppendTranscript(collected, messages);
                    break;
                case WorkflowErrorEvent errEvt:
                    _logger.LogError(errEvt.Exception, "Concurrent workflow error [trace={Trace}]", traceId ?? "");
                    collected.AppendLine($"[Error: {errEvt.Exception?.Message ?? "unknown"}]");
                    break;
            }
        }

        return collected.ToString();
    }

    private AIAgent[] ResolveAgents(string[] names)
    {
        return names
            .Select(n => string.Equals(n, _router.Name, StringComparison.OrdinalIgnoreCase)
                ? _router
                : _specialists.GetValueOrDefault(n))
            .Where(a => a != null)
            .Cast<AIAgent>()
            .ToArray();
    }

    private static void AppendTranscript(StringBuilder sb, List<ChatMessage> messages)
    {
        foreach (var m in messages)
        {
            if (string.IsNullOrEmpty(m.Text)) continue;
            var name = !string.IsNullOrEmpty(m.AuthorName) ? m.AuthorName : "(agent)";
            sb.AppendLine($"### {name}");
            sb.AppendLine(m.Text);
            sb.AppendLine();
        }
    }

    /// <summary>
    /// P15: try the registry-backed greeting workflow first. If the
    /// registry is null or has no <c>greeting</c> workflow loaded, fall back
    /// to the static <see cref="YAMLWorkflowHost"/> (D69: preserve C# path).
    /// </summary>
    /// <remarks>
    /// P14.9 review: if the greeting fast-path produces a canned reply but
    /// the user's message is substantially longer than a typical greeting
    /// (<paramref name="task"/>.Length &gt; 50), we treat it as a mixed
    /// greeting+query message (e.g. "早上好 帮我查天气") and fall through to
    /// the LLM handoff so the user's substantive request isn't lost.
    /// </remarks>
    private async Task<string?> TryRunGreetingAsync(string task, CancellationToken ct)
    {
        // P14.9 review: pre-check for mixed greeting+query.
        // If the message is long enough to contain a substantive request,
        // skip the canned greeting path entirely and let the LLM handle it.
        var isLongMessage = task.Trim().Length > 50;

        if (isLongMessage)
        {
            _logger.LogDebug("Greeting fast-path skipped: message length {Len} > 50 (likely mixed greeting+query)",
                task.Trim().Length);
            return null;
        }

        // P15 hot path: registry snapshot.
        if (_workflowRegistry != null)
        {
            var workflow = _workflowRegistry.TryGetWorkflow("greeting");
            if (workflow != null)
            {
                await using var run = await InProcessExecution
                    .RunStreamingAsync(workflow, task, cancellationToken: ct)
                    .ConfigureAwait(false);
                await foreach (var evt in run.WatchStreamAsync(ct).ConfigureAwait(false))
                {
                    if (evt is MessageActivityEvent mae && !string.IsNullOrWhiteSpace(mae.Message))
                    {
                        return mae.Message;
                    }
                }
                return null;
            }
        }
        // C# fallback (D69).
        return await YAMLWorkflowHost.RunGreetingFastPathAsync(task, ct).ConfigureAwait(false);
    }
}
