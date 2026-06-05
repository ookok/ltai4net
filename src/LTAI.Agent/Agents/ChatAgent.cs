using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using LTAI.AI;
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

    public ChatAgent(AIAgent agent, AIAgent? proAgent = null, AgentWorkflows? workflows = null,
        BudgetTracker? budgetTracker = null,
        LocalEmbedder? localEmbedder = null, IHttpClientFactory? httpFactory = null,
        bool sameModel = false)
    {
        _agent = agent;
        _proAgent = proAgent ?? agent;
        _sameModel = sameModel;
        _workflows = workflows;
        _budgetTracker = budgetTracker;
        _localEmbedder = localEmbedder;
        _httpFactory = httpFactory;
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

    private async Task<(bool IsAdequate, string Reason)> JudgeResponseQualityAsync(
        string originalMessage, string response, CancellationToken ct)
    {
        var safeOriginal = JsonSerializer.Serialize(originalMessage);
        var safeResponse = JsonSerializer.Serialize(response);
        var jsonFormat = """{"adequate": true/false, "reason": "简短原因（中文）"}""";
        var judgePrompt = $"""
            你是一个回答质量审核员。判断以下AI回答是否合格。

            用户问题（JSON字符串）：{safeOriginal}

            AI回答（JSON字符串）：{safeResponse}

            用JSON格式返回：{jsonFormat}

            合格条件（全部满足则adequate=true）：
            1. 回答了用户的核心问题
            2. 没有拒绝回答或说"无法"
            3. 内容具体（有数据/事实/步骤，不是空话）
            4. 不编造数据（如果问题需要实时/本地信息，应该调用工具）

            不合格举例：
            - "我无法访问您的本地文件"而没有尝试调用文件工具
            - 编造了具体数据而没有工具调用支撑
            - 回答过于空泛（"这是一个复杂的问题"等）
            """;

        try
        {
            var judgeSession = await _agent.CreateSessionAsync(ct).ConfigureAwait(false);
            var judgeResult = await _agent.RunAsync(
                [new ChatMessage(ChatRole.User, judgePrompt)], judgeSession,
                cancellationToken: ct).ConfigureAwait(false);
            var judgeText = judgeResult.Messages?.LastOrDefault()?.Text ?? "";

            using var doc = JsonDocument.Parse(judgeText);
            var adequate = doc.RootElement.GetProperty("adequate").GetBoolean();
            var reason = doc.RootElement.GetProperty("reason").GetString() ?? "";
            return (adequate, reason);
        }
        catch
        {
            return (true, "judge failed, assume adequate");
        }
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

        var needsPro = text.Contains("<<<NEEDS_PRO:");

        if (!needsPro && text.Length > 20)
        {
            var lower = text.ToLowerInvariant();
            var failPatterns = new[] { "无法获取", "无法确定", "无法提供", "无法访问",
                "抱歉", "我无法", "暂时无法", "目前还不支持", "我不能",
                "cannot", "can't", "unable to", "don't know", "i don't" };
            if (failPatterns.Any(p => lower.Contains(p)))
            {
                System.Diagnostics.Debug.WriteLine("[ChatAgent] L1→L2 auto-upgrade triggered by refusal pattern");
                FailureRecorder.Record(message, text, "refusal pattern detected", "L1");
                needsPro = true;
            }
        }

        // Scheme 2: Tool traceability — 查询需要工具但 L1 没调用
        if (!needsPro && !isSimple && LikelyRequiresTool(message) && !HasToolResults(r))
            needsPro = true;

        // Scheme 1: LLM-as-Judge — 语义级质量评估
        if (!needsPro && !isSimple && text.Length > 50)
        {
            var (adequate, reason) = await JudgeResponseQualityAsync(message, text, ct).ConfigureAwait(false);
            if (!adequate)
            {
                FailureRecorder.Record(message, text, reason ?? "judge deemed inadequate", "L1");
                needsPro = true;
            }
        }

        if (needsPro)
        {
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

            r = await _proAgent.RunAsync(messages, session, cancellationToken: ct).ConfigureAwait(false);
            text = ApplyBlockedOutput(r.Messages?.LastOrDefault()?.Text ?? "");
            text = $"[Auto-upgraded to Pro: {reason}]\n\n{text}";
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
}
