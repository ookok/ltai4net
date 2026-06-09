using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LTAI.AI;
using LTAI.Agent.Formats;
using LTAI.Agent.FusionRoute;
using LTAI.Agent.Learning;
using LTAI.Agent.Tools;
using LTAI.Agent.Workflows;
using LTAI.Core.Safety;
using LTAI.Core.Session;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace LTAI.Agent;

public sealed class ChatAgent
{
    private static readonly HashSet<string> _simpleQueries = new(StringComparer.OrdinalIgnoreCase)
    {
        "hello", "hi", "hey", "你好", "嗨", "早上好", "下午好", "晚上好",
        "good morning", "good afternoon", "good evening",
        "who are you", "你是谁", "help", "帮助", "/help",
        "status", "状态", "/status", "thanks", "谢谢", "thank you"
    };
    private static readonly HashSet<string> _toolRequiredKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "search", "查找", "find", "lookup", "查询", "计算",
        "compile", "build", "run", "执行", "运行", "编译",
        "git", "commit", "push", "pull", "branch",
        "file", "文件", "read", "写", "write",
        "read", "代码", "code", "analyze", "分析",
        "翻译", "translate", "summarize", "总结",
        "draw", "画", "diagram", "图",
        "create", "创建", "delete", "删除", "update", "更新"
    };

    private readonly AIAgent _agent;
    private readonly AIAgent? _proAgent;
    private readonly AgentWorkflows? _workflows;
    private readonly BudgetTracker? _budgetTracker;
    private readonly LocalEmbedder? _localEmbedder;
    private readonly IHttpClientFactory? _httpFactory;
    private readonly bool _sameModel;

    // ── Response rendering ──

    public ChatAgent(AIAgent agent, AIAgent? proAgent = null, AgentWorkflows? workflows = null,
        BudgetTracker? budgetTracker = null,
        LocalEmbedder? localEmbedder = null, IHttpClientFactory? httpFactory = null,
        bool sameModel = false, IChatClient? steerJudge = null)
    {
        _agent = agent;
        _proAgent = proAgent;
        _workflows = workflows;
        _budgetTracker = budgetTracker;
        _localEmbedder = localEmbedder;
        _httpFactory = httpFactory;
        _sameModel = sameModel;
        _steerJudge = steerJudge;
    }

    private static readonly AsyncLocal<string> _traceId = new();
    private static string GetOrCreateTraceId() => _traceId.Value ??= Guid.NewGuid().ToString("N")[..12];

    /// <summary>P6: lightweight steer model for fast decisions (hallucination check, routing, suitability).</summary>
    private readonly IChatClient? _steerJudge;

    /// <summary>
    /// P6: Fast hallucination check using the steer model (or main LLM fallback).
    /// Returns true if answer appears grounded, false if likely hallucinated.
    /// </summary>
    private async Task<(bool IsAdequate, string? Reason)> JudgeResponseQualityAsync(
        string message, string response, CancellationToken ct)
    {
        if (_steerJudge == null)
            return (true, "no steer model configured — assuming adequate");
        var client = _steerJudge;
        try
        {
            var judgeMessages = new ChatMessage[]
            {
                new(ChatRole.System,
                    "You are a response quality judge. Given a user query and an AI response, " +
                    "determine if the response is adequate. " +
                    "Criteria: relevant, helpful, not vague/hedging, not refusing, not hallucinating.\n" +
                    "Respond with ONLY valid JSON like: {\"adequate\": true, \"reason\": \"...\", \"self_score\": 4}\n" +
                    "Score: 1-5 (5=excellent). Adequate if score >= 3 and reason indicates adequate."),
                new(ChatRole.User,
                    $"Query: {message}\n\nResponse: {response}")
            };
            var judgeResult = await client.GetResponseAsync(judgeMessages, cancellationToken: ct)
                .ConfigureAwait(false);
            var raw = judgeResult.Text ?? "";
            var start = raw.IndexOf('{');
            var end = raw.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
                var root = doc.RootElement;
                var adequate = root.TryGetProperty("adequate", out var a) && a.GetBoolean();
                var reason = root.TryGetProperty("reason", out var r) ? r.GetString() : null;
                var score = root.TryGetProperty("self_score", out var s) ? s.GetInt32() : 0;
                return (adequate && score >= 3, reason ?? (adequate ? null : "judge deemed inadequate"));
            }
            return (true, null);
        }
        catch
        {
            return (true, null); // On error, assume adequate (don't block the user)
        }
    }

    /// <summary>Build L1 exploration state for chain-of-thought routing.</summary>
    private static L1State BuildL1State(string message, string response, AgentResponse result)
    {
        var gap = EstimateCoverageGap(message, response);
        var state = new L1State
        {
            Label = gap > 0.4 ? "escalate" : "handled",
            SupportCount = CountSupportingEvidence(response),
            Gap = gap,
            ToolCalls = result.Messages?
                .SelectMany(m => m.Contents?.OfType<FunctionCallContent>() ?? [])
                .Select(fc => fc.Name ?? "")
                .Where(n => n.Length > 0)
                .ToList() ?? [],
            EscalationReason = gap > 0.5
                ? $"coverage gap={gap:F2}" : null
        };
        return state;
    }

    private static double EstimateCoverageGap(string message, string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return 1.0;
        var msgLower = message.ToLowerInvariant();
        var respLower = response.ToLowerInvariant();
        var keywords = msgLower.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 3)
            .ToHashSet();
        return keywords.Count == 0 ? 0 : (double)keywords.Count(k => !respLower.Contains(k)) / keywords.Count;
    }

    private static int CountSupportingEvidence(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var count = 0;
        var lines = text.Split('\n');
        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t.StartsWith('-') || t.StartsWith('*') || t.StartsWith("1.") || t.StartsWith("2."))
                count++;
        }
        return count;
    }

    private static int EstimateComplexity(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return 0;
        var parts = message.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var score = parts.Length switch
        {
            <= 5 => 1,
            <= 15 => 2,
            <= 30 => 3,
            <= 50 => 4,
            _ => 5
        };
        if (_toolRequiredKeywords.Any(k => message.Contains(k, StringComparison.OrdinalIgnoreCase)))
            score += 1;
        if (message.Contains('\n')) score += 1;
        if (message.Length > 200) score += 1;
        return Math.Min(score, 7);
    }

    private static double EstimateResponseEntropy(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 1.0;
        var hedgeWords = new[] { "maybe", "perhaps", "probably", "possibly", "might", "could be",
            "不确定", "可能", "也许", "大概", "估计", "似乎", "推测" };
        var count = hedgeWords.Count(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
        return Math.Min(1.0, count * 0.2);
    }

    private static double EstimateValueOfInformation(string query, string response, double entropy)
    {
        if (entropy > 0.5) return 0.8;
        if (string.IsNullOrWhiteSpace(response)) return 1.0;
        var qLower = query.ToLowerInvariant();
        var ambiguous = new[] { "what", "how", "why", "when", "where", "which",
            "什么", "如何", "怎么", "为什么", "何时", "哪里" };
        if (ambiguous.Any(w => qLower.Contains(w))) return 0.5 + entropy * 0.3;
        return entropy * 0.5;
    }

    /// <summary>
    /// 预热：本地嵌入模型懒加载。不再发送网络请求到外部 API，
    /// 因为 UI 初始化不应该依赖外部网络连通性。
    /// </summary>
    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        // 本地嵌入模型懒加载（仅在需要时触发，不阻塞 UI）
        if (_localEmbedder?.Available == false)
            _ = _localEmbedder.Dim;

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>F14: Check safety block flag and replace output if unsafe.</summary>
    private static string ApplyBlockedOutput(string text)
    {
        var reason = SafetyCoordinator.ConsumeBlock();
        if (reason != null)
            return $"[Content blocked by safety filter. Reason: {reason}]";
        return text;
    }

    public async Task<string> ChatAsync(string message, ISessionHandle? sessionHandle = null,
        string? userId = null, CancellationToken ct = default)
    {
        userId ??= "default";
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
        var session = sessionHandle != null
            ? await CreateAgentSessionFromHandleAsync(sessionHandle, ct).ConfigureAwait(false)
            : await _agent.CreateSessionAsync(ct).ConfigureAwait(false);
        var trimmed = message.Trim();
        var isSimple = trimmed.Length <= 10 || _simpleQueries.Contains(trimmed);
        var messages = new[] { new ChatMessage(ChatRole.User, message) };

        // F1+F13: Scope PlanState and BackgroundJobService to this session
        PlanTools.SessionId = sessionHandle?.Name ?? traceId;
        BackgroundJobService.CurrentSessionId = sessionHandle?.Name ?? traceId;

        // P1: Check for embedding model switch notification
        var switchMsg = LocalEmbedderModelSwitchNotifier.ConsumeSwitchMessage();
        if (switchMsg != null)
        {
            // Embedder model switch notification consumed — no Debug.WriteLine needed
        }

        // Pro 快速通道：复杂度 >= 4 直接走 Pro，不经过 L1
        if (!isSimple && EstimateComplexity(message) >= 4)
        {
            var proSession = sessionHandle != null
                ? await CreateAgentSessionFromHandleAsync(sessionHandle, ct).ConfigureAwait(false)
                : await _proAgent.CreateSessionAsync(ct).ConfigureAwait(false);
            var proR = await _proAgent.RunAsync(messages, proSession, cancellationToken: ct).ConfigureAwait(false);
            var proText = ApplyBlockedOutput(proR.Messages?.LastOrDefault()?.Text ?? "");
            if (sessionHandle != null)
                await SaveSessionToHandleAsync(proSession, sessionHandle, ct).ConfigureAwait(false);
            return proText;
        }

        var r = await _agent.RunAsync(messages, session, cancellationToken: ct).ConfigureAwait(false);
        var text = ApplyBlockedOutput(r.Messages?.LastOrDefault()?.Text ?? "");

        if (isSimple)
        {
            if (sessionHandle != null)
                await SaveSessionToHandleAsync(session, sessionHandle, ct).ConfigureAwait(false);
            return text;
        }

        // ── GoS-inspired L1State extraction ──
        var l1State = BuildL1State(message, text, r);

        // #7 EDRM: entropy-based routing — before full escalation check
        var edrmEntropy = EstimateResponseEntropy(text);
        var voi = EstimateValueOfInformation(message, text, edrmEntropy);

        // ── FusionRoute: span-level uncertainty analysis ──
        var spanRouter = new ResponseSpanRouter();
        l1State.Spans = spanRouter.ParseSpans(text,
            l1State.ToolCalls.Count > 0 ? l1State.ToolCalls.ToArray() : null);
        l1State.SpanUncertaintyRatio = l1State.Spans.Count > 0
            ? (double)l1State.Spans.Count(s => s.UncertaintyScore >= 0.4) / l1State.Spans.Count
            : 0;

        // #2 UCCI: calibration — merge entropy + L1State gap into calibrated score
        var calibratedScore = edrmEntropy * 0.4 + l1State.Gap * 0.4 - l1State.SupportCount * 0.05;
        calibratedScore = Math.Clamp(calibratedScore, 0.0, 1.0);
        var needsPro = l1State.ShouldEscalate || calibratedScore > 0.6 || voi > 0.5;

        // Traditional signals merged into L1State: explicit escalation marker
        if (!needsPro && text.Contains("<<<NEEDS_PRO:"))
            needsPro = true;

        // LLM-as-Judge (GoS: cognitive-to-symbolic evidence grounding)
        if (!needsPro && !isSimple && text.Length > 50)
        {
            var (adequate, reason) = await JudgeResponseQualityAsync(message, text, ct).ConfigureAwait(false);
            if (!adequate)
            {
                FailureRecorder.Record(message, text, reason ?? "judge deemed inadequate", "L1");
                needsPro = true;
                l1State.Label = "escalate";
                l1State.EscalationReason = reason;
            }
        }

        if (needsPro)
        {
            var reason = l1State.EscalationReason ?? "complex task";
            if (text.Contains("<<<NEEDS_PRO:"))
            {
                var match = Regex.Match(text, @"<<<NEEDS_PRO:\s*(.+?)>>>");
                if (match.Success) reason = match.Groups[1].Value.Trim();
            }

            // FusionRoute: prefer span-level routing over full regeneration
            if (l1State.ShouldRouteBySpans && !text.Contains("<<<NEEDS_PRO:") &&
                !reason.Contains("declined") && !reason.Contains("refusal"))
            {
                text = await TrySpanRoutingAsync(message, text, l1State, session, ct).ConfigureAwait(false);
            }

            // If span routing returned original text (didn't start with FusionRoute marker),
            // or wasn't attempted, do full regeneration
            if (!text.StartsWith("[FusionRoute"))
            {
                text = await FullRegenerationAsync(message, reason, l1State, session, ct).ConfigureAwait(false);
            }
        }

        text = await EnforceAndReflectAsync(text, message, session, ct).ConfigureAwait(false);
        if (sessionHandle != null)
            await SaveSessionToHandleAsync(session, sessionHandle, ct).ConfigureAwait(false);
        var pendingSwitch = LocalEmbedderModelSwitchNotifier.ConsumeSwitchMessage();
        return pendingSwitch != null ? $"{pendingSwitch}\n\n{text}" : text;
    }

    public async IAsyncEnumerable<AgentResponseUpdate> ChatStreamingAsync(
        string message, ISessionHandle? sessionHandle = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var session = sessionHandle != null
            ? await CreateAgentSessionFromHandleAsync(sessionHandle, ct).ConfigureAwait(false)
            : await _agent.CreateSessionAsync(ct).ConfigureAwait(false);

        // F1+F13: Scope PlanState and BackgroundJobService to this session
        PlanTools.SessionId = sessionHandle?.Name;
        BackgroundJobService.CurrentSessionId = sessionHandle?.Name;

        var toolResultCount = 0;
        var lastSaveAt = DateTime.UtcNow;

        await foreach (var update in _agent.RunStreamingAsync(
            [new ChatMessage(ChatRole.User, message)], session, cancellationToken: ct).ConfigureAwait(false))
        {
            if (update.Contents is { Count: > 0 })
            {
                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case FunctionCallContent fc when !string.IsNullOrEmpty(fc.Name):
                            LTAI.Core.Configuration.UsageTracker.RecordToolCall();
                            LTAI.Core.Configuration.UsageTracker.SetActiveTool(fc.Name);
                            LTAI.Core.Configuration.UsageTracker.StartToolTimer();
                            yield return new AgentResponseUpdate(ChatRole.Assistant, $"⏳ 正在调用 {fc.Name}...\n");
                            break;
                        case FunctionResultContent frc:
                            LTAI.Core.Configuration.UsageTracker.StopToolTimer();
                            var preview = frc.Result?.ToString() ?? "(null)";
                            if (preview.Length > 200) preview = preview[..200] + "...";
                            yield return new AgentResponseUpdate(ChatRole.Assistant, $"  ✅ 返回: {preview}\n");

                            // 增量保存：每 3 个工具结果或 30s 间隔做一次 checkpoint
                            toolResultCount++;
                            if (sessionHandle != null &&
                                (toolResultCount % 3 == 0 ||
                                 (DateTime.UtcNow - lastSaveAt).TotalSeconds >= 30))
                            {
                                await SaveSessionToHandleAsync(session, sessionHandle, ct).ConfigureAwait(false);
                                lastSaveAt = DateTime.UtcNow;
                            }
                            break;
                    }
                }
            }

            yield return update;
        }

        // F14: Check safety block flag after streaming completes
        var blockedReason = SafetyCoordinator.ConsumeBlock();
        if (blockedReason != null)
        {
            yield return new AgentResponseUpdate(ChatRole.Assistant,
                $"\n\n[Content blocked by safety filter. Reason: {blockedReason}]");
        }

        LTAI.Core.Configuration.UsageTracker.SetActiveTool("");

        // P2.1: inject citation chips for @AgentName mentions
        try
        {
            var citationPattern = new System.Text.RegularExpressions.Regex(
                @"@(LTAI-\w+|sql-agent|code-agent)");
        }
        catch { }

        if (sessionHandle != null)
            await SaveSessionToHandleAsync(session, sessionHandle, ct).ConfigureAwait(false);
    }

    public Task<AgentResponse> RunWorkflowAsync(string task, CancellationToken ct = default)
    {
        if (_workflows == null)
            return Task.FromResult(new AgentResponse(
                new ChatMessage(ChatRole.Assistant, "Workflow orchestrator not available.")));
        return _workflows.RunHandoffAsync(task, traceId: GetOrCreateTraceId(), ct: ct);
    }

    public Task<string> RunSequentialAsync(string[] agentNames, string task, CancellationToken ct = default)
    {
        if (_workflows == null)
            return Task.FromResult("Workflow orchestrator not available.");
        return _workflows.RunSequentialAsync(agentNames, task, traceId: GetOrCreateTraceId(), ct: ct);
    }

    private static readonly AsyncLocal<int> _correctionDepth = new();

    private async Task<string> EnforceAndReflectAsync(string text, string originalMessage,
        AgentSession session, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        // P2: Prevent recursive correction loops — max 2 rounds
        _correctionDepth.Value++;
        if (_correctionDepth.Value > 2)
        {
            _correctionDepth.Value = 0;
            return text;
        }
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

        var safeOriginal = JsonSerializer.Serialize(originalMessage);
        var stage1Prompt = $"""
            - 不要拒绝、猜测或编造
            - 确保回答完整（不含占位符）

            用户原始问题（JSON字符串）：{safeOriginal}

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

            var stage2Prompt = $"你必须使用工具来回答用户问题。不要拒绝、不要猜测。\n\n用户问题是（JSON字符串）: {safeOriginal}";
            var result2 = await _proAgent.RunAsync(
                [new ChatMessage(ChatRole.User, stage2Prompt)], session,
                cancellationToken: ct).ConfigureAwait(false);
            var refined2 = result2.Messages?.LastOrDefault()?.Text ?? "";

            if (!string.IsNullOrWhiteSpace(refined2) && refined2.Length > 10)
                return $"[工具]\n\n{refined2}";
        }
        catch { }

        return text;
    }

    private async Task<AgentSession> CreateAgentSessionFromHandleAsync(ISessionHandle handle, CancellationToken ct)
    {
        var json = handle.SerializeToJson();
        if (string.IsNullOrEmpty(json))
            return await _agent.CreateSessionAsync(ct).ConfigureAwait(false);

        var element = JsonDocument.Parse(json).RootElement;
        return await _agent.DeserializeSessionAsync(element, cancellationToken: ct).ConfigureAwait(false);
    }

    private async Task SaveSessionToHandleAsync(AgentSession session, ISessionHandle handle, CancellationToken ct)
    {
        var json = await _agent.SerializeSessionAsync(session, cancellationToken: ct).ConfigureAwait(false);
        handle.UpdateFromJson(json.GetRawText());
    }

    // ── FusionRoute span-level routing ──

    private async Task<string> TrySpanRoutingAsync(
        string message, string originalText, L1State l1State, AgentSession session, CancellationToken ct)
    {
        var spanRouter = new ResponseSpanRouter();
        var refinePrompt = l1State.ToSpanRoutingHandoff(message);
        var result = await _proAgent.RunAsync(
            [new ChatMessage(ChatRole.User, refinePrompt)], session,
            cancellationToken: ct).ConfigureAwait(false);
        var refined = ApplyBlockedOutput(result.Messages?.LastOrDefault()?.Text ?? "");

        if (string.IsNullOrWhiteSpace(refined) || refined.Length <= 10)
            return originalText;

        var refinedSpans = spanRouter.ParseSpans(refined);
        var stitched = spanRouter.Stitch(l1State.Spans,
            l1State.Spans.Where(s => s.UncertaintyScore >= 0.4).ToList(),
            refinedSpans.Select(s => s.Text).ToList());
        return $"[FusionRoute: refined {l1State.Spans.Count(s => s.UncertaintyScore >= 0.4)}/{l1State.Spans.Count} spans]\n\n{stitched}";
    }

    private async Task<string> FullRegenerationAsync(
        string message, string reason, L1State l1State, AgentSession session, CancellationToken ct)
    {
        var l1Handoff = l1State.ToHandoff(ResultFormat.Toon);
        var l2Messages = new[]
        {
            new ChatMessage(ChatRole.System,
                "You are the Pro assistant. A Flash assistant attempted this query " +
                "but could not produce a satisfactory answer. Below is the structured " +
                "exploration state from the Flash attempt.\n\n" + l1Handoff),
            new ChatMessage(ChatRole.User,
                $"The Flash assistant escalated for reason: {reason}\n\n" +
                $"Original query: {message}")
        };

        var result = await _proAgent.RunAsync(l2Messages, session, cancellationToken: ct).ConfigureAwait(false);
        var text = ApplyBlockedOutput(result.Messages?.LastOrDefault()?.Text ?? "");
        return $"[Auto-upgraded to Pro: {reason}]\n\n{text}";
    }
}
