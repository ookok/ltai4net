using System.Text.Json;
using LTAI.AI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Workflows;

/// <summary>
/// Multi-agent orchestrator. Routes tasks to specialist agents via LLM-based routing.
/// Supports: handoff (LLM decides specialist), sequential chain, concurrent fan-out.
///
/// <b>Consumers:</b> ChatAgent (delegates complex tasks via RunWorkflowAsync),
/// WorkflowTools (agent tool interface), ServiceCollectionExtensions (DI setup).
///
/// Routing flow:
///   1. Default agent analyzes task, picks specialist via "HANDOFF TO &lt;name&gt;:" marker
///   2. If no handoff, default agent answers directly
///   3. Sequential/Concurrent modes run explicit agent chains
///
/// &#x26a0; Thread safety: _concurrencyThrottle limits concurrent fan-out to 2 agents.
/// </summary>
public sealed class WorkflowOrchestrator
{
    /// <summary>
    /// JSON handoff marker template for LLM system prompt.
    /// Example: {"handoff_to":"code","task":"Find all places that use HttpClient"}
    /// Must be a const to avoid raw string literal escaping issues.
    /// Use <see cref="TryParseJsonHandoff"/> to parse this from agent output.
    /// </summary>
    private const string HandoffJsonExample = """{"handoff_to":"code","task":"Find all places using HttpClient"}""";

    private readonly ILogger<WorkflowOrchestrator> _logger;
    private readonly Dictionary<string, AIAgent> _specialists;  // non-default agents by name
    private readonly AIAgent _defaultAgent;                      // orchestrator + fallback
    private readonly EmbeddingClient? _embedder;                  // semantic agent router
    private readonly SemaphoreSlim _concurrencyThrottle = new(2, 2); // Max 2 concurrent agents
    private readonly Dictionary<string, int> _specialistFailures = new(StringComparer.OrdinalIgnoreCase);
    private const int SpecialistCircuitBreaker = 3;  // consecutive failures → stop routing
    private const int RetryDelayMs = 500;

    public WorkflowOrchestrator(
        IEnumerable<AIAgent> allAgents,
        AIAgent defaultAgent,
        ILogger<WorkflowOrchestrator> logger,
        EmbeddingClient? embedder = null)
    {
        _logger = logger;
        _defaultAgent = defaultAgent;
        _embedder = embedder;
        _specialists = allAgents
            .Where(a => !string.Equals(a.Name, defaultAgent.Name, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(a => a.Name!, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Route a task to a specialist agent by name.
    /// Supports two handoff markers (checked in order):
    ///   1. JSON structured: {"handoff_to":"name","task":"..."} — embedded anywhere in response
    ///   2. String fallback: "HANDOFF TO name: task" — backward compatible
    /// If no marker is found, the orchestrator's response is returned directly.
    /// </summary>
    // ═══════════════════════════════════════════
    //  问候/闲聊关键词 — 不走 LLM 路由
    // ═══════════════════════════════════════════

    private enum GreetingType { None, Greeting, Thanks, Affirm, Farewell, Probing, Test }

    private static readonly HashSet<string> GreetingPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "你好", "hi", "hello", "hey", "嗨", "嘿嘿",
        "早上好", "下午好", "晚上好", "晚安",
    };

    private static readonly HashSet<string> ThanksPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "谢谢", "感谢", "多谢", "辛苦了", "thanks", "thank", "谢谢啦",
    };

    private static readonly HashSet<string> AffirmPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "好的", "ok", "okay", "嗯", "嗯嗯", "是", "对",
    };

    private static readonly HashSet<string> FarewellPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "再见", "拜拜", "bye", "明天见", "回头聊",
    };

    private static readonly HashSet<string> ProbingPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "你会做什么", "你能做什么", "你会写代码吗", "你会什么", "你有什么功能", "你能干嘛",
    };

    private static readonly HashSet<string> TestPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "测试", "试一下", "试试", "在吗", "在不在",
    };

    /// <summary>
    /// 快速通道：分类问候/闲聊意图，返回子类型。
    /// 节省一次 LLM 往返（~5-15秒 + token 费用）。
    /// </summary>
    private static GreetingType ClassifyGreeting(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return GreetingType.Greeting;

        var trimmed = text.Trim();
        if (trimmed.Length > 10) return GreetingType.None;

        if (GreetingPrefixes.Any(p => trimmed.Equals(p, StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith(p))) return GreetingType.Greeting;
        if (ThanksPrefixes.Any(p => trimmed.Equals(p, StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith(p))) return GreetingType.Thanks;
        if (AffirmPrefixes.Any(p => trimmed.Equals(p, StringComparison.OrdinalIgnoreCase))) return GreetingType.Affirm;
        if (FarewellPrefixes.Any(p => trimmed.Equals(p, StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith(p))) return GreetingType.Farewell;
        if (ProbingPrefixes.Any(p => trimmed.Equals(p, StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith(p))) return GreetingType.Probing;
        if (TestPrefixes.Any(p => trimmed.Equals(p, StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith(p))) return GreetingType.Test;

        return GreetingType.None;
    }

    public async Task<AgentResponse> ExecuteHandoffAsync(
        string task,
        string? traceId = null,
        CancellationToken ct = default)
    {
        // ── Fast path: greeting/small-talk → direct answer, no LLM routing ──
        var gType = ClassifyGreeting(task);
        if (gType != GreetingType.None && gType != GreetingType.Affirm)
        {
            _logger.LogInformation("Greeting fast path ({GType}): \"{Task}\"", gType, task);
            var response = gType switch
            {
                GreetingType.Greeting => "你好 👋 我是 LTAI 助手，有什么可以帮你的？",
                GreetingType.Thanks => "不客气 😊 还有什么需要帮忙的吗？",
                GreetingType.Farewell => "再见 👋 随时欢迎回来！",
                GreetingType.Probing => "我是 LTAI 助手，可以帮你写代码、查资料、分析数据、管理文件等。你想做什么？",
                GreetingType.Test => "我在呢 ✅ 随时可以开始。",
                _ => "你好 👋 有什么可以帮你的？",
            };
            return new AgentResponse
            {
                Messages = [new ChatMessage(ChatRole.Assistant, response)]
            };
        }

        // Use orchestrator agent to decide routing.
        // If embedder is available, select top-5 agents by semantic similarity
        // instead of dumping all specialists into the prompt (avoid prompt bloat).
        string specialistsDesc;
        string[] candidateNames;

        if (_embedder != null && _specialists.Count > 5)
        {
            candidateNames = await AgentRegistry.SelectTopKAsync(task, _embedder, k: 5).ConfigureAwait(false);
            specialistsDesc = string.Join("\n", candidateNames.Select(n => $"  - {n}"));
            _logger.LogDebug("Vector router: selected {N}/{Total} candidates",
                candidateNames.Length, _specialists.Count);
        }
        else
        {
            candidateNames = _specialists.Keys.ToArray();
            specialistsDesc = string.Join("\n", _specialists.Select(s => $"  - {s.Key}"));
        }

        var routingMessages = new List<ChatMessage>
        {
            new(ChatRole.System, $"""
                You are the orchestrator. Available specialists:
                {specialistsDesc}

                Analyze the user's request. Choose ONE:
                A) Complex task → respond with JSON: {HandoffJsonExample}
                B) Simple request → answer directly

                Example: {HandoffJsonExample}
                """),
            new(ChatRole.User, task)
        };

        AgentResponse routingResponse;
        try
        {
            routingResponse = await _defaultAgent.RunAsync(routingMessages, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Orchestrator routing failed for task: {Task}", task);
            return new AgentResponse { Messages = [new ChatMessage(ChatRole.Assistant, $"Routing failed: {ex.Message}")] };
        }
        var decision = routingResponse.Messages?.LastOrDefault()?.Text ?? "";

        // Priority 1: Try structured JSON handoff marker
        var jsonHandoff = TryParseJsonHandoff(decision);
        if (jsonHandoff != null && _specialists.TryGetValue(jsonHandoff.Value.name, out var jsonAgent))
        {
            _logger.LogInformation("Handoff (JSON) '{Task}' → {Agent} [trace={Trace}]", task, jsonHandoff.Value.name, traceId ?? "");
            return await RunAgentSafely(jsonAgent, jsonHandoff.Value.name, jsonHandoff.Value.task ?? task, ct, traceId).ConfigureAwait(false);
        }

        // Priority 2: Fall back to string marker "HANDOFF TO name: task"
        foreach (var (name, agent) in _specialists)
        {
            if (decision.Contains($"HANDOFF TO {name}", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Handoff (string) '{Task}' → {Agent} [trace={Trace}]", task, name, traceId ?? "");
                var specialistTask = ExtractHandoffTask(decision, name) ?? task;
                return await RunAgentSafely(agent, name, specialistTask, ct, traceId).ConfigureAwait(false);
            }
        }

        // No handoff - orchestrator answered directly
        _logger.LogInformation("Direct answer (no handoff): {Task} [trace={Trace}]", task, traceId ?? "");
        return routingResponse;
    }

    /// <summary>
    /// Run a specialist agent with retry (1x), fallback to default agent, and circuit breaker.
    /// If the specialist fails:
    ///   1. Increment failure count (circuit breaker @ 3 → stop routing to this specialist)
    ///   2. Retry once after 500ms delay
    ///   3. If retry also fails, fall back to default agent for the answer
    /// </summary>
    private async Task<AgentResponse> RunAgentSafely(AIAgent agent, string name, string specialistTask, CancellationToken ct,
        string? traceId = null)
    {
        // Circuit breaker check
        if (_specialistFailures.TryGetValue(name, out var failures) && failures >= SpecialistCircuitBreaker)
        {
            _logger.LogWarning("Circuit breaker open for '{Agent}' ({Fails} consecutive failures) — skipping", name, failures);
            return await FallbackToDefaultAsync(name, specialistTask, ct, traceId).ConfigureAwait(false);
        }

        for (int attempt = 0; attempt <= 1; attempt++)
        {
            try
            {
                var result = await agent.RunAsync(
                    [new ChatMessage(ChatRole.User, specialistTask)],
                    cancellationToken: ct).ConfigureAwait(false);
                // Success — reset circuit
                _specialistFailures.Remove(name);
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Specialist '{Agent}' failed (attempt {A}) for task: {Task} [trace={Trace}]",
                    name, attempt + 1, specialistTask, traceId ?? "");

                // Track failure for circuit breaker
                _specialistFailures[name] = _specialistFailures.GetValueOrDefault(name) + 1;

                if (attempt == 0)
                {
                    // Retry once after brief delay
                    _logger.LogInformation("Retrying '{Agent}' in {Delay}ms...", name, RetryDelayMs);
                    try { await Task.Delay(RetryDelayMs, ct).ConfigureAwait(false); } catch { break; }
                }
            }
        }

        // Retries exhausted — fall back to default agent
        _logger.LogWarning("Falling back to default agent after '{Agent}' failed", name);
        return await FallbackToDefaultAsync(name, specialistTask, ct, traceId).ConfigureAwait(false);
    }

    /// <summary>
    /// Fallback: route the task to the default orchestrator agent for a direct answer.
    /// </summary>
    private async Task<AgentResponse> FallbackToDefaultAsync(string specialistName, string task,
        CancellationToken ct, string? traceId = null)
    {
        _logger.LogInformation("Fallback: default agent answering '{Task}' (originally for '{Agent}') [trace={Trace}]",
            task, specialistName, traceId ?? "");
        try
        {
            var result = await _defaultAgent.RunAsync(
                [new ChatMessage(ChatRole.User, task)],
                cancellationToken: ct).ConfigureAwait(false);
            var text = result.Messages?.LastOrDefault()?.Text ?? "";
            return new AgentResponse { Messages = [new ChatMessage(ChatRole.Assistant,
                $"[Fallback from {specialistName}]\n\n{text}")] };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fallback default agent also failed for task: {Task}", task);
            return new AgentResponse { Messages = [new ChatMessage(ChatRole.Assistant,
                $"Specialist '{specialistName}' and default agent both failed: {ex.Message}")] };
        }
    }

    /// <summary>
    /// Execute agents sequentially, each receiving previous output.
    /// </summary>
    public async Task<string> ExecuteSequentialAsync(
        string[] agentNames,
        string task,
        string? traceId = null,
        CancellationToken ct = default)
    {
        var agents = ResolveAgents(agentNames);
        if (agents.Length == 0) return "No valid agents specified.";

        _logger.LogInformation("Sequential: {Agents} → {Task} [trace={Trace}]",
            string.Join(" → ", agents.Select(a => a.Name)), task, traceId ?? "");

        var messages = new List<ChatMessage> { new(ChatRole.User, task) };

        foreach (var agent in agents)
        {
            try
            {
                var response = await agent.RunAsync(messages, cancellationToken: ct).ConfigureAwait(false);
                var text = response.Messages?.LastOrDefault()?.Text ?? "(no output)";
                messages = [new ChatMessage(ChatRole.User, text)];
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sequential agent '{Agent}' failed", agent.Name);
                messages = [new ChatMessage(ChatRole.User, $"Agent '{agent.Name}' failed: {ex.Message}")];
            }
        }

        return messages[0].Text ?? "";
    }

    /// <summary>
    /// Execute agents concurrently, combine results.
    /// </summary>
    public async Task<string> ExecuteConcurrentAsync(
        string[] agentNames,
        string task,
        string? traceId = null,
        CancellationToken ct = default)
    {
        var agents = ResolveAgents(agentNames);
        if (agents.Length == 0) return "No valid agents specified.";

        _logger.LogInformation("Concurrent: {Agents} on: {Task} [trace={Trace}]",
            string.Join(", ", agents.Select(a => a.Name)), task, traceId ?? "");

        var results = await Task.WhenAll(agents.Select(async agent =>
        {
            await _concurrencyThrottle.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var agentResponse = await agent.RunAsync(
                    [new ChatMessage(ChatRole.User, task)], cancellationToken: ct).ConfigureAwait(false);
                return (name: agent.Name, response: (AgentResponse?)agentResponse, error: (string?)null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Concurrent agent '{Agent}' failed", agent.Name);
                return (name: agent.Name, response: (AgentResponse?)null, error: (string?)ex.Message);
            }
            finally
            {
                _concurrencyThrottle.Release();
            }
        })).ConfigureAwait(false);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Concurrent Results\n");
        foreach (var (name, response, error) in results)
        {
            sb.AppendLine($"### {name}");
            if (error != null)
                sb.AppendLine($"❌ Failed: {error}");
            else
                sb.AppendLine(response?.Messages?.LastOrDefault()?.Text ?? "(no response)");
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

    /// <summary>
    /// Try to extract a structured JSON handoff marker from the assistant's response.
    /// Looks for JSON in code fences or inline: {"handoff_to":"name","task":"..."}
    /// Returns null if no valid handoff JSON is found.
    /// </summary>
    private static (string name, string? task)? TryParseJsonHandoff(string decision)
    {
        // Try to find JSON object in the text — look for {"handoff_to": ...}
        var jsonPattern = System.Text.RegularExpressions.Regex.Match(
            decision, @"\{(?:[^{}]|(?:\{[^{}]*\}))*""handoff_to""\s*:\s*""([^""]+)""[^}]*\}",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (!jsonPattern.Success)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(jsonPattern.Value,
                new JsonDocumentOptions { AllowTrailingCommas = true });
            var root = doc.RootElement;

            if (!root.TryGetProperty("handoff_to", out var nameProp))
                return null;

            var name = nameProp.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(name))
                return null;

            var task = root.TryGetProperty("task", out var taskProp)
                ? taskProp.GetString()
                : null;

            return (name, task);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
