using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LTAI.AI;
using LTAI.Agent.Caching;
using LTAI.Agent.CodeAnalysis;
using LTAI.Agent.Formats;
using LTAI.Agent.FusionRoute;
using LTAI.Agent.LanguageServer;
using LTAI.Agent.Learning;
using LTAI.Agent.Pipeline;
using LTAI.Agent.Pipeline.Steps;
using LTAI.Agent.Tools;
using LTAI.Agent.Workflows;
using LTAI.Core.Configuration;
using LTAI.Core.Safety;
using LTAI.Core.Session;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent;

public sealed partial class ChatAgent
{
    private readonly AIAgent _agent;
    private readonly AIAgent? _proAgent;
    private readonly AgentWorkflows? _workflows;
    private readonly BudgetTracker? _budgetTracker;
    private readonly LocalEmbedder? _localEmbedder;
    private readonly IEscalationDecider _escalationDecider;
    private readonly IChatClient? _steerJudge;
    private readonly IHttpClientFactory? _httpFactory;
    private readonly QuestionService? _questionService;
    private readonly int _judgeConfidenceThreshold;
    private readonly SmartRetryController _retryController = new();
    private readonly IMemoryCachingStore? _checkpointStore;
    private readonly ConcurrentDictionary<string, int> _sessionCheckpointCounters = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionCheckpointLocks = new(StringComparer.Ordinal);
    private readonly bool _sameModel;
    private readonly TreeSitterParser? _tsParser;
    private readonly LspLanguageManager? _lspManager;
    private readonly int _complexityProFastTrack;
    private readonly int _grammarRetryMaxDepth;
    private readonly int _correctionLoopMaxDepth;
    private int _sessionMaxErrors = 5;
    private TimeSpan _sessionCircuitDuration = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, PerSessionErrorState> _sessionErrorStates = new(StringComparer.Ordinal);
    private readonly GrammarCheckStep _grammarCheck;
    private readonly PipelineRunner _pipelineRunner;
    private readonly ILogger _logger;

    private sealed class PerSessionErrorState
    {
        public int ErrorCount;
        public DateTime? CircuitOpenUntil;
    }

    private static readonly AsyncLocal<int> _correctionDepth = new();
    private static readonly AsyncLocal<int> _grammarDepth = new();

    private static readonly HashSet<string> FileToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "write", "edit", "writefile", "editfile", "create", "createfile",
        "writetool", "filewritetool", "editfiletool"
    };

    private static readonly string[] ModeCycle = ["chat", "plan", "execute"];

    public ChatAgent(AIAgent agent, AIAgent? proAgent = null, AgentWorkflows? workflows = null,
        BudgetTracker? budgetTracker = null,
        LocalEmbedder? localEmbedder = null, IHttpClientFactory? httpFactory = null,
        bool sameModel = false, IChatClient? steerJudge = null,
        IEscalationDecider? escalationDecider = null,
        QuestionService? questionService = null,
        int judgeConfidenceThreshold = 3,
        TreeSitterParser? tsParser = null,
        LspLanguageManager? lspManager = null,
        IMemoryCachingStore? checkpointStore = null,
        EscalationConfig? escalationConfig = null,
        GrammarCheckStep? grammarCheck = null,
        PipelineRunner? pipelineRunner = null,
        ILogger<ChatAgent>? logger = null)
    {
        var cfg = escalationConfig ?? new EscalationConfig();
        _agent = agent;
        _proAgent = proAgent;
        _workflows = workflows;
        _budgetTracker = budgetTracker;
        _localEmbedder = localEmbedder;
        _httpFactory = httpFactory;
        _sameModel = sameModel;
        _steerJudge = steerJudge;
        _escalationDecider = escalationDecider ?? new DefaultEscalationDecider(cfg);
        _questionService = questionService;
        _judgeConfidenceThreshold = Math.Clamp(judgeConfidenceThreshold, 1, 5);
        _tsParser = tsParser;
        _lspManager = lspManager;
        _checkpointStore = checkpointStore;
        _grammarCheck = grammarCheck ?? new GrammarCheckStep(tsParser: tsParser, lspManager: lspManager);
        _pipelineRunner = pipelineRunner ?? new PipelineRunner(new IPipelineStep[] { _grammarCheck });
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ChatAgent>.Instance;
        _complexityProFastTrack = cfg.ComplexityProFastTrack;
        _grammarRetryMaxDepth = cfg.GrammarRetryMaxDepth;
        _correctionLoopMaxDepth = cfg.CorrectionLoopMaxDepth;
        _sessionMaxErrors = cfg.SessionMaxErrors;
        _sessionCircuitDuration = TimeSpan.FromMinutes(cfg.SessionCircuitDurationMinutes);
    }

    private static string GetOrCreateTraceId() => Guid.NewGuid().ToString("N")[..12];

    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        if (_localEmbedder?.Available == false)
            _ = _localEmbedder.Dim;
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task<string> ChatAsync(string message, ISessionHandle? sessionHandle = null,
        string? userId = null, CancellationToken ct = default)
    {
        userId ??= "default";
        TaskTools.EvictStaleSessions();
        PlanTools.EvictStaleSessions();

        if (_budgetTracker != null)
        {
            var estimatedTokens = Math.Max(10, TokenEstimator.Estimate(message));
            var (allowed, remaining) = _budgetTracker.TryConsume(userId, estimatedTokens);
            if (!allowed)
                return $"⛔ Token budget exceeded. Remaining budget: {remaining} tokens. Please wait for budget reset or contact your administrator.";
        }

        var traceId = GetOrCreateTraceId();
        var session = sessionHandle != null
            ? await CreateAgentSessionFromHandleAsync(sessionHandle, ct).ConfigureAwait(false)
            : await _agent.CreateSessionAsync(ct).ConfigureAwait(false);
        session = await TryRestoreFromCheckpointAsync(sessionHandle, session, ct).ConfigureAwait(false);
        var trimmed = message.Trim();
        var isSimple = _escalationDecider.IsSimpleQuery(message);
        var complexity = _escalationDecider.EstimateComplexity(message);
        var messages = new[] { new ChatMessage(ChatRole.User, message) };

        var pipelineCtx = new MessageContext(message, ct);
        pipelineCtx = await _pipelineRunner.RunPreGenerationAsync(pipelineCtx).ConfigureAwait(false);
        if (pipelineCtx.SafetyBlocked)
            return "⛔ Request blocked by safety filter.";
        if (pipelineCtx.GrammarCheckBlocked)
            return "⚠️ Pre-generation checks failed: grammar errors detected in input.";

        BackgroundJobService.CurrentSessionId = sessionHandle?.Name ?? traceId;

        if (!isSimple && complexity >= _complexityProFastTrack && _proAgent != null)
        {
            if (sessionHandle != null)
                await SaveSessionToHandleAsync(session, sessionHandle, ct).ConfigureAwait(false);
            var proSession = sessionHandle != null
                ? await CreateAgentSessionFromHandleAsync(sessionHandle, ct).ConfigureAwait(false)
                : await _proAgent.CreateSessionAsync(ct).ConfigureAwait(false);
            var proR = await _proAgent.RunAsync(messages, proSession, cancellationToken: ct).ConfigureAwait(false);
            var proText = ApplyBlockedOutput(proR.Messages?.LastOrDefault()?.Text ?? "");

            if (proR.Messages != null && proText.Length > 0)
            {
                var (hasErrors, errorMessages) = await PostGenerationGrammarCheckAsync(proR.Messages, ct).ConfigureAwait(false);
                if (hasErrors && errorMessages.Count > 0 && _grammarDepth.Value <= _grammarRetryMaxDepth)
                {
                    _grammarDepth.Value++;
                    var retryR = await _proAgent.RunAsync(errorMessages, proSession, cancellationToken: ct).ConfigureAwait(false);
                    var retryText = ApplyBlockedOutput(retryR.Messages?.LastOrDefault()?.Text ?? "");
                    if (!string.IsNullOrWhiteSpace(retryText)) proText = retryText;
                }
            }

            if (sessionHandle != null)
                await SaveSessionToHandleAsync(proSession, sessionHandle, ct).ConfigureAwait(false);
            return proText;
        }

        var r = await _agent.RunAsync(messages, session, cancellationToken: ct).ConfigureAwait(false);
        var text = ApplyBlockedOutput(r.Messages?.LastOrDefault()?.Text ?? "");

        if (sessionHandle != null)
            await SaveSessionToHandleAsync(session, sessionHandle, ct).ConfigureAwait(false);

        var ctxRatio = UsageTracker.ContextRatio();
        var sessionId = sessionHandle?.Name ?? traceId;

        if (r.Messages != null)
        {
            var (hasErrors, errorMessages) = await PostGenerationGrammarCheckAsync(r.Messages, ct).ConfigureAwait(false);
            if (hasErrors && errorMessages.Count > 0)
            {
                _grammarDepth.Value++;
                var result = ParseGrammarCheckResult(errorMessages);
                var decision = _retryController.Decide(result, _grammarDepth.Value);
                if (decision.Action != RetryAction.Continue)
                {
                    RecordSessionError(sessionId);
                    _grammarDepth.Value = 0;
                }
                else if (_grammarDepth.Value <= _grammarRetryMaxDepth)
                {
                    var retryR = await _agent.RunAsync(errorMessages, session, cancellationToken: ct).ConfigureAwait(false);
                    var retryText = ApplyBlockedOutput(retryR.Messages?.LastOrDefault()?.Text ?? "");
                    if (!string.IsNullOrWhiteSpace(retryText))
                    {
                        text = retryText;
                        _retryController.RecordSuccess(result.FilePath);
                        _grammarDepth.Value = 0;
                    }
                }
                else
                {
                    RecordSessionError(sessionId);
                    _grammarDepth.Value = 0;
                }
            }
        }

        SaveCheckpointFireAndForget(sessionId, r.Messages, session, ct);

        if (isSimple)
        {
            if (sessionHandle != null)
                await SaveSessionToHandleAsync(session, sessionHandle, ct).ConfigureAwait(false);
            return text;
        }

        var l1State = BuildL1State(message, text, r);
        var edrmEntropy = EstimateResponseEntropy(text);
        var voi = EstimateValueOfInformation(message, text, edrmEntropy);

        var spanRouter = new ResponseSpanRouter();
        l1State.Spans = spanRouter.ParseSpans(text,
            l1State.ToolCalls.Count > 0 ? l1State.ToolCalls.ToArray() : null);
        l1State.SpanUncertaintyRatio = l1State.Spans.Count > 0
            ? (double)l1State.Spans.Count(s => s.UncertaintyScore >= 0.4) / l1State.Spans.Count
            : 0;

        if (IsSessionCircuitOpen(sessionId))
        {
            text = await FullRegenerationAsync(message, "circuit breaker tripped — too many prior errors", l1State, session, ct).ConfigureAwait(false);
            return text;
        }

        var judgeInadequate = false;
        string? judgeReason = null;
        if (text.Length > 50)
        {
            var (adequate, jReason, jScore) = await JudgeResponseQualityAsync(message, text, ct).ConfigureAwait(false);
            if (!adequate)
            {
                FailureRecorder.Record(message, text, jReason ?? "judge deemed inadequate", "L1");
                judgeInadequate = true;
                judgeReason = $"{jReason} (score={jScore}, threshold={_judgeConfidenceThreshold})";
            }
        }

        var (needsPro, reason, _) = _escalationDecider.Evaluate(
            message, text, l1State, edrmEntropy, voi, judgeInadequate, judgeReason);

        if (needsPro && _proAgent != null)
        {
            if (_sameModel)
            {
                text = $"[Same-model: L1 escalation skipped (L1==L2, reason: {reason})]\n\n{text}";
            }
            else
            {
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
        }

        text = await EnforceAndReflectAsync(text, message, session, ct).ConfigureAwait(false);
        if (sessionHandle != null)
            await SaveSessionToHandleAsync(session, sessionHandle, ct).ConfigureAwait(false);
        SaveCheckpointFireAndForget(sessionId, r.Messages, session, ct);
        var pendingSwitch = LocalEmbedderModelSwitchNotifier.ConsumeSwitchMessage();
        return pendingSwitch != null ? $"{pendingSwitch}\n\n{text}" : text;
    }

    public async IAsyncEnumerable<AgentResponseUpdate> ChatStreamingAsync(
        string message, ISessionHandle? sessionHandle = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_budgetTracker != null && !_budgetTracker.TryConsume("streaming", 0).allowed)
        {
            var remaining = _budgetTracker.TryConsume("streaming", 0).remaining;
            yield return new AgentResponseUpdate(new ChatResponseUpdate(ChatRole.Assistant, $"[Budget exhausted. {remaining:N0} tokens remaining.]"));
            yield break;
        }

        var session = sessionHandle != null
            ? await CreateAgentSessionFromHandleAsync(sessionHandle, ct).ConfigureAwait(false)
            : await _agent.CreateSessionAsync(ct).ConfigureAwait(false);
        session = await TryRestoreFromCheckpointAsync(sessionHandle, session, ct).ConfigureAwait(false);

        BackgroundJobService.CurrentSessionId = sessionHandle?.Name;

        var toolResultCount = 0;
        var lastSaveAt = DateTime.UtcNow;
        var lastStreamSaveAt = DateTime.UtcNow;
        var streamIndex = 0;
        var streamToolCalls = new List<(string Name, string Arguments, string Result)>();
        var pendingCalls = new Dictionary<string, (string Name, string Arguments)>();
        var roundMessages = new List<ChatMessage> { new(ChatRole.User, message) };
        var streamTextAccum = new StringBuilder();
        var streamThinkAccum = new StringBuilder();

        while (true)
        {
            AIContent? approvalRequest = null;

            await foreach (var update in _agent.RunStreamingAsync(
                roundMessages, session, cancellationToken: ct).ConfigureAwait(false))
            {
                streamIndex++;
                if (sessionHandle != null &&
                    (toolResultCount % 3 == 0 ||
                     (DateTime.UtcNow - lastSaveAt).TotalSeconds >= 30 ||
                     (DateTime.UtcNow - lastStreamSaveAt).TotalSeconds >= 10))
                {
                    await SaveSessionToHandleAsync(session, sessionHandle, ct).ConfigureAwait(false);
                    SaveCheckpointFireAndForget(sessionHandle.Name, roundMessages, session, ct);
                    lastSaveAt = DateTime.UtcNow;
                    lastStreamSaveAt = DateTime.UtcNow;
                }
                if (update.Contents is { Count: > 0 })
                {
                    foreach (var content in update.Contents)
                    {
                        if (content is Microsoft.Extensions.AI.ToolApprovalRequestContent)
                        {
                            approvalRequest = content;
                            break;
                        }

                        switch (content)
                        {
                            case TextReasoningContent trc:
                                var thinkText = trc.Text;
                                if (!string.IsNullOrEmpty(thinkText))
                                {
                                    streamThinkAccum.Append(thinkText);
                                    yield return new AgentResponseUpdate(ChatRole.Assistant, $"🧠 {thinkText}\n");
                                }
                                break;
                            case FunctionCallContent fc when !string.IsNullOrEmpty(fc.Name):
                                UsageTracker.RecordToolCall();
                                UsageTracker.SetActiveTool(fc.Name);
                                UsageTracker.StartToolTimer();
                                var callId = fc.CallId ?? Guid.NewGuid().ToString();
                                var args = fc.Arguments != null
                                    ? JsonSerializer.Serialize(fc.Arguments)
                                    : "";
                                pendingCalls[callId] = (fc.Name, args);
                                yield return new AgentResponseUpdate(ChatRole.Assistant, $"\n⏳ 正在调用 `{fc.Name}`...\n");
                                break;
                            case FunctionResultContent frc:
                                UsageTracker.StopToolTimer();
                                var preview = frc.Result?.ToString() ?? "(null)";
                                if (preview.Length > 200) preview = preview[..200] + "...";
                                yield return new AgentResponseUpdate(ChatRole.Assistant, $"  ✅ 返回: {preview}\n\n");

                                var fKey = frc.CallId ?? "";
                                if (pendingCalls.TryGetValue(fKey, out var pending))
                                {
                                    if (FileToolNames.Contains(pending.Name))
                                    {
                                        streamToolCalls.Add((pending.Name, pending.Arguments, frc.Result?.ToString() ?? ""));
                                    }
                                    pendingCalls.Remove(fKey);
                                }

                                toolResultCount++;
                                break;
                        }
                    }
                }
                yield return update;
                if (update.Text != null)
                    streamTextAccum.Append(update.Text);
                if (approvalRequest != null) break;
            }

            if (approvalRequest != null && _questionService != null)
            {
                var tarc = (Microsoft.Extensions.AI.ToolApprovalRequestContent)approvalRequest;
                var toolName = tarc.ToolCall is FunctionCallContent fcc
                    ? fcc.Name ?? "未知工具" : "未知工具";
                yield return new AgentResponseUpdate(ChatRole.Assistant, $"\n🔐 代理请求执行 **{toolName}**，需要您的确认...\n");

                var questions = new[]
                {
                    new QuestionPrompt(
                        $"代理请求调用 **{toolName}**，是否允许？",
                        "工具审批",
                        new[]
                        {
                            new QuestionOption("允许", "仅本次允许"),
                            new QuestionOption("始终允许此工具", "后续不再询问此工具"),
                            new QuestionOption("拒绝", "拒绝本次调用"),
                        })
                };

                try
                {
                    var answers = await _questionService.AskAsync(questions, ct).ConfigureAwait(false);
                    if (answers.Count > 0 && answers[0].Count > 0)
                    {
                        var choice = answers[0][0];
                        AIContent responseContent = choice switch
                        {
                            "始终允许此工具" => tarc.CreateAlwaysApproveToolResponse("user approved"),
                            "允许" => tarc.CreateResponse(approved: true, reason: "user approved"),
                            _ => tarc.CreateResponse(approved: false, reason: "user denied"),
                        };
                        roundMessages = [new ChatMessage(ChatRole.User, [responseContent])];
                        continue;
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger?.LogDebug("User cancelled tool approval");
                }
            }

            break;
        }

        var streamCtxRatio = UsageTracker.ContextRatio();
        if (streamToolCalls.Count > 0)
        {
            var ctx = new MessageContext("", ct);
            foreach (var tc in streamToolCalls)
                ctx.ToolCalls.Add(tc);

            ctx = await _pipelineRunner.RunPostGenerationAsync(ctx).ConfigureAwait(false);

            if (ctx.GrammarCheckBlocked)
            {
                var errMsgs = ctx.Messages
                    .Where(m => m.Role == ChatRole.System)
                    .ToList();

                _grammarDepth.Value++;
                var result = ParseGrammarCheckResult(errMsgs);
                var decision = _retryController.Decide(result, _grammarDepth.Value);
                if (decision.Action != RetryAction.Continue)
                {
                    _grammarDepth.Value = 0;
                }
                else if (_grammarDepth.Value <= _grammarRetryMaxDepth && errMsgs.Count > 0)
                {
                    yield return new AgentResponseUpdate(ChatRole.Assistant, "\n\n🔍 检测到语法错误，正在自动修复...\n");
                    var retryR = await _agent.RunAsync(errMsgs, session, cancellationToken: ct).ConfigureAwait(false);
                    var retryText = ApplyBlockedOutput(retryR.Messages?.LastOrDefault()?.Text ?? "");
                    if (!string.IsNullOrWhiteSpace(retryText))
                    {
                        yield return new AgentResponseUpdate(ChatRole.Assistant, retryText);
                        _retryController.RecordSuccess(result.FilePath);
                    }
                }
                else
                {
                    _grammarDepth.Value = 0;
                }
            }
        }

        var blockedReason = SafetyCoordinator.ConsumeBlock();
        if (blockedReason != null)
        {
            yield return new AgentResponseUpdate(ChatRole.Assistant,
                $"\n\n[Content blocked by safety filter. Reason: {blockedReason}]");
        }

        if (streamTextAccum.Length > 50 && _steerJudge != null)
        {
            var (adequate, jReason, jScore) = await JudgeResponseQualityAsync(message, streamTextAccum.ToString(), ct)
                .ConfigureAwait(false);
            if (!adequate && _proAgent != null)
            {
                FailureRecorder.Record(message, streamTextAccum.ToString(), jReason ?? "streaming judge deemed inadequate", "L1");
                yield return new AgentResponseUpdate(ChatRole.Assistant,
                    $"\n\n⟳ 正在升级到 Pro 模型...\n");

                var streamL1State = new L1State
                {
                    Label = "escalate",
                    EscalationReason = jReason,
                    SupportCount = CountSupportingEvidence(streamTextAccum.ToString()),
                    Gap = EstimateCoverageGap(message, streamTextAccum.ToString()),
                    ToolCalls = streamToolCalls.Select(tc => tc.Name).ToList()
                };
                var proResult = await FullRegenerationAsync(message, jReason ?? "judge deemed inadequate", streamL1State, session, ct)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(proResult))
                    yield return new AgentResponseUpdate(ChatRole.Assistant, proResult);
            }
        }

        UsageTracker.SetActiveTool("");
        RefreshModeObserver(session);

        if (sessionHandle != null)
            await SaveSessionToHandleAsync(session, sessionHandle, ct).ConfigureAwait(false);

        SaveCheckpointFireAndForget(sessionHandle?.Name ?? "streaming", roundMessages, session, ct);
    }

    public async Task<string> CycleModeAsync(CancellationToken ct = default)
    {
        var session = await _agent.CreateSessionAsync(ct).ConfigureAwait(false);
        var jso = Microsoft.Agents.AI.AgentAbstractionsJsonUtilities.DefaultOptions;

        var current = "chat";
        try
        {
            var state = session.StateBag.GetValue<Tooling.ObservableAgentModeState>("AgentModeProvider", jso);
            if (state?.CurrentMode != null)
                current = state.CurrentMode;
        }
        catch
        {
            _logger?.LogWarning("Swallowing exception in ChatAgent.cs");
        }

        var idx = Array.IndexOf(ModeCycle, current.ToLowerInvariant());
        var next = idx >= 0 ? ModeCycle[(idx + 1) % ModeCycle.Length] : ModeCycle[0];

        session.StateBag.SetValue("AgentModeProvider", new Tooling.ObservableAgentModeState { CurrentMode = next }, jso);

        try { await _agent.SerializeSessionAsync(session, cancellationToken: ct).ConfigureAwait(false); }
        catch
        {
            _logger?.LogWarning("Swallowing exception in ChatAgent.cs");
        }

        RefreshModeObserver(session);

        return next;
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
}
