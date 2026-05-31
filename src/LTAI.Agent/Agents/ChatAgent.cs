using System.Diagnostics;
using System.Runtime.CompilerServices;
using LTAI.AI;
using LTAI.Agent.Workflows;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace LTAI.Agent;

/// <summary>
/// Primary user-facing agent wrapper. Routes messages through L1 (flash) model,
/// auto-upgrades to L2 (pro) when response contains &lt;&lt;&lt;NEEDS_PRO&gt;&gt;&gt; marker.
/// Supports: direct chat, workflow handoff, sequential/concurrent agent routing.
///
/// Each ChatAsync call generates a <c>traceId</c> (from Activity.Current or new Guid)
/// propagated through WorkflowOrchestrator and SubagentTools for causal chain tracing.
///
/// <b>Consumers:</b> TuiApp, ChatView (UI layer calls ChatAsync/ChatStreamingAsync).
/// Registered in ServiceCollectionExtensions.AddLTAIAgent() as singleton.
///
/// L1 → L2 upgrade flow:
///   1. Flash model responds with &lt;&lt;&lt;NEEDS_PRO: reason&gt;&gt;&gt;
///   2. ChatAgent re-runs with Pro model, prepends upgrade note
///   3. User sees "[Auto-upgraded to Pro: reason]" in response
/// </summary>
public sealed class ChatAgent
{
    private static readonly HashSet<string> _simpleQueries = new(StringComparer.OrdinalIgnoreCase)
    {
        "你好", "hi", "hello", "hey", "嗨",
        "早上好", "下午好", "晚上好", "午安", "晚安",
        "help", "status", "clear", "ping", "test",
        "thanks", "谢谢", "thank you", "thank u", "多谢", "感谢",
        "bye", "再见", "拜拜", "goodbye",
        "yes", "no", "ok", "okay", "好的", "嗯", "哦",
        "who are you", "你是谁",
        "?", "？", "！", "!", "",
    };

    /// <summary>
    /// Get or create a trace ID for the current operation.
    /// Uses Activity.Current?.Id when OpenTelemetry is active, otherwise a new Guid.
    /// </summary>
    internal static string GetOrCreateTraceId() =>
        Activity.Current?.Id ?? Guid.NewGuid().ToString("n");
    private readonly AIAgent _agent;       // L1 flash agent (fast, cheap)
    private readonly AIAgent _proAgent;    // L2 pro agent (deep reasoning)
    private readonly WorkflowOrchestrator? _workflows;  // multi-agent router
    private readonly BudgetTracker? _budgetTracker;
    private AgentSession? _session;        // MAF conversation session
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private readonly LocalEmbedder? _localEmbedder;  // 用于预加载 ONNX 模型
    private readonly IHttpClientFactory? _httpFactory; // 用于预热 HTTP 连接

    /// <param name="agent">Default L1 (flash) agent.</param>
    /// <param name="proAgent">L2 (pro) agent for complex task auto-upgrade. Falls back to agent if null.</param>
    /// <param name="workflows">Optional workflow orchestrator for multi-agent routing.</param>
    /// <param name="budgetTracker">Optional token budget tracker for per-user spending limits.</param>
    /// <param name="localEmbedder">Optional ONNX embedder for preloading (warmup).</param>
    /// <param name="httpFactory">Optional HTTP factory for connection warmup.</param>
    public ChatAgent(AIAgent agent, AIAgent? proAgent = null, WorkflowOrchestrator? workflows = null,
        BudgetTracker? budgetTracker = null,
        LocalEmbedder? localEmbedder = null, IHttpClientFactory? httpFactory = null)
    {
        _agent = agent;
        _proAgent = proAgent ?? agent;
        _workflows = workflows;
        _budgetTracker = budgetTracker;
        _localEmbedder = localEmbedder;
        _httpFactory = httpFactory;
    }

    /// <summary>
    /// 预加载：初始化 session + 发送最小 HTTP 请求预热网络连接（DNS/TLS/keep-alive），
    /// 让用户的首条消息响应更快。
    /// </summary>
    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        // 1. 创建 session（内存操作）
        await GetOrCreateSessionAsync(ct).ConfigureAwait(false);

        // 2. 预加载 ONNX 模型（~500ms 首次, ~0ms 后续）
        // 直接触发 LocalEmbedder._sessionLazy.Value，
        // 比等首条用户消息触发 ToolRetrievalProvider 更早加载。
        if (_localEmbedder?.Available == false)
        {
            // Available 属性会触发 Lazy 加载，这里显式访问触发
            _ = _localEmbedder.Dim;
        }

        // 3. 预热 HTTP 连接（无 token 消耗）
        // 发送 OPTIONS 请求到 LLM endpoint 建立 TCP+TLS 连接池，
        // 替代之前用 . 触发 LLM 调用的方式（省钱且更快）。
        try
        {
            using var warmCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            warmCts.CancelAfter(TimeSpan.FromSeconds(5));
            using var http = _httpFactory?.CreateClient("llm");
            if (http != null)
            {
                // OPTIONS 请求是 HTTP 规范中最轻的请求，无 token 消耗
                using var req = new HttpRequestMessage(HttpMethod.Options, "https://api.deepseek.com/v1/chat/completions");
                _ = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, warmCts.Token)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // 预热超时或失败不影响主流程（部分网络/代理可能不支持 OPTIONS）
        }
    }

    /// <summary>
    /// Send a message and get a non-streaming response.
    /// Checks token budget before calling LLM; returns friendly message if budget exceeded.
    /// Auto-upgrades to Pro model if flash response contains &lt;&lt;&lt;NEEDS_PRO&gt;&gt;&gt;.
    /// <b>Callers:</b> ChatView (Desktop UI send button).
    /// </summary>
    public async Task<string> ChatAsync(string message, string? userId = null, CancellationToken ct = default)
    {
        userId ??= "default";
        // ── Budget check at entry point ──
        if (_budgetTracker != null)
        {
            var estimatedTokens = Math.Max(10, message.Length / 4);
            var (allowed, remaining) = _budgetTracker.TryConsume(userId, estimatedTokens);
            if (!allowed)
            {
                return $"⛔ Token budget exceeded. Remaining budget: {remaining} tokens. " +
                       "Please wait for budget reset or contact your administrator.";
            }
        }

        var traceId = GetOrCreateTraceId();
        var session = await GetOrCreateSessionAsync(ct).ConfigureAwait(false);
        var trimmed = message.Trim();
        var isSimple = trimmed.Length <= 10 || _simpleQueries.Contains(trimmed);
        var messages = new[] { new ChatMessage(ChatRole.User, message) };

        // L1: try with flash model first
        var r = await _agent.RunAsync(messages, session, cancellationToken: ct).ConfigureAwait(false);
        var text = r.Messages?.LastOrDefault()?.Text ?? "";

        // Simple query fast path: single L1 call, skip NEEDS_PRO, enforce, and reflection
        if (isSimple) return text;

        // L2: detect upgrade marker or refusal patterns → re-run with pro model
        var needsPro = text.Contains("<<<NEEDS_PRO:");

        // 失败驱动：flash 响应含拒绝词（长度 > 20 避免误伤问候）但无 NEEDS_PRO 标记
        if (!needsPro && text.Length > 20)
        {
            var lower = text.ToLowerInvariant();
            var failPatterns = new[] { "无法获取", "无法确定", "无法提供", "无法访问",
                "抱歉", "我无法", "暂时无法", "目前还不支持", "我不能",
                "cannot", "can't", "unable to", "don't know", "i don't" };
            if (failPatterns.Any(p => lower.Contains(p)))
            {
                System.Diagnostics.Debug.WriteLine("[ChatAgent] L1→L2 auto-upgrade triggered by refusal pattern");
                needsPro = true;
            }
        }

        if (needsPro)
        {
            // Extract reason
            var reason = "complex task";
            if (text.Contains("<<<NEEDS_PRO:"))
            {
                var match = System.Text.RegularExpressions.Regex.Match(text, @"<<<NEEDS_PRO:\s*(.+?)>>>");
                if (match.Success) reason = match.Groups[1].Value.Trim();
            }
            else
            {
                reason = "flash model declined to answer";
            }

            // Re-run with pro agent
            r = await _proAgent.RunAsync(messages, session, cancellationToken: ct).ConfigureAwait(false);
            text = r.Messages?.LastOrDefault()?.Text ?? "";

            // Prepend upgrade note
            text = $"[Auto-upgraded to Pro: {reason}]\n\n{text}";
        }

        // Single combined enforce + reflection pass (at most 1 LLM call)
        text = await EnforceAndReflectAsync(text, message, session, ct).ConfigureAwait(false);

        return text;
    }

    public async IAsyncEnumerable<AgentResponseUpdate> ChatStreamingAsync(
        string message, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var session = await GetOrCreateSessionAsync(ct).ConfigureAwait(false);
        await foreach (var update in _agent.RunStreamingAsync(
            [new ChatMessage(ChatRole.User, message)], session, cancellationToken: ct).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    /// <summary>
    /// Execute a handoff workflow: the orchestrator routes to specialist agents.
    /// </summary>
    public Task<AgentResponse> RunWorkflowAsync(string task, CancellationToken ct = default)
    {
        if (_workflows == null)
            return Task.FromResult(new AgentResponse(
                new ChatMessage(ChatRole.Assistant, "Workflow orchestrator not available.")));
        return _workflows.ExecuteHandoffAsync(task, traceId: GetOrCreateTraceId(), ct: ct);
    }

    /// <summary>
    /// Execute agents sequentially.
    /// </summary>
    public Task<string> RunSequentialAsync(string[] agentNames, string task, CancellationToken ct = default)
    {
        if (_workflows == null)
            return Task.FromResult("Workflow orchestrator not available.");
        return _workflows.ExecuteSequentialAsync(agentNames, task, traceId: GetOrCreateTraceId(), ct: ct);
    }

    /// <summary>
    /// Three-stage correction waterfall: (1) combined enforce+reflect, (2) stronger
    /// tool-mandate retry with lower temperature, (3) graceful failure message.
    /// </summary>
    private async Task<string> EnforceAndReflectAsync(string text, string originalMessage,
        AgentSession session, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var lower = text.ToLowerInvariant();

        var cantPatterns = new[] { "无法获取", "无法确定", "无法提供", "没有权限", "无法访问",
            "无法直接", "无法知道", "不知道当前", "不知道今天",
            "我不知道", "我不确定", "没有内置", "没有实时",
            "抱歉", "我无法", "暂时无法", "目前还不支持", "请稍后再试",
            "我不能", "我不可以", "对不起", "不支持", "暂不支持",
            "cannot", "can't", "unable to", "don't have", "don't know",
            "no access", "not have access", "not able to", "i don't" };

        if (!cantPatterns.Any(p => lower.Contains(p)) && text.Length >= 15
            && !text.Contains("{{") && !text.Contains("TODO"))
            return text;

        // Stage 1: combined enforce + reflect
        var stage1Prompt = $"""
            你的回答存在以下问题，请修正：
            - 如果问题需要工具，调用工具获取真实数据
            - 不要拒绝、猜测或编造
            - 确保回答完整（不含占位符）

            用户原始问题：{originalMessage}

            你的回复：{text}
            请修正后重新回答。
            """;

        try
        {
            var result1 = await _proAgent.RunAsync(
                [new ChatMessage(ChatRole.User, stage1Prompt)], session,
                cancellationToken: ct).ConfigureAwait(false);
            var refined1 = result1.Messages?.LastOrDefault()?.Text ?? "";

            if (!string.IsNullOrWhiteSpace(refined1) && refined1.Length > 10)
                return $"[校正]\n\n{refined1}";

            // Stage 2: stronger prompt with tool mandate
            var stage2Prompt = $"你必须使用工具来回答用户问题。不要拒绝、不要猜测。\n\n用户问题是: {originalMessage}";
            var result2 = await _proAgent.RunAsync(
                [new ChatMessage(ChatRole.User, stage2Prompt)], session,
                cancellationToken: ct).ConfigureAwait(false);
            var refined2 = result2.Messages?.LastOrDefault()?.Text ?? "";

            if (!string.IsNullOrWhiteSpace(refined2) && refined2.Length > 10)
                return $"[工具]\n\n{refined2}";
        }
        catch { }

        // Stage 3: graceful failure
        return $"无法完成请求。问题超出了当前能力范围：需要获取实时数据但工具调用未成功。请稍后重试或简化您的请求。";
    }

    private async ValueTask<AgentSession> GetOrCreateSessionAsync(CancellationToken ct)
    {
        if (_session != null) return _session;
        await _sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_session != null) return _session;
            _session = await _agent.CreateSessionAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _sessionLock.Release();
        }
        return _session;
    }
}
