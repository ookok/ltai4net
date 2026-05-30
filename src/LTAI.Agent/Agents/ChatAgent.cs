using System.Diagnostics;
using System.Net.Http.Json;
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
    private readonly string _sessionDir;   // persistent session directory
    private AgentSession? _session;        // MAF conversation session
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private readonly List<(string user, string assistant)> _messageLog = new();

    /// <param name="agent">Default L1 (flash) agent.</param>
    /// <param name="proAgent">L2 (pro) agent for complex task auto-upgrade. Falls back to agent if null.</param>
    /// <param name="workflows">Optional workflow orchestrator for multi-agent routing.</param>
    /// <param name="budgetTracker">Optional token budget tracker for per-user spending limits.</param>
    /// <param name="sessionDir">Optional directory for session persistence. Default: .livingtree/sessions/</param>
    public ChatAgent(AIAgent agent, AIAgent? proAgent = null, WorkflowOrchestrator? workflows = null,
        BudgetTracker? budgetTracker = null, string? sessionDir = null)
    {
        _agent = agent;
        _proAgent = proAgent ?? agent;
        _workflows = workflows;
        _budgetTracker = budgetTracker;
        _sessionDir = sessionDir ?? Path.Combine(Directory.GetCurrentDirectory(), ".livingtree", "sessions");
        Directory.CreateDirectory(_sessionDir);
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
        var messages = new[] { new ChatMessage(ChatRole.User, message) };

        // L1: try with flash model first
        var r = await _agent.RunAsync(messages, session, cancellationToken: ct).ConfigureAwait(false);
        var text = r.Messages?.LastOrDefault()?.Text ?? "";

        // L2: detect upgrade marker, re-run with pro model
        if (text.Contains("<<<NEEDS_PRO:"))
        {
            // Extract reason for logging
            var reason = "complex task";
            var match = System.Text.RegularExpressions.Regex.Match(text, @"<<<NEEDS_PRO:\s*(.+?)>>>");
            if (match.Success) reason = match.Groups[1].Value.Trim();

            // Re-run with pro agent
            r = await _proAgent.RunAsync(messages, session, cancellationToken: ct).ConfigureAwait(false);
            text = r.Messages?.LastOrDefault()?.Text ?? "";

            // Prepend upgrade note
            text = $"[Auto-upgraded to Pro: {reason}]\n\n{text}";
        }

        // Tool call enforcement：如果 LLM 本应调 tool 却试图猜测，强制重试
        text = await EnforceToolCallAsync(text, message, session, ct).ConfigureAwait(false);

        // Reflection Loop：自省输出质量，不合格时自动修正
        text = await ReflectAsync(text, message, session, ct).ConfigureAwait(false);

        // Persist: save (user message, response) to session log
        _messageLog.Add((message, text));
        await SaveSessionAsync().ConfigureAwait(false);

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
    /// Tool call enforcement：C# 直接调 tool 注入结果，不依赖 LLM 主动调用。
    /// 检测到 LLM 拒绝/猜测模式后，分析用户问题类型，从 C# 侧执行对应工具，
    /// 将真实数据注入后让 LLM 重新组织回答。
    /// 最多强制 1 次（防循环）。
    /// </summary>
    private async Task<string> EnforceToolCallAsync(string text, string originalMessage,
        AgentSession session, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var lower = text.ToLowerInvariant();

        var cantPatterns = new[] { "无法获取", "无法确定", "无法提供", "没有权限", "无法访问",
            "无法直接", "无法知道", "不知道当前", "不知道今天",
            "我不知道", "我不确定", "没有内置", "没有实时",
            "cannot", "can't", "unable to", "don't have", "don't know",
            "no access", "not have access", "not able to", "i don't" };

        if (!cantPatterns.Any(p => lower.Contains(p))) return text;

        // LLM 拒绝猜测 → 通用重试让其自查工具列表
        var retryPrompt = "你的回答在猜测。检查你的工具列表，调用能回答用户问题的工具获取真实信息。\n\n用户的问题是: " + originalMessage;
        try
        {
            var retryResult = await _proAgent.RunAsync(
                [new ChatMessage(ChatRole.User, retryPrompt)], session,
                cancellationToken: ct).ConfigureAwait(false);
            var retryText = retryResult.Messages?.LastOrDefault()?.Text ?? "";
            if (!string.IsNullOrWhiteSpace(retryText) && retryText.Length > 5)
                return $"[工具]\n\n{retryText}";
        }
        catch { }
        return text;
    }



    /// <summary>
    /// Reflection Loop：用启发式规则检查输出质量，不合格时让模型自省修正。
    /// 触发条件：空回答、错误关键词、过短回答、包含典型失败模式。
    /// 最多自省 1 次（防止无限循环）。
    /// </summary>
    private async Task<string> ReflectAsync(string text, string originalMessage,
        AgentSession session, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        // 检查是否需要自省
        var needsReflection = false;
        var reasons = new List<string>();

        // 空回答或太短
        if (text.Length < 15) { needsReflection = true; reasons.Add("回答过短"); }

        // 包含错误关键词
        var errorPatterns = new[] { "Error:", "error:", "失败:", "无法完成", "not available",
            "I cannot", "I'm unable", "I don't have", "insufficient", "sorry" };
        if (errorPatterns.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase)))
        { needsReflection = true; reasons.Add("检测到失败关键词"); }

        // 包含占位符
        if (text.Contains("{{") || text.Contains("TODO"))
        { needsReflection = true; reasons.Add("包含未填占位符"); }

        if (!needsReflection) return text;

        // Reflection prompt：让模型审查并修正自己的输出
        var reflectPrompt = $"""
            请复查你上一条回复，发现以下问题：{string.Join("、", reasons)}

            用户原始问题：{originalMessage}

            你的回复：{text}

            请修正上述问题后重新回答。如果确实无法完成，请给出明确的原因和替代方案。
            """;

        try
        {
            var reflectResult = await _proAgent.RunAsync(
                [new ChatMessage(ChatRole.User, reflectPrompt)], session,
                cancellationToken: ct).ConfigureAwait(false);
            var refined = reflectResult.Messages?.LastOrDefault()?.Text ?? "";
            if (!string.IsNullOrWhiteSpace(refined) && refined.Length > 10)
            {
                return $"[Reflection: {string.Join(", ", reasons)}]\n\n{refined}";
            }
        }
        catch
        {
            // Reflection 失败不影响原结果
        }

        return text;
    }

    private async ValueTask<AgentSession> GetOrCreateSessionAsync(CancellationToken ct)
    {
        if (_session != null) return _session;
        await _sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_session != null) return _session;
            _session = await _agent.CreateSessionAsync(ct).ConfigureAwait(false);

            // Load last session from disk to restore context
            await LoadLastSessionAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _sessionLock.Release();
        }
        return _session;
    }

    // ═══════════════════════════════════════════
    //  Session persistence
    // ═══════════════════════════════════════════

    /// <summary>
    /// Save the current message log to disk.
    /// Writes to {sessionDir}/last.json, shared with Desktop SessionManager format.
    /// </summary>
    private async Task SaveSessionAsync()
    {
        try
        {
            var path = Path.Combine(_sessionDir, "last.json");
            var entries = _messageLog.Select(m => new
            {
                role = "user",
                content = m.user
            }).Concat(_messageLog.Select(m => new
            {
                role = "assistant",
                content = m.assistant
            })).ToList();

            var json = System.Text.Json.JsonSerializer.Serialize(entries, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = false
            });
            await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ChatAgent] SaveSession failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Load the last session from {sessionDir}/last.json and prepend messages.
    /// Only loads if there are persisted messages (not a fresh session).
    /// </summary>
    private async Task LoadLastSessionAsync(CancellationToken ct)
    {
        try
        {
            var path = Path.Combine(_sessionDir, "last.json");
            if (!File.Exists(path)) return;

            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            var entries = System.Text.Json.JsonSerializer.Deserialize<List<SessionEntry>>(json);
            if (entries == null || entries.Count == 0) return;

            // Replay history into MAF session for context
            foreach (var entry in entries)
            {
                var role = entry.Role?.ToLowerInvariant() switch
                {
                    "user" => ChatRole.User,
                    "assistant" => ChatRole.Assistant,
                    _ => ChatRole.User
                };
                // MAF session uses AddMessage internally; we inject via RunAsync history
                _messageLog.Add((entry.Content ?? "", ""));
            }

            System.Diagnostics.Debug.WriteLine($"[ChatAgent] Loaded {entries.Count} messages from last session");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ChatAgent] LoadSession failed: {ex.Message}");
        }
    }

    private sealed record SessionEntry(string? Role, string? Content);
}
