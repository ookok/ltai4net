using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LTAI.AI;
using LTAI.Agent.Caching;
using LTAI.Agent.Formats;
using LTAI.Agent.FusionRoute;
using LTAI.Agent.Learning;
using LTAI.Agent.Pipeline;
using LTAI.Agent.Pipeline.Steps;
using LTAI.Agent.Tools;
using LTAI.Agent.Workflows;
using LTAI.Core.Safety;
using LTAI.Core.Session;
using LTAI.Core.Configuration;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

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
    private readonly LTAI.Agent.Tools.QuestionService? _questionService;
    private readonly int _judgeConfidenceThreshold;
    private readonly SmartRetryController _retryController = new();
    private readonly IMemoryCachingStore? _checkpointStore;
    private readonly ConcurrentDictionary<string, int> _sessionCheckpointCounters = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionCheckpointLocks = new(StringComparer.Ordinal);

    // Escalation thresholds from config — defaults match prior hardcoded values
    private readonly int _complexityProFastTrack;
    private readonly int _grammarRetryMaxDepth;
    private readonly int _correctionLoopMaxDepth;
    private static int _sessionMaxErrors = 5;
    private static TimeSpan _sessionCircuitDuration = TimeSpan.FromMinutes(5);

    private sealed class PerSessionErrorState
    {
        public int ErrorCount;
        public DateTime? CircuitOpenUntil;
    }

    private static readonly ConcurrentDictionary<string, PerSessionErrorState> _sessionErrorStates = new(StringComparer.Ordinal);
    private static readonly ILogger _logger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("ChatAgent");

    private static void RecordSessionError(string sessionId)
    {
        var state = _sessionErrorStates.GetOrAdd(sessionId, _ => new());
        Interlocked.Increment(ref state.ErrorCount);
    }

    private static bool IsSessionCircuitOpen(string sessionId)
    {
        if (!_sessionErrorStates.TryGetValue(sessionId, out var state)) return false;
        if (state.CircuitOpenUntil.HasValue && DateTime.UtcNow < state.CircuitOpenUntil.Value)
            return true;
        if (state.ErrorCount >= _sessionMaxErrors)
        {
            state.CircuitOpenUntil = DateTime.UtcNow + _sessionCircuitDuration;
            return true;
        }
        return false;
    }

    private static void ResetSessionErrors(string sessionId)
    {
        if (_sessionErrorStates.TryGetValue(sessionId, out var state))
        {
            Interlocked.Exchange(ref state.ErrorCount, 0);
            state.CircuitOpenUntil = null;
        }
        // Periodic cleanup: remove sessions with zero errors that are past circuit duration
        var now = DateTime.UtcNow;
        foreach (var kv in _sessionErrorStates)
        {
            if (kv.Value.ErrorCount == 0 && kv.Value.CircuitOpenUntil == null)
                _sessionErrorStates.TryRemove(kv.Key, out _);
        }
    }

    public ChatAgent(AIAgent agent, AIAgent? proAgent = null, AgentWorkflows? workflows = null,
        BudgetTracker? budgetTracker = null,
        LocalEmbedder? localEmbedder = null, IHttpClientFactory? httpFactory = null,
        bool sameModel = false, IChatClient? steerJudge = null,
        IEscalationDecider? escalationDecider = null,
        LTAI.Agent.Tools.QuestionService? questionService = null,
        int judgeConfidenceThreshold = 3,
        TreeSitterParser? tsParser = null,
        LTAI.Agent.LanguageServer.LspLanguageManager? lspManager = null,
        IMemoryCachingStore? checkpointStore = null,
        EscalationConfig? escalationConfig = null)
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

        _complexityProFastTrack = cfg.ComplexityProFastTrack;
        _grammarRetryMaxDepth = cfg.GrammarRetryMaxDepth;
        _correctionLoopMaxDepth = cfg.CorrectionLoopMaxDepth;
        _sessionMaxErrors = cfg.SessionMaxErrors;
        _sessionCircuitDuration = TimeSpan.FromMinutes(cfg.SessionCircuitDurationMinutes);
    }

    private static readonly AsyncLocal<string> _traceId = new();
    private static string GetOrCreateTraceId() => _traceId.Value ??= Guid.NewGuid().ToString("N")[..12];

    private readonly IHttpClientFactory? _httpFactory;
    private readonly bool _sameModel;
    private readonly TreeSitterParser? _tsParser;
    private readonly LanguageServer.LspLanguageManager? _lspManager;

    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        // Warm local embedder
        if (_localEmbedder?.Available == false)
            _ = _localEmbedder.Dim;
        // Warm workflows
        // Warmup handled by WarmupService (IHostedService)
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

        // Periodic cleanup of stale in-memory session state (TaskTools, PlanTools)
        Tools.TaskTools.EvictStaleSessions();
        Tools.PlanTools.EvictStaleSessions();

        if (_budgetTracker != null)
        {
            var estimatedTokens = Math.Max(10, TokenEstimator.Estimate(message));
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

        // Try restore from checkpoint: if checkpoint has more recent full-state snapshot,
        // deserialize it into the agent session to recover lost intermediate turns.
        session = await TryRestoreFromCheckpointAsync(sessionHandle, session, ct).ConfigureAwait(false);
        var trimmed = message.Trim();
        var isSimple = _escalationDecider.IsSimpleQuery(message);
        var complexity = _escalationDecider.EstimateComplexity(message);
        var messages = new[] { new ChatMessage(ChatRole.User, message) };

        BackgroundJobService.CurrentSessionId = sessionHandle?.Name ?? traceId;

        // Pro 快速通道：复杂度 >= 4 直接走 Pro，不经过 L1
        if (!isSimple && complexity >= _complexityProFastTrack && _proAgent != null)
        {
            // Skip L1 entirely but still apply quality gate on Pro output
            if (sessionHandle != null)
                await SaveSessionToHandleAsync(session, sessionHandle, ct).ConfigureAwait(false);
            var proSession = sessionHandle != null
                ? await CreateAgentSessionFromHandleAsync(sessionHandle, ct).ConfigureAwait(false)
                : await _proAgent.CreateSessionAsync(ct).ConfigureAwait(false);
            var proR = await _proAgent.RunAsync(messages, proSession, cancellationToken: ct).ConfigureAwait(false);
            var proText = ApplyBlockedOutput(proR.Messages?.LastOrDefault()?.Text ?? "");

            // Quality gate for Pro output: grammar check
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

        // Intermediate save: checkpoint halfway through for crash recovery
        if (sessionHandle != null)
            await SaveSessionToHandleAsync(session, sessionHandle, ct).ConfigureAwait(false);

        // ── 上下文监控与压缩 ──
        // MAF-level CompactionProvider (position [5]) + MaxMessageCountReducer (200 msg cap)
        // handle conversation compaction. This is an observability signal.
        var ctxRatio = LTAI.Core.Configuration.UsageTracker.ContextRatio();
        var sessionId = sessionHandle?.Name ?? traceId;

        // ── 生成后语法检查 ──
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
                    }
                }
                else
                {
                    RecordSessionError(sessionId);
                    _grammarDepth.Value = 0;
                }
            }
        }

        // Save conversation state checkpoint
        SaveCheckpointFireAndForget(sessionId, r.Messages, session, ct);

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

        // ── Session circuit breaker ──
        if (IsSessionCircuitOpen(sessionId))
        {
            await FullRegenerationAsync(message, "circuit breaker tripped — too many prior errors", l1State, session, ct);
            return text; // return immediately after full regeneration
        }

        // ── LLM-as-Judge ──
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

        // ── Escalation decision (via IEscalationDecider) ──
        var (needsPro, reason, _) = _escalationDecider.Evaluate(
            message, text, l1State, edrmEntropy, voi, judgeInadequate, judgeReason);

        if (needsPro && _proAgent != null)
        {
            // When L1 and L2 are the same model, escalation just re-invokes same model
            if (_sameModel)
            {
                text = $"[Same-model: L1 escalation skipped (L1==L2, reason: {reason})]\n\n{text}";
            }
            else
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
        // Budget check — consistent with non-streaming path
        if (_budgetTracker != null && !_budgetTracker.TryConsume("streaming", 0).allowed)
        {
            var remaining = _budgetTracker.TryConsume("streaming", 0).remaining;
            yield return new AgentResponseUpdate(new ChatResponseUpdate(ChatRole.Assistant, $"[Budget exhausted. {remaining:N0} tokens remaining.]"));
            yield break;
        }

        var session = sessionHandle != null
            ? await CreateAgentSessionFromHandleAsync(sessionHandle, ct).ConfigureAwait(false)
            : await _agent.CreateSessionAsync(ct).ConfigureAwait(false);

        // Try restore from checkpoint for streaming path too
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
            Microsoft.Extensions.AI.AIContent? approvalRequest = null;

            await foreach (var update in _agent.RunStreamingAsync(
                roundMessages, session, cancellationToken: ct).ConfigureAwait(false))
            {
                // Periodic auto-save during streaming: every 10 seconds or every 3rd update
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
                        // Detect MAF ToolApprovalRequestContent (not in switch since the type
                        // comes from Microsoft.Extensions.AI.Abstractions as a base AIContent)
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
                                LTAI.Core.Configuration.UsageTracker.RecordToolCall();
                                LTAI.Core.Configuration.UsageTracker.SetActiveTool(fc.Name);
                                LTAI.Core.Configuration.UsageTracker.StartToolTimer();
                                var callId = fc.CallId ?? Guid.NewGuid().ToString();
                                var args = fc.Arguments != null
                                    ? JsonSerializer.Serialize(fc.Arguments)
                                    : "";
                                pendingCalls[callId] = (fc.Name, args);
                                yield return new AgentResponseUpdate(ChatRole.Assistant, $"\n⏳ 正在调用 `{fc.Name}`...\n");
                                break;
                            case FunctionResultContent frc:
                                LTAI.Core.Configuration.UsageTracker.StopToolTimer();
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
                var toolName = tarc.ToolCall is Microsoft.Extensions.AI.FunctionCallContent fcc
                    ? fcc.Name ?? "未知工具" : "未知工具";
                yield return new AgentResponseUpdate(ChatRole.Assistant, $"\n🔐 代理请求执行 **{toolName}**，需要您的确认...\n");

                var questions = new[]
                {
                    new LTAI.Agent.Tools.QuestionPrompt(
                        $"代理请求调用 **{toolName}**，是否允许？",
                        "工具审批",
                        new[]
                        {
                            new LTAI.Agent.Tools.QuestionOption("允许", "仅本次允许"),
                            new LTAI.Agent.Tools.QuestionOption("始终允许此工具", "后续不再询问此工具"),
                            new LTAI.Agent.Tools.QuestionOption("拒绝", "拒绝本次调用"),
                        })
                };

                try
                {
                    var answers = await _questionService.AskAsync(questions, ct).ConfigureAwait(false);
                    if (answers.Count > 0 && answers[0].Count > 0)
                    {
                        var choice = answers[0][0];
                        Microsoft.Extensions.AI.AIContent responseContent = choice switch
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
                    // User cancelled — stop
                }
            }

            break;
        }

        // ── 上下文监控（streaming） ──
        var streamCtxRatio = LTAI.Core.Configuration.UsageTracker.ContextRatio();
        if (streamCtxRatio > 0.75)
        {
            // MAF-level CompactionProvider + MaxMessageCountReducer handle actual compaction.
        }

        // ── 生成后语法检查（streaming） ──
        if (streamToolCalls.Count > 0)
        {
            var ctx = new MessageContext("", ct);
            foreach (var tc in streamToolCalls)
                ctx.ToolCalls.Add(tc);

            var step = new GrammarCheckStep(tsParser: _tsParser, lspManager: _lspManager);
            ctx = await step.ProcessAsync(ctx).ConfigureAwait(false);

            if (ctx.TryGet<bool>("GrammarCheckBlocked", out var gramBlocked) && gramBlocked)
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

        // ── L3 Quality Judge (streaming mode) ──
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

        LTAI.Core.Configuration.UsageTracker.SetActiveTool("");

        // Update frontend observer with current mode/todo state
        RefreshModeObserver(session);

        if (sessionHandle != null)
            await SaveSessionToHandleAsync(session, sessionHandle, ct).ConfigureAwait(false);

        SaveCheckpointFireAndForget(sessionHandle?.Name ?? "streaming", roundMessages, session, ct);
    }

    private static void RefreshModeObserver(AgentSession session)
    {
        // Read mode/todo state from MAF providers' StateBag entries using
        // local DTOs with matching JsonPropertyName attributes.
        var jso = Microsoft.Agents.AI.AgentAbstractionsJsonUtilities.DefaultOptions;
        try
        {
            var modeState = session.StateBag.GetValue<Tooling.ObservableAgentModeState>("AgentModeProvider", jso);
            if (modeState != null)
                Tooling.AgentModeObserver.CurrentMode = modeState.CurrentMode ?? "chat";
        }
        catch
        {
            // Degrade gracefully — MAF provider may not have run yet
        }

        try
        {
            var todoState = session.StateBag.GetValue<Tooling.ObservableTodoState>("TodoProvider", jso);
            if (todoState?.Items is { Count: > 0 })
            {
                Tooling.AgentModeObserver.TotalTodos = todoState.Items.Count;
                Tooling.AgentModeObserver.RemainingTodos = todoState.Items.Count(t => !t.IsComplete);
                var sb = new System.Text.StringBuilder();
                foreach (var t in todoState.Items)
                {
                    var icon = t.IsComplete ? "✅" : "⬜";
                    sb.AppendLine($"{icon} {t.Title}" + (t.Description != null ? $": {t.Description}" : ""));
                }
                Tooling.AgentModeObserver.TodoSummary = sb.ToString();
            }
            else
            {
                Tooling.AgentModeObserver.TotalTodos = 0;
                Tooling.AgentModeObserver.RemainingTodos = 0;
                Tooling.AgentModeObserver.TodoSummary = null;
            }
        }
        catch
        {
            // Degrade gracefully
        }
    }

    private static readonly string[] ModeCycle = ["chat", "plan", "execute"];

    /// <summary>Cycle the agent mode: chat → plan → execute → chat.</summary>
    public async Task<string> CycleModeAsync(CancellationToken ct = default)
    {
        var session = await _agent.CreateSessionAsync(ct).ConfigureAwait(false);
        var jso = Microsoft.Agents.AI.AgentAbstractionsJsonUtilities.DefaultOptions;

        // Read current mode from state bag
        var current = "chat";
        try
        {
            var state = session.StateBag.GetValue<Tooling.ObservableAgentModeState>("AgentModeProvider", jso);
            if (state?.CurrentMode != null)
                current = state.CurrentMode;
        }
        catch { }

        // Find next mode in cycle
        var idx = Array.IndexOf(ModeCycle, current.ToLowerInvariant());
        var next = idx >= 0 ? ModeCycle[(idx + 1) % ModeCycle.Length] : ModeCycle[0];

        // Write to state bag
        session.StateBag.SetValue("AgentModeProvider", new Tooling.ObservableAgentModeState { CurrentMode = next }, jso);

        // Save session
        try { await _agent.SerializeSessionAsync(session, cancellationToken: ct).ConfigureAwait(false); }
        catch { }

        // Update observer
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

    // ── Quality Judge (LLM-as-Judge) ──

    private async Task<(bool IsAdequate, string? Reason, int Score)> JudgeResponseQualityAsync(
        string message, string response, CancellationToken ct)
    {
        if (_steerJudge == null)
            return (true, "no steer model configured — assuming adequate", 5);
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
                var isConfident = score >= _judgeConfidenceThreshold;
                return (adequate && isConfident, reason ?? (adequate ? null : "judge deemed inadequate"), score);
            }
            return (true, null, 5);
        }
        catch
        {
            return (true, null, 0);
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
        var hedgeWords = new[] {
            // Chinese hedge words
            "不确定", "可能", "也许", "大概", "估计", "似乎", "推测",
            "疑似", "貌似", "好像", "或许是", "按理说", "看样子", "猜测",
            "通常情况下", "一般来说", "理论上", "某种程度上",
            // English hedge words
            "maybe", "perhaps", "probably", "possibly", "might", "could be",
            "sometimes", "usually", "generally", "typically", "often",
            "likely", "unlikely", "presumably", "arguably", "apparently",
            "seems", "appears", "suggests", "indicates",
            // Japanese hedge words
            "かもしれない", "でしょう", "たぶん", "おそらく",
            // Korean hedge words
            "아마도", "아마",
        };
        var count = hedgeWords.Count(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
        return Math.Min(1.0, count * 0.15);
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
    private static readonly AsyncLocal<int> _grammarDepth = new();

    private async Task<string> EnforceAndReflectAsync(string text, string originalMessage,
        AgentSession session, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        _correctionDepth.Value++;
        if (_correctionDepth.Value > _correctionLoopMaxDepth) { _correctionDepth.Value = 0; return text; }

        if (_proAgent == null) return text;

        // Extended trigger: refusal patterns OR placeholder patterns OR high hedge-word density
        var hasRefusal = _escalationDecider.ContainsRefusalPatterns(text);
        var hasPlaceholder = text.Contains("{{") || text.Contains("TODO");
        var hasHedgeWords = ContainsHedgeWords(text);

        if (!hasRefusal && text.Length >= 15 && !hasPlaceholder && !hasHedgeWords)
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

    /// <summary>Detect high hedge-word density indicating uncertain response.</summary>
    private static bool ContainsHedgeWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 30) return false;
        var hedgeCount = 0;
        var lower = text.ToLowerInvariant();
        var hedgeWords = new[] {
            "不确定", "可能", "也许", "大概", "估计", "似乎", "推测",
            "maybe", "perhaps", "probably", "possibly", "might", "could be",
            "seems", "appears", "suggests", "indicates",
            "かもしれません", "でしょう", "たぶん",
            "아마도", "아마",
        };
        foreach (var word in hedgeWords)
        {
            var idx = 0;
            while ((idx = lower.IndexOf(word, idx, StringComparison.Ordinal)) >= 0)
            {
                hedgeCount++;
                idx += word.Length;
            }
        }
        // Trigger if more than 2 hedge words or hedge density > 5%
        var wordCount = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return hedgeCount >= 3 || (wordCount > 0 && (double)hedgeCount / wordCount > 0.05);
    }

    // ── Checkpoint helpers ──

    /// <summary>Attempt to restore session state from nearest full-state checkpoint.
    /// Returns the restored session if successful, or the original session if not.
    /// Also syncs restored state back to ISessionHandle for durable persistence.</summary>
    private async Task<AgentSession> TryRestoreFromCheckpointAsync(ISessionHandle? sessionHandle, AgentSession session, CancellationToken ct)
    {
        if (_checkpointStore == null || sessionHandle == null) return session;
        var sessionId = sessionHandle.Name;
        try
        {
            var cp = await _checkpointStore.FindNearestAsync(sessionId, long.MaxValue, ct).ConfigureAwait(false);
            if (cp?.data == null) return session;

            var cpData = JsonSerializer.Deserialize<CheckpointData>(Encoding.UTF8.GetString(cp.Value.data));
            if (cpData?.SessionData == null) return session;

            var currentMsgs = sessionHandle.Messages.Count;
            if (cpData.MsgCount <= currentMsgs) return session;

            // Validate SessionData is non-empty and parseable before attempting restore
            if (string.IsNullOrEmpty(cpData.SessionData) || cpData.SessionData.Length < 20) return session;
            JsonElement restoredElement;
            try { restoredElement = JsonDocument.Parse(cpData.SessionData).RootElement.Clone(); }
            catch { return session; }

            _logger.LogInformation("Restoring session {SessionId} from checkpoint at msgCount={MsgCount} (current={Current})",
                sessionId, cpData.MsgCount, currentMsgs);

            var restored = await _agent.DeserializeSessionAsync(restoredElement, cancellationToken: ct).ConfigureAwait(false);

            // Sync restored state back to ISessionHandle so subsequent saves don't overwrite recovery
            var restoredJson = await _agent.SerializeSessionAsync(restored, cancellationToken: ct).ConfigureAwait(false);
            sessionHandle.UpdateFromJson(restoredJson.GetRawText());

            return restored;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Checkpoint restore failed for session {SessionId}", sessionId);
            return session;
        }
    }

    private sealed record CheckpointData
    {
        public string Session { get; init; } = "";
        public long Tokens { get; init; }
        public int MsgCount { get; init; }
        public string? SessionData { get; init; }
    }

    private async Task SaveCheckpointAsync(string sessionId, IList<ChatMessage>? messages, AgentSession? session, CancellationToken ct)
    {
        if (_checkpointStore == null || messages == null || messages.Count == 0) return;

        var lockObj = _sessionCheckpointLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await lockObj.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long tokenCount = 0;
            foreach (var msg in messages)
            {
                if (!string.IsNullOrEmpty(msg.Text))
                    tokenCount += TokenEstimator.Estimate(msg.Text);
            }
            var key = $"session:{sessionId}:pos:{tokenCount}";

            var sessionCounter = _sessionCheckpointCounters.AddOrUpdate(sessionId, 1, (_, v) => v + 1);
            string? sessionData = null;
            if (session != null && sessionCounter % 10 == 0)
            {
                try
                {
                    var sessionJson = await _agent.SerializeSessionAsync(session, cancellationToken: ct).ConfigureAwait(false);
                    sessionData = sessionJson.GetRawText();
                }
                catch { /* serialize best-effort */ }
            }

            var data = JsonSerializer.Serialize(new CheckpointData
            {
                Session = sessionId,
                Tokens = tokenCount,
                MsgCount = messages.Count,
                SessionData = sessionData
            });
            await _checkpointStore.StoreAsync(key, Encoding.UTF8.GetBytes(data), tokenCount, ct).ConfigureAwait(false);

            // Periodic compaction: every 200 checkpoints for a session, reset the store.
            // The current checkpoint (just saved above) includes full session data, so
            // we can safely invalidate all older checkpoints and start fresh.
            if (sessionCounter == 200)
            {
                try
                {
                    _sessionCheckpointCounters.TryRemove(sessionId, out _);
                    await _checkpointStore.InvalidateSessionAsync(sessionId, ct).ConfigureAwait(false);
                    // Re-save the current checkpoint with counter reset to 1
                    _sessionCheckpointCounters.AddOrUpdate(sessionId, 1, (_, _) => 1);
                    await _checkpointStore.StoreAsync(key, Encoding.UTF8.GetBytes(data), tokenCount, ct).ConfigureAwait(false);
                }
                catch { /* compaction best-effort */ }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SaveCheckpointAsync failed for session {SessionId}", sessionId);
        }
        finally
        {
            lockObj.Release();
        }
    }

    private void SaveCheckpointFireAndForget(string sessionId, IList<ChatMessage>? messages, AgentSession? session, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try { await SaveCheckpointAsync(sessionId, messages, session, ct).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "Checkpoint fire-and-forget failed for session {SessionId}", sessionId); }
        });
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

    // ── 生成后语法检查 ──

    private static readonly HashSet<string> FileToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "write", "edit", "writefile", "editfile", "create", "createfile",
        "writetool", "filewritetool", "editfiletool"
    };

    /// <summary>从消息列表中提取写文件类工具调用。</summary>
    private static List<(string Name, string Arguments, string Result)> ExtractFileToolCalls(IList<ChatMessage> messages)
    {
        var calls = new List<(string Name, string Arguments, string Result)>();
        var callMap = new Dictionary<string, (string Name, string Arguments)>();

        foreach (var msg in messages)
        {
            if (msg.Contents == null) continue;

            foreach (var content in msg.Contents)
            {
                if (content is FunctionCallContent fcc && fcc.Name != null)
                {
                    var callId = fcc.CallId ?? Guid.NewGuid().ToString();
                    var args = fcc.Arguments != null
                        ? JsonSerializer.Serialize(fcc.Arguments)
                        : "";
                    callMap[callId] = (fcc.Name, args);
                }
                else if (content is FunctionResultContent frc)
                {
                    var key = frc.CallId ?? "";
                    if (callMap.TryGetValue(key, out var callInfo) && FileToolNames.Contains(callInfo.Name))
                    {
                        calls.Add((callInfo.Name, callInfo.Arguments, frc.Result?.ToString() ?? ""));
                        callMap.Remove(key);
                    }
                }
            }
        }

        return calls;
    }

    /// <summary>运行生成后语法检查。返回是否有语法错误及应注入的系统消息。</summary>
    private async Task<(bool HasErrors, List<ChatMessage> ErrorMessages)> PostGenerationGrammarCheckAsync(
        IList<ChatMessage> messages, CancellationToken ct)
    {
        var toolCalls = ExtractFileToolCalls(messages);
        if (toolCalls.Count == 0)
            return (false, []);

        var ctx = new MessageContext("", ct);
        foreach (var (name, args, result) in toolCalls)
            ctx.ToolCalls.Add((name, args, result));

        var step = new GrammarCheckStep(tsParser: _tsParser, lspManager: _lspManager);
        ctx = await step.ProcessAsync(ctx).ConfigureAwait(false);

        if (ctx.TryGet<bool>("GrammarCheckBlocked", out var blocked) && blocked)
        {
            var errorMessages = ctx.Messages
                .Where(m => m.Role == ChatRole.System)
                .ToList();
            return (true, errorMessages);
        }

        return (false, []);
    }

    private static GrammarCheckResult ParseGrammarCheckResult(List<ChatMessage> errorMessages)
    {
        var errorCount = errorMessages.Count;
        var firstMsg = errorMessages.FirstOrDefault()?.Text ?? "";
        var parts = firstMsg.Split(':', 3);
        var filePath = parts.Length > 0 ? parts[0].Trim() : "";
        var errorType = parts.Length > 2 ? parts[2].Trim() : "syntax";
        if (errorType.Length > 40) errorType = errorType[..40];
        return new GrammarCheckResult(errorType, filePath, errorCount, 0, 0);
    }
}
