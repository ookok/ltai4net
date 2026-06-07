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
        "你好", "hi", "hello", "hey", "嗨",
        "早上好", "下午好", "晚上好", "午安", "晚安",
        "help", "status", "clear", "ping", "test",
        "thanks", "谢谢", "thank you", "thank u", "多谢", "感谢",
        "bye", "再见", "拜拜", "goodbye",
        "yes", "no", "ok", "okay", "好的", "嗯", "哦",
        "who are you", "你是谁",
        "?", "？", "！", "!", "",
    };

    private static readonly HashSet<string> _toolRequiredKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "file", "directory", "folder", "script",
        "文件", "目录", "文件夹", "脚本",
        "run", "执行", "运行",
        "list", "search", "grep", "read", "write", "create",
        "列出", "搜索", "查找", "读取", "写入", "创建",
        "compile", "build", "编译",
        "git", "commit", "push", "pull",
        "network", "port", "docker", "container",
        "进程", "process", "system", "系统",
        "shell", "cmd", "powershell",
        "杀", "启动", "停止", "kill", "start", "stop",
    };

    internal static string GetOrCreateTraceId() =>
        Activity.Current?.Id ?? Guid.NewGuid().ToString("n");
    private readonly AIAgent _agent;
    private readonly AIAgent _proAgent;
    private readonly bool _sameModel;
    private readonly AgentWorkflows? _workflows;
    private readonly BudgetTracker? _budgetTracker;
    private readonly LocalEmbedder? _localEmbedder;
    private readonly IHttpClientFactory? _httpFactory;
    private readonly IChatClient? _steerJudge;

    public ChatAgent(AIAgent agent, AIAgent? proAgent = null, AgentWorkflows? workflows = null,
        BudgetTracker? budgetTracker = null,
        LocalEmbedder? localEmbedder = null, IHttpClientFactory? httpFactory = null,
        bool sameModel = false, IChatClient? steerJudge = null)
    {
        _agent = agent;
        _proAgent = proAgent ?? agent;
        _sameModel = sameModel;
        _workflows = workflows;
        _budgetTracker = budgetTracker;
        _localEmbedder = localEmbedder;
        _httpFactory = httpFactory;
        _steerJudge = steerJudge;
    }

    private static int EstimateComplexity(string message)
    {
        var score = 0;

        // 长度因子
        score += message.Length > 200 ? 2 : message.Length > 50 ? 1 : 0;

        // 高认知词：优化/分析/架构/设计/重构等
        var highCogWords = Regex.Matches(message,
            @"\b(优化|分析|架构|设计|重构|review|trace|debug|profile|" +
            "refactor|design pattern|architecture|performance|security|" +
            "并发|线程安全|性能|安全|架构|模式|设计|优化|重构)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        score += Math.Min(highCogWords.Count, 3);

        // 因果/对比推理
        var causalWords = Regex.Matches(message,
            @"\b(because|why|compare|vs|差异|区别|什么原因|" +
            "为什么|对比|原因|影响|关系)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        score += Math.Min(causalWords.Count, 2);

        // 代码块引用
        if (message.Contains("```")) score += 2;

        // 多行输入（代码片段、日志等）
        if (message.Count(c => c == '\n') > 5) score += 2;

        return score;
    }

    private static bool LikelyRequiresTool(string message)
    {
        return _toolRequiredKeywords.Any(k =>
            message.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasToolResults(AgentResponse r)
    {
        if (r.Messages == null) return false;
        return r.Messages.Any(m =>
            m.Contents?.OfType<FunctionResultContent>().Any() == true);
    }

    /// <summary>
    /// GoS-inspired L1State builder: extracts structured belief state from
    /// L1's free-text response and tool call history.
    /// </summary>
    private static L1State BuildL1State(string message, string text, AgentResponse r)
    {
        var toolCalls = new List<string>();
        if (r.Messages != null)
        {
            foreach (var msg in r.Messages)
            {
                var calls = msg.Contents?.OfType<FunctionCallContent>();
                if (calls != null)
                    toolCalls.AddRange(calls.Select(c => c.Name ?? c.CallId));
            }
        }

        // Heuristic: extract candidate entities from response
        var candidates = new List<(string name, string kind, double score)>();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines.Take(10))
        {
            var trimmed = line.TrimStart('-', ' ', '*');
            var parts = trimmed.Split(':');
            if (parts.Length >= 2)
            {
                var name = parts[0].Trim();
                if (name.Length > 1 && name.Length < 80)
                {
                    candidates.Add((name, "concept", 1.0 / candidates.Count + 1));
                }
            }
        }

        // Default: the response itself as a single candidate
        if (candidates.Count == 0)
        {
            var title = text.Length > 60 ? text[..60] + "..." : text;
            candidates.Add((title, "response", 1.0));
        }

        var gap = candidates.Count >= 2
            ? Math.Round(candidates[0].score - candidates[1].score, 2)
            : 1.0;

        var msgCount = r.Messages?.Count ?? 1;
        var supportCount = toolCalls.Count + (text.Length / 200);

        // Determine label
        string? label = null;
        string? reason = null;

        if (LikelyRequiresTool(message) && toolCalls.Count == 0)
        {
            label = "escalate";
            reason = "query requires tools but L1 made no tool calls";
            FailureRecorder.Record(message, text, reason, "L1");
        }
        else if (text.Length > 20)
        {
            var lower = text.ToLowerInvariant();
            var failPatterns = new[] { "无法获取", "无法确定", "无法提供", "无法访问",
                "抱歉", "我无法", "暂时无法", "目前还不支持", "我不能",
                "cannot", "can't", "unable to", "don't know", "i don't" };
            if (failPatterns.Any(p => lower.Contains(p)))
            {
                label = "escalate";
                reason = "L1 declined to answer";
                FailureRecorder.Record(message, text, "refusal pattern detected", "L1");
            }
        }

        return new L1State
        {
            Candidates = candidates,
            Gap = gap,
            SupportCount = supportCount,
            StepsTaken = msgCount,
            Label = label ?? (gap < 0.3 && supportCount < 2 ? "escalate" : "report"),
            EscalationReason = reason ?? (gap < 0.3 && supportCount < 2
                ? $"low confidence (gap={gap:F2}, support={supportCount})" : null),
            L1Response = text,
            ToolCalls = toolCalls,
        };
    }

    private async Task<(bool IsAdequate, string Reason)> JudgeResponseQualityAsync(
        string originalMessage, string response, CancellationToken ct)
    {
        // #8 SEE: self-evaluation — use a non-interpolated prompt to avoid
        // brace-escaping issues with JSON content.
        var safeResponse = JsonSerializer.Serialize(response);
        var seePrompt = "Rate your OWN response above on a scale of 1-5.\n"
            + "Respond with ONLY valid JSON like: {\"adequate\": true, \"reason\": \"...\", \"self_score\": 4}\n\n"
            + "Criteria (all must be true for adequate=true):\n"
            + "1. Directly answers the user's core question\n"
            + "2. Does NOT refuse or say \"unable to\"\n"
            + "3. Contains specific information (data, facts, steps)\n"
            + "4. If real-time/local data is needed, tools should have been called\n\n"
            + "Your response to evaluate: " + safeResponse;

        try
        {
            // Reuse the existing session — no separate judge invocation
            var judgeSession = await _agent.CreateSessionAsync(ct).ConfigureAwait(false);
            var judgeResult = await _agent.RunAsync(
                [new ChatMessage(ChatRole.User, seePrompt)], judgeSession,
                cancellationToken: ct).ConfigureAwait(false);
            var judgeText = judgeResult.Messages?.LastOrDefault()?.Text ?? "";

            using var doc = JsonDocument.Parse(judgeText);
            var adequate = doc.RootElement.GetProperty("adequate").GetBoolean();
            var reason = doc.RootElement.GetProperty("reason").GetString() ?? "";
            var selfScore = doc.RootElement.TryGetProperty("self_score", out var s) ? s.GetInt32() : 3;

            // #7 EDRM: estimate entropy from response, combine with self_score
            var entropy = EstimateResponseEntropy(response);
            if (entropy > 0.6 && selfScore < 3)
                adequate = false; // high entropy + low confidence → escalate

            return (adequate, reason);
        }
        catch
        {
            return (true, "self-eval failed, assume adequate");
        }
    }

    /// <summary>#7 EDRM: heuristic entropy from response — hedging density + length variance.</summary>
    private static double EstimateResponseEntropy(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 10) return 0;
        var lower = text.ToLowerInvariant();
        var hedgeWords = new[] { "maybe", "perhaps", "probably", "possibly", "might", "could be",
            "不确定", "可能", "也许", "大概", "估计", "似乎", "推测" };
        var hedgeCount = hedgeWords.Count(w => lower.Contains(w));
        var hedgeDensity = (double)hedgeCount / Math.Max(1, text.Length / 50);

        // Sentence-length variance: high variance = mixed confidence
        var sentences = text.Split('.', '!', '?', '。', '！', '？');
        var avgLen = sentences.Average(s => (double)s.Length);
        var variance = sentences.Average(s => Math.Pow(s.Length - avgLen, 2));
        var normVar = Math.Min(1, variance / 1000);

        return Math.Min(1, hedgeDensity * 0.4 + normVar * 0.6);
    }

    /// <summary>#2 UCCI: calibrated uncertainty from entropy + gap + support.</summary>
    private static double CalibrateUncertainty(double entropy, double gap, int support)
    {
        // Simple isotonic-style calibration: combine three signals with learned weights
        var eNorm = entropy;
        var gNorm = Math.Max(0, 1.0 - gap);          // low gap = high uncertainty
        var sNorm = Math.Max(0, 1.0 - support / 5.0); // low support = high uncertainty
        return Math.Min(1, 0.4 * eNorm + 0.35 * gNorm + 0.25 * sNorm);
    }

    /// <summary>#6 Bayesian: cost-aware value of information for routing.</summary>
    private static double EstimateValueOfInformation(string message, string response, double entropy)
    {
        // VoI = expected improvement from consulting L2 vs cost of L2 call
        var taskComplexity = Math.Min(1, message.Length / 500.0);
        var responseQuality = 1.0 - entropy;
        var improvementPotential = Math.Max(0, 1.0 - responseQuality) * taskComplexity;
        // Normalize: VoI > 0.5 means worth routing to L2
        return improvementPotential;
    }

    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        if (_localEmbedder?.Available == false)
            _ = _localEmbedder.Dim;

        try
        {
            using var warmCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            warmCts.CancelAfter(TimeSpan.FromSeconds(5));
            using var http = _httpFactory?.CreateClient("llm");
            if (http != null)
            {
                using var req = new HttpRequestMessage(HttpMethod.Options, "https://api.deepseek.com/v1/chat/completions");
                _ = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, warmCts.Token)
                    .ConfigureAwait(false);
            }
        }
        catch { }
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
            System.Diagnostics.Debug.WriteLine($"[ChatAgent] {switchMsg}");
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
        var calibratedScore = CalibrateUncertainty(edrmEntropy, l1State.Gap, l1State.SupportCount);
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
            你的回答存在以下问题，请修正：
            - 如果问题需要工具，调用工具获取真实数据
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

        return $"无法完成请求。问题超出了当前能力范围：需要获取实时数据但工具调用未成功。请稍后重试或简化您的请求。";
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
