using System.Runtime.CompilerServices;
using System.Text.Json;
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
    private readonly AIAgent _agent;
    private readonly AIAgent? _proAgent;
    private readonly AgentWorkflows? _workflows;
    private readonly BudgetTracker? _budgetTracker;
    private readonly LocalEmbedder? _localEmbedder;
    private readonly IEscalationDecider _escalationDecider;
    private readonly IChatClient? _steerJudge;

    public ChatAgent(AIAgent agent, AIAgent? proAgent = null, AgentWorkflows? workflows = null,
        BudgetTracker? budgetTracker = null,
        LocalEmbedder? localEmbedder = null, IHttpClientFactory? httpFactory = null,
        bool sameModel = false, IChatClient? steerJudge = null,
        IEscalationDecider? escalationDecider = null)
    {
        _agent = agent;
        _proAgent = proAgent;
        _workflows = workflows;
        _budgetTracker = budgetTracker;
        _localEmbedder = localEmbedder;
        _httpFactory = httpFactory;
        _sameModel = sameModel;
        _steerJudge = steerJudge;
        _escalationDecider = escalationDecider ?? new DefaultEscalationDecider();
    }

    private static readonly AsyncLocal<string> _traceId = new();
    private static string GetOrCreateTraceId() => _traceId.Value ??= Guid.NewGuid().ToString("N")[..12];

    private readonly IHttpClientFactory? _httpFactory;
    private readonly bool _sameModel;

    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        if (_localEmbedder?.Available == false)
            _ = _localEmbedder.Dim;
        await Task.CompletedTask.ConfigureAwait(false);
    }

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
        var isSimple = _escalationDecider.IsSimpleQuery(message);
        var complexity = _escalationDecider.EstimateComplexity(message);
        var messages = new[] { new ChatMessage(ChatRole.User, message) };

        PlanTools.SessionId = sessionHandle?.Name ?? traceId;
        BackgroundJobService.CurrentSessionId = sessionHandle?.Name ?? traceId;

        // Pro 快速通道：复杂度 >= 4 直接走 Pro，不经过 L1
        if (!isSimple && complexity >= 4 && _proAgent != null)
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

        // ── L1State extraction ──
        var l1State = BuildL1State(message, text, r);

        // ── Entropy & Value of Information ──
        var edrmEntropy = EstimateResponseEntropy(text);
        var voi = EstimateValueOfInformation(message, text, edrmEntropy);

        // ── FusionRoute: span-level uncertainty analysis ──
        var spanRouter = new ResponseSpanRouter();
        l1State.Spans = spanRouter.ParseSpans(text,
            l1State.ToolCalls.Count > 0 ? l1State.ToolCalls.ToArray() : null);
        l1State.SpanUncertaintyRatio = l1State.Spans.Count > 0
            ? (double)l1State.Spans.Count(s => s.UncertaintyScore >= 0.4) / l1State.Spans.Count
            : 0;

        // ── LLM-as-Judge ──
        var judgeInadequate = false;
        string? judgeReason = null;
        if (text.Length > 50)
        {
            var (adequate, jReason) = await JudgeResponseQualityAsync(message, text, ct).ConfigureAwait(false);
            if (!adequate)
            {
                FailureRecorder.Record(message, text, jReason ?? "judge deemed inadequate", "L1");
                judgeInadequate = true;
                judgeReason = jReason;
            }
        }

        // ── Escalation decision (via IEscalationDecider) ──
        var (needsPro, reason, _) = _escalationDecider.Evaluate(
            message, text, l1State, edrmEntropy, voi, judgeInadequate, judgeReason);

        if (needsPro && _proAgent != null)
        {
            // FusionRoute: prefer span-level routing over full regeneration
            var hasExplicitSignal = EscalationSignal.FromString(text) != null;
            if (l1State.ShouldRouteBySpans && !hasExplicitSignal &&
                !reason.Contains("declined") && !reason.Contains("refusal"))
            {
                text = await TrySpanRoutingAsync(message, text, l1State, session, ct).ConfigureAwait(false);
            }

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

    // ── Quality Judge (LLM-as-Judge) ──

    private async Task<(bool IsAdequate, string? Reason)> JudgeResponseQualityAsync(
        string message, string response, CancellationToken ct)
    {
        if (_steerJudge == null)
            return (true, "no steer model configured — assuming adequate");
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
                new(ChatRole.User, $"Query: {message}\n\nResponse: {response}")
            };
            var judgeResult = await _steerJudge.GetResponseAsync(judgeMessages, cancellationToken: ct)
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
            return (true, null);
        }
    }

    // ── L1State / Entropy / Coverage ──

    private static L1State BuildL1State(string message, string response, AgentResponse result)
    {
        var gap = EstimateCoverageGap(message, response);
        return new L1State
        {
            Label = gap > 0.4 ? "escalate" : "handled",
            SupportCount = CountSupportingEvidence(response),
            Gap = gap,
            ToolCalls = result.Messages?
                .SelectMany(m => m.Contents?.OfType<FunctionCallContent>() ?? [])
                .Select(fc => fc.Name ?? "")
                .Where(n => n.Length > 0)
                .ToList() ?? [],
            EscalationReason = gap > 0.5 ? $"coverage gap={gap:F2}" : null
        };
    }

    private static double EstimateCoverageGap(string message, string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return 1.0;
        var msgLower = message.ToLowerInvariant();
        var respLower = response.ToLowerInvariant();
        var keywords = msgLower.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 3).ToHashSet();
        return keywords.Count == 0 ? 0 : (double)keywords.Count(k => !respLower.Contains(k)) / keywords.Count;
    }

    private static int CountSupportingEvidence(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var count = 0;
        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith('-') || t.StartsWith('*') || t.StartsWith("1.") || t.StartsWith("2."))
                count++;
        }
        return count;
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

    // ── Correction loop ──

    private static readonly AsyncLocal<int> _correctionDepth = new();

    private async Task<string> EnforceAndReflectAsync(string text, string originalMessage,
        AgentSession session, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        _correctionDepth.Value++;
        if (_correctionDepth.Value > 2) { _correctionDepth.Value = 0; return text; }

        if (_proAgent == null) return text;
        if (!_escalationDecider.ContainsRefusalPatterns(text) && text.Length >= 15
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

    // ── Session helpers ──

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

    // ── FusionRoute & Full Regeneration ──

    private async Task<string> TrySpanRoutingAsync(
        string message, string originalText, L1State l1State, AgentSession session, CancellationToken ct)
    {
        if (_proAgent == null) return originalText;
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
        if (_proAgent == null) return message;
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
