using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LTAI.Core.Configuration;
using LTAI.Core.Execution;
using LTAI.Core.Interfaces;
using LTAI.Core.Messaging;
using LTAI.Core.Models;
using LTAI.DNA;
using LTAI.Tools.Reasoning;
using LTAI.AI.Providers;
using LTAI.AI.Utilities;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.AI.Governors;

public sealed class LivingTreeSystem : IAsyncDisposable
{
    private readonly ICognitiveMesh _mesh;
    private readonly TaskJournal _journal;
    private readonly IChatClient _llm;
    private readonly ProviderFanOutRace? _fanOut;
    private readonly AIToolRegistry _toolRegistry;
    private readonly ILogger<LivingTreeSystem> _logger;
    private readonly DNAOrchestrator? _dna;
    private readonly IOptions<LTAIOptions> _options;

    private readonly InputGovernor _input;
    private readonly ContextGovernor _context;
    private readonly RoutingGovernor _routing;
    private readonly OutputGovernor _output;
    private readonly SelfGovernor _self;
    private readonly SystemGuardian _guardian;
    private readonly ReasoningOrchestrator? _reasoning;
    private readonly L1L2DuplexRouter? _duplexRouter;
    private readonly SynapticMemory? _synapticMemory;
    private readonly DreamCycle? _dreamCycle;
    private readonly MetaCognitiveLayer _metaCognition;
    private readonly QueryPatternRouter _patternRouter;
    private readonly ResponseGroundingVerifier _groundingVerifier;
    private readonly L1PlanExecutor _planExecutor;
    private readonly BackgroundWorkQueue _workQueue;
    private readonly ToolSelector _toolSelector;

    private readonly BAVTRouter _bavtRouter = new(100.0);
    private readonly ERLLoop _erlLoop = new();
    private readonly ElasticMemoryOrchestrator _elasticMemory = new();
    private readonly StructuredReflectionEngine _reflectionEngine = new();
    private readonly CoEchoDetector _echoDetector = new();
    private readonly TaskPipeline _taskPipeline;
    private readonly ICrossRunEvolutionStore? _evolutionStore;
    private readonly IVerifiableRegistry? _verifiableRegistry;
    private int _requestCount;
    private const int TrainingInterval = 50;

    private string DefaultModel => _options.Value.AI.L2.Model;
    private string FlashModel => _options.Value.AI.L1.Model;

    private string GetDegradedModel(string model)
    {
        var chain = _options.Value.ModelPricing?.DegradationChain;
        if (chain != null && chain.TryGetValue(model, out var fallback))
            return fallback;
        return FlashModel;
    }

    public SystemGuardian Guardian => _guardian;
    public SystemMode Mode => _guardian.Mode;
    public bool DNAEnabled => _dna != null;
    public DNAStatus? DNAStatus => _dna?.GetStatus();
    public InputGovernor InputGovernor => _input;
    public ContextGovernor ContextGovernor => _context;
    public RoutingGovernor RoutingGovernor => _routing;
    public IChatClient LLMClient => _llm;
    public TaskPipeline TaskPipeline => _taskPipeline;

    public LivingTreeSystem(
        ICognitiveMesh mesh,
        TaskJournal journal,
        IChatClient llm,
        IOptions<LTAIOptions> options,
        InputGovernor input,
        ContextGovernor context,
        RoutingGovernor routing,
        OutputGovernor output,
        SelfGovernor self,
        SystemGuardian guardian,
        AIToolRegistry toolRegistry,
        ILogger<LivingTreeSystem> logger,
        ProviderFanOutRace? fanOut = null,
        DNAOrchestrator? dna = null,
        ReasoningOrchestrator? reasoning = null,
        L1L2DuplexRouter? duplexRouter = null,
        SynapticMemory? synapticMemory = null,
        DreamCycle? dreamCycle = null,
        MetaCognitiveLayer? metaCognition = null,
        QueryPatternRouter? patternRouter = null,
        ResponseGroundingVerifier? groundingVerifier = null,
        L1PlanExecutor? planExecutor = null,
        BackgroundWorkQueue? workQueue = null,
        ToolSelector? toolSelector = null,
        ICrossRunEvolutionStore? evolutionStore = null,
        IVerifiableRegistry? verifiableRegistry = null)
    {
        _mesh = mesh;
        _journal = journal;
        _llm = llm;
        _fanOut = fanOut;
        _toolRegistry = toolRegistry;
        _logger = logger;
        _options = options;
        _input = input;
        _context = context;
        _routing = routing;
        _output = output;
        _self = self;
        _guardian = guardian;
        _dna = dna;
        _reasoning = reasoning;
        _duplexRouter = duplexRouter;
        _synapticMemory = synapticMemory;
        _dreamCycle = dreamCycle;
        _metaCognition = metaCognition ?? new MetaCognitiveLayer();
        _patternRouter = patternRouter ?? new QueryPatternRouter(toolRegistry);
        _groundingVerifier = groundingVerifier ?? new ResponseGroundingVerifier();
        _planExecutor = planExecutor ?? new L1PlanExecutor();
        _workQueue = workQueue ?? new BackgroundWorkQueue();
        _toolSelector = toolSelector ?? new ToolSelector(toolRegistry);
        _evolutionStore = evolutionStore;
        _verifiableRegistry = verifiableRegistry;
        _taskPipeline = new TaskPipeline(_journal);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _mesh.RegisterAsync(_input, cancellationToken);
        await _mesh.RegisterAsync(_context, cancellationToken);
        await _mesh.RegisterAsync(_routing, cancellationToken);
        await _mesh.RegisterAsync(_output, cancellationToken);
        await _mesh.RegisterAsync(_self, cancellationToken);

        _guardian.StartMonitoring(TimeSpan.FromSeconds(15));
        _logger.LogInformation("LivingTreeSystem v6.0 initialized with 5 governors, DNA: {DNA}",
            _dna != null ? "enabled" : "disabled");

        if (_evolutionStore != null)
        {
            var activeLessons = _evolutionStore.GetActiveLessons(10);
            if (activeLessons.Count > 0)
            {
                _logger.LogInformation("Cross-run evolution: loaded {Count} active lessons from prior runs",
                    activeLessons.Count);
            }
        }
    }

    public async Task<string> ChatAsync(string query, CancellationToken cancellationToken = default)
    {
        var entry = _journal.Add(query);

        try
        {
            if (_guardian.Mode == SystemMode.LifeSupport)
            {
                _journal.Complete(entry, "emergency");
                return await _guardian.EmergencyChatAsync(query, cancellationToken);
            }

            if (_journal.IsPaused)
            {
                _journal.Complete(entry, "paused");
                return "Journal is paused. Resume to continue.";
            }

            if (_dna != null)
            {
                var safetyCheck = await _dna.Safety.EvaluateAsync(query, cancellationToken: cancellationToken);
                if (!safetyCheck.Allowed)
                {
                    _journal.Complete(entry, $"blocked: {safetyCheck.BlockReason}");
                    _logger.LogWarning("DNA safety blocked query: {Reason}", safetyCheck.BlockReason);
                    return $"[Safety: {safetyCheck.BlockReason}]";
                }
            }

            var response = await ProcessTypedAsync(GovernorInput.Create(query), cancellationToken);
            _journal.Complete(entry, response.Response[..Math.Min(response.Response.Length, 500)]);

            var reply = response.Response;
            _workQueue.Enqueue(async ct => { try { await SilentSelfCheckAsync(reply); } catch { } }, "SilentSelfCheck");

            if (_dna != null && !string.IsNullOrEmpty(reply))
            {
                _workQueue.Enqueue(async ct =>
                {
                    try { await _dna.ProcessAsync(query, reply, ct); }
                    catch { }
                }, "DNA process");
            }

            return reply;
        }
        catch (Exception ex)
        {
            _journal.Fail(entry, ex.Message);
            _guardian.RecordError();
            _logger.LogError(ex, "Chat failed");
            throw;
        }
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        string query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_guardian.Mode == SystemMode.LifeSupport)
        {
            yield return await _guardian.EmergencyChatAsync(query, cancellationToken);
            yield break;
        }

        if (_dna != null)
        {
            var safetyCheck = await _dna.Safety.EvaluateAsync(query, cancellationToken: cancellationToken);
            if (!safetyCheck.Allowed)
            {
                yield return $"[Safety blocked: {safetyCheck.BlockReason}]";
                yield break;
            }
        }

        // Layer 1: Pattern-based tool execution (deterministic, no model call needed)
        var patternResult = await _patternRouter.MatchAndExecuteAsync(query, cancellationToken);
        string? layer1Context = null;
        bool layer1HighConfidence = false;

        if (patternResult.Matched)
        {
            layer1Context = $"【Layer1 自动执行工具: {patternResult.ToolName}】\n{patternResult.ContextMessage}";
            layer1HighConfidence = patternResult.Confidence >= 0.95f;
            _logger.LogInformation("Layer1 matched: tool={Tool} confidence={Conf:F2}",
                patternResult.ToolName, patternResult.Confidence);

            // For high-confidence matches, yield tool result directly — no model call needed
            if (layer1HighConfidence && patternResult.ContextMessage != null)
            {
                var summary = patternResult.ContextMessage;
                yield return summary;
                _metaCognition.RecordOutcome(query, true);
                if (patternResult.ToolName != null)
                    _metaCognition.ReinforceDomain(patternResult.ToolName, 0.05f);
                yield break;
            }
        }

        // L0: intent classification + knowledge graph shortcut
        var toolCount = _toolRegistry.ListTools().Count();

        // L0 classify: fast vs deep — must run before any routing/search
        var label = "general";
        string model;
        string? extractedEntity = null;
        try
        {
            var inputResult = await _input.ProcessAsync(new Handshake
            {
                To = "input", Action = "process",
                Payload = new Dictionary<string, object?> { ["query"] = query }
            }, cancellationToken);
            label = inputResult.Payload?.GetValueOrDefault("label")?.ToString() ?? "deep";
            extractedEntity = (inputResult.Payload?.GetValueOrDefault("entity_root") as string)
                ?? (inputResult.Payload?.GetValueOrDefault("entity") as string);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-search JSON parse failed for: {Query}", query);
        }

        model = label switch { "fast" or "reflex" => FlashModel, "deep" => DefaultModel, _ => DefaultModel };

        // MetaCognitiveLayer: self-awareness check before answering
        // Layer 1 high-confidence match boosts local confidence → skip unnecessary delegation
        var localConfidence = layer1HighConfidence
            ? 0.95f
            : label switch { "fast" => 0.8f, "reflex" => 0.9f, _ => 0.5f };
        var metaAssessment = _metaCognition.Assess(query, localConfidence);
        string? metaContext = null;

        if (metaAssessment.ShouldDelegate)
        {
            if (label is "fast" or "reflex")
            {
                model = DefaultModel;
                metaContext = $"【系统自评】该领域熟悉度低（置信度={metaAssessment.Certainty:F2}，原因: {metaAssessment.DelegationReason}），已升级到 {DefaultModel} 处理。请务必使用工具验证信息，不得推测。";
            }
            else
            {
                metaContext = $"【系统自评】该领域熟悉度低（置信度={metaAssessment.Certainty:F2}，原因: {metaAssessment.DelegationReason}）。请务必使用工具，不得推测。";
            }
            _logger.LogInformation("MetaCognition: {Assessment} | Model={Model}", metaAssessment.Assessment, model);
        }
        _logger.LogDebug("MetaCognition assessment: {Assessment} | Delegating={Deleg} Layer1={L1}",
            metaAssessment.Assessment, metaAssessment.ShouldDelegate, layer1HighConfidence);

        var now = DateTime.Now;
        var dayNames = new[] { "星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六" };
        var dateTag = $"当前日期: {now:yyyy年M月d日} {dayNames[(int)now.DayOfWeek]}";

        // After L0 understanding, auto-search only when a named entity was extracted
        // Skip auto-search if Layer 1 already performed a web_search or provided tool context
        string? autoSearchContext = null;
        if (label != "fast" && label != "reflex" && extractedEntity != null
            && toolCount > 0 && _toolRegistry.HasTool("web_search")
            && !layer1HighConfidence
            && patternResult.ToolName != "web_search")
        {
            try
            {
                var searchResult = await _toolRegistry.InvokeAsync("web_search",
                    new Dictionary<string, object?> { ["query"] = extractedEntity, ["maxResults"] = 5 },
                    cancellationToken);
                if (searchResult?.ToString() is { Length: > 0 } raw)
                {
                    int resultCount = 0;
                    try
                    {
                        using var doc = JsonDocument.Parse(raw);
                        resultCount = doc.RootElement.TryGetProperty("count", out var c) ? c.GetInt32() : 0;
                    }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "L0 intent classification failed, falling back to 'deep'");
        }

                    if (resultCount == 0)
                    {
                        autoSearchContext = $"【自动网络搜索】搜索 \"{query}\" 未找到任何相关结果。你必须如实告知用户未找到相关信息，严禁编造虚构。";
                    }
                    else
                    {
                        autoSearchContext = $"【自动网络搜索结果】（仅基于以下数据回答，不得自行推测或联想）\n{raw[..Math.Min(raw.Length, 4000)]}";
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Auto web_search failed for: {Query}", query);
            }
        }

        // Layer 2: L1 model plans tool usage → execute → inject results
        // Runs when Layer 1 didn't match and MetaCog says we're in unfamiliar territory
        string? layer2Context = null;
        if (!patternResult.Matched
            && autoSearchContext == null
            && layer1Context == null
            && metaAssessment.ShouldDelegate
            && label != "fast" && label != "reflex"
            && toolCount > 0)
        {
            try
            {
                var planResult = await _planExecutor.PlanAndExecuteAsync(
                    query, _llm, _toolRegistry, FlashModel, cancellationToken);
                if (planResult.Success && planResult.ContextMessage != null)
                {
                    layer2Context = planResult.ContextMessage;
                    _logger.LogInformation("Layer2 plan executed: {Count} tools", planResult.ToolsExecuted);
                }
                else
                {
                    _logger.LogDebug("Layer2 plan failed: {Error}", planResult.Error ?? "no tools executed");
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Layer2 planning exception");
            }
        }

        if (_duplexRouter != null)
        {
            var routeResult = _duplexRouter.Route(query);
            if (routeResult.CanAnswerLocally)
            {
                // Layer 4: verify the cached answer
                if (!layer1HighConfidence)
                {
                    var toolCtx = layer1Context ?? autoSearchContext;
                    var g = _groundingVerifier.Verify(
                        routeResult.LocalResponse, query, toolCtx,
                        false, 0, layer1Context != null);
                    if (!g.IsGrounded)
                    {
                        _logger.LogWarning("DuplexRouter local answer grounding failed: {Issue}, falling through",
                            g.Issue);
                    }
                    else
                    {
                        _metaCognition.RecordOutcome(query, true);
                        yield return routeResult.LocalResponse;
                        yield break;
                    }
                }
                else
                {
                    yield return routeResult.LocalResponse;
                    yield break;
                }
            }

            // When Layer 1 already has tool data, skip duplex router L2 teaching
            // because the teaching model doesn't see Layer 1's injected context
            if (routeResult.Route == "delegate_l2" && layer1Context == null && layer2Context == null)
            {
                var ctxResult = await _context.ProcessAsync(new Handshake
                {
                    To = "context", Action = "preload",
                    Payload = new Dictionary<string, object?> { ["query"] = query },
                    ReplyTo = Guid.NewGuid().ToString("N")
                }, cancellationToken);
                var ctx = ctxResult.Payload?.GetValueOrDefault("context")?.ToString() ?? "";

                var fullContext = string.IsNullOrEmpty(ctx) ? (autoSearchContext ?? "") : $"{(autoSearchContext != null ? autoSearchContext + "\n\n" : "")}Context:\n{ctx}";
                var toolHint = toolCount > 0
                    ? $"\n[System: You have {toolCount} tools available including web_search, shell_exec, http_get, filesystem_read, and more. Use tools when you need real-time information or external capabilities.]"
                    : "";
                var fullQuery = string.IsNullOrEmpty(fullContext)
                    ? $"{dateTag}\n{query}{toolHint}"
                    : $"Context:\n{fullContext}\n\n{dateTag}\nQuery: {query}{toolHint}";
                var teachingResult = await _duplexRouter.RequestL2ReasoningAsync(fullQuery, routeResult, cancellationToken);
                if (teachingResult != null)
                {
                    // Layer 4: Verify cached/taught answer before returning
                    if (!layer1HighConfidence)
                    {
                        var toolCtx = layer1Context ?? autoSearchContext;
                        var v = _groundingVerifier.Verify(
                            teachingResult.Answer, query, toolCtx,
                            false, 0, layer1Context != null);
                        if (!v.IsGrounded)
                        {
                            _logger.LogWarning("DuplexRouter grounding failed: {Issue}, falling through to ReAct",
                                v.Issue);
                            // Fall through to main ReAct loop for proper grounding
                        }
                        else
                        {
                            _duplexRouter.LearnFromL2(query, teachingResult);
                            _context.AddTurn(query, teachingResult.Answer);
                            _metaCognition.RecordOutcome(query, true);
                            yield return teachingResult.Answer;
                            yield break;
                        }
                    }
                    else
                    {
                        _duplexRouter.LearnFromL2(query, teachingResult);
                        _context.AddTurn(query, teachingResult.Answer);
                        _metaCognition.RecordOutcome(query, true);
                        yield return teachingResult.Answer;
                        yield break;
                    }
                }
            }
        }

        var selectedTools = _toolSelector.SelectTools(query, _toolRegistry.GetTools());

        // Cross-run memory: inject relevant past experiences as context
        string? memoryContext = null;
        if (_synapticMemory != null && layer1Context == null)
        {
            var similar = _synapticMemory.FindSimilar(query, maxResults: 2, minReward: 0.7f);
            if (similar.Count > 0)
            {
                var memSb = new StringBuilder();
                memSb.AppendLine("【跨运行记忆】以下是以往相似问题的成功回答，可参考其结构和关键信息：");
                foreach (var exp in similar)
                {
                    var snippet = exp.Response.Length > 500 ? exp.Response[..500] + "..." : exp.Response;
                    memSb.AppendLine($"--- 历史问答 (置信度={exp.Confidence:F2}, 奖励={exp.Reward:F2}) ---");
                    memSb.AppendLine(snippet);
                }
                memoryContext = memSb.ToString();
                _logger.LogInformation("SynapticMemory: injected {Count} similar past experiences", similar.Count);
            }
        }

        var (messages, streamOptions) = BuildSystemMessages(
            model, layer1Context, autoSearchContext, layer2Context,
            metaContext, metaAssessment, label, toolCount, dateTag, query,
            selectedTools);

        if (memoryContext != null)
            messages.Insert(0, new ChatMessage(ChatRole.System, memoryContext));

        // ReAct loop: stream response, detect tool calls, execute them, and retry
        var useStreaming = label != "fast" && label != "reflex";
        var fullResponse = new StringBuilder();
        var totalToolCalls = 0;
        var retryLevel = 0;
        var groundingFailed = false;
        const int maxToolRounds = 5;
        for (int round = 0; round < maxToolRounds; round++)
        {
            var toolCalls = new List<FunctionCallContent>();
            var responseText = new StringBuilder();

            if (useStreaming)
            {
                IAsyncEnumerable<ChatResponseUpdate>? streamResponse = null;
                try { streamResponse = _llm.GetStreamingResponseAsync(messages, streamOptions, cancellationToken); }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Stream init failed for query: {Query}", query[..Math.Min(query.Length, 60)]);
                }

                if (streamResponse == null) { yield return "Error connecting to provider."; yield break; }

                var streamChunks = new List<string>();
                Exception? streamError = null;
                try
                {
                    await foreach (var update in streamResponse)
                    {
                        foreach (var content in update.Contents)
                        {
                            if (content is FunctionCallContent fcc)
                                toolCalls.Add(fcc);
                            else if (content is TextReasoningContent rc && !string.IsNullOrEmpty(rc.Text))
                                streamChunks.Add($"<thinking>{rc.Text}</thinking>");
                        }
                        if (!string.IsNullOrEmpty(update.Text))
                        {
                            streamChunks.Add(update.Text);
                            responseText.Append(update.Text);
                        }
                    }
                }
                catch (Exception ex)
                {
                    streamError = ex;
                    _logger.LogWarning(ex, "Stream iteration failed, using partial response: {Len} chars",
                        responseText.Length);
                }

                foreach (var chunk in streamChunks)
                    yield return chunk;

                if (streamError != null && responseText.Length == 0)
                {
                    yield return "模型调用失败，请稍后重试。";
                    yield break;
                }
            }
            else
            {
                var response = await _llm.GetResponseAsync(messages, streamOptions, cancellationToken);
                responseText.Append(response.Text ?? "");
                if (response.Messages != null)
                {
                    foreach (var msg in response.Messages)
                        if (msg.Contents?.OfType<FunctionCallContent>() is { } fccs)
                            toolCalls.AddRange(fccs);
                }
                var text = responseText.ToString();
                if (!string.IsNullOrEmpty(text))
                    yield return text;
            }

            fullResponse.Append(responseText.ToString());

            if (toolCalls.Count == 0)
            {
                // Layer 4+5: Answer grounding verification + multi-level retry escalation
                if (!layer1HighConfidence)
                {
                    var toolContextForVerification = layer1Context ?? layer2Context ?? autoSearchContext;
                    var verification = _groundingVerifier.Verify(
                        responseText.ToString(), query, toolContextForVerification,
                        totalToolCalls > 0, totalToolCalls, layer1Context != null);

                    if (!verification.IsGrounded)
                    {
                        retryLevel++;
                        var escalation = await EscalateGroundingFailure(
                            query, retryLevel, verification, messages,
                            layer1Context, layer2Context, autoSearchContext, cancellationToken);

                        switch (escalation.Action)
                        {
                            case EscalationAction.YieldAndBreak:
                                groundingFailed = true;
                                foreach (var chunk in escalation.YieldChunks!)
                                    yield return chunk;
                                yield break;
                            case EscalationAction.Break:
                                groundingFailed = true;
                                yield break;
                            case EscalationAction.Continue:
                                messages.Add(new ChatMessage(ChatRole.System, escalation.RetryMessage!));
                                continue;
                        }
                    }
                    _logger.LogDebug("Grounding check passed");

                    // LLM-based semantic verification as second pass (only on first attempt with tool data)
                    if (retryLevel == 0 && !string.IsNullOrWhiteSpace(toolContextForVerification)
                        && toolContextForVerification!.Length > 200)
                    {
                        var llmVerification = await _groundingVerifier.VerifyWithLLMAsync(
                            responseText.ToString(), toolContextForVerification,
                            _llm, FlashModel, cancellationToken);

                        if (!llmVerification.IsGrounded)
                        {
                            retryLevel = 1;
                            _logger.LogWarning("LLM grounding check failed: {Issue}", llmVerification.Issue);
                            messages.Add(new ChatMessage(ChatRole.System,
                                $"【语义验证失败】{llmVerification.RetryInstruction}"));
                            continue;
                        }
                        _logger.LogDebug("LLM grounding check passed");
                    }
                }
                break;
            }

            totalToolCalls += toolCalls.Count;

            // Execute tool calls and feed results back into the conversation
            var assistantReply = responseText.ToString();
            messages.Add(new ChatMessage(ChatRole.Assistant, assistantReply) { Contents = new List<AIContent>(toolCalls) });

            foreach (var tc in toolCalls)
            {
                try
                {
                    var args = new Dictionary<string, object?>();
                    if (tc.Arguments != null)
                    {
                        foreach (var kv in tc.Arguments)
                            args[kv.Key] = kv.Value;
                    }
                    var result = await _toolRegistry.InvokeAsync(tc.Name, args, cancellationToken);
                    var resultText = result?.ToString() ?? "";
                    messages.Add(new ChatMessage(ChatRole.User, "") { Contents = new List<AIContent> { new FunctionResultContent(tc.CallId, resultText) } });
                    _logger.LogInformation("ReAct: executed {Tool} (callId={Id})", tc.Name, tc.CallId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ReAct: tool {Tool} failed", tc.Name);
                    messages.Add(new ChatMessage(ChatRole.User, "") { Contents = new List<AIContent> { new FunctionResultContent(tc.CallId, $"Error: {ex.Message}") } });
                }
            }

            if (!useStreaming)
            {
                // For non-streaming (fast/reflex), continue the loop silently
                continue;
            }
        }

        // MetaCognitiveLayer: record outcome for self-learning
        var finalResponse = fullResponse.ToString();

        if (groundingFailed)
        {
            _metaCognition.RecordOutcome(query, false);
            _logger.LogWarning("MetaCognition: recorded grounding failure for query: {Query}", query[..Math.Min(query.Length, 60)]);
        }
        else if (layer1HighConfidence)
        {
            // Layer 1 provided ground truth data → boost success confidence and reinforce domain
            _metaCognition.RecordOutcome(query, true);
            if (patternResult.ToolName != null)
                _metaCognition.ReinforceDomain(patternResult.ToolName, 0.1f);
        }
        else
        {
            // Standard outcome recording with stricter failure detection
            var hasFailure = finalResponse.Contains("未找到相关信息")
                || finalResponse.Contains("无法")
                || finalResponse.Length <= 20;
            _metaCognition.RecordOutcome(query, !hasFailure);
        }

        if (Interlocked.Increment(ref _requestCount) % 20 == 0)
        {
            var metrics = _metaCognition.GetMetrics();
            _logger.LogInformation("MetaCognition periodic: queries={Q} delegations={D} rate={R:F2} domains={Dom} familiarity={F:F2}",
                metrics["total_queries"], metrics["total_delegations"],
                metrics["delegation_rate"], metrics["domain_count"],
                metrics["avg_familiarity"]);
        }
    }

    public async IAsyncEnumerable<string> StreamWithModelAsync(
        string query, string modelId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var modelOptions = new ChatOptions { ModelId = modelId, Temperature = 0.3f, MaxOutputTokens = 4096, Tools = _toolRegistry.GetTools().ToList() };
        var modelMessages = new List<ChatMessage> { new(ChatRole.User, query) };

        IAsyncEnumerable<ChatResponseUpdate> modelStream;
        try { modelStream = _llm.GetStreamingResponseAsync(modelMessages, modelOptions, cancellationToken); }
        catch (Exception ex) { _logger.LogError(ex, "StreamWithModel init failed for {Model}", modelId); yield break; }
        if (modelStream == null) yield break;

        await foreach (var update in modelStream)
        {
            if (!string.IsNullOrEmpty(update.Text))
                yield return update.Text;
        }
    }

    public async Task<GovernorOutput> ProcessTypedAsync(GovernorInput input, CancellationToken cancellationToken = default)
    {
        var traceId = input.TraceId;
        var query = input.Query;

        if (_journal.TryConsumeMessage(out var humanMessage) && humanMessage != null)
        {
            _logger.LogInformation("Human message injected: {Message}", humanMessage[..Math.Min(humanMessage.Length, 100)]);
            query = humanMessage;
            input = GovernorInput.Create(query, traceId);
        }

        if (_dna != null)
        {
            try { await _dna.Consciousness.ProcessExperienceAsync(query, cancellationToken: cancellationToken); }
            catch (Exception ex) { _logger.LogDebug(ex, "DNA consciousness processing skipped"); }
        }

        var inputResult = await _input.ProcessAsync(new Handshake
        {
            To = "input", Action = "process",
            Payload = new Dictionary<string, object?> { ["query"] = query },
            ReplyTo = traceId
        }, cancellationToken);

        if (inputResult.Action == "reflex")
            return GovernorOutput.Reflex(inputResult.Payload?.GetValueOrDefault("command")?.ToString() ?? "", traceId);

        var label = inputResult.Payload?.GetValueOrDefault("label")?.ToString() ?? "deep";

        if (_duplexRouter != null)
        {
            var routeResult = _duplexRouter.Route(query);
            if (routeResult.CanAnswerLocally)
            {
                _erlLoop.RecordTrial(query[..Math.Min(query.Length, 60)], routeResult.LocalResponse, "l1_success", 0.8, true);
                return GovernorOutput.Success(routeResult.LocalResponse, traceId);
            }

            if (routeResult.Route == "delegate_l2")
            {
                var ctxResult = await _context.ProcessAsync(new Handshake
                {
                    To = "context", Action = "preload",
                    Payload = inputResult.Payload, ReplyTo = traceId
                }, cancellationToken);

                var ctx = ctxResult.Payload?.GetValueOrDefault("context")?.ToString() ?? "";
                var fullQuery = string.IsNullOrEmpty(ctx) ? query : $"Context:\n{ctx}\n\nQuery: {query}";
                var teachingResult = await _duplexRouter.RequestL2ReasoningAsync(fullQuery, routeResult, cancellationToken);
                if (teachingResult != null)
                {
                    _duplexRouter.LearnFromL2(query, teachingResult);
                    _context.AddTurn(query, teachingResult.Answer);
                    _duplexRouter.CacheResponse(query, teachingResult.Answer, "delegate_l2", "general", routeResult.Confidence);
                    _erlLoop.RecordTrial(query[..Math.Min(query.Length, 60)], teachingResult.Answer, "l2_teaching", 0.9, true);
                    return GovernorOutput.Success(teachingResult.Answer, traceId);
                }
            }
        }

        var contextResult = await _context.ProcessAsync(new Handshake
        {
            To = "context", Action = "preload",
            Payload = inputResult.Payload, ReplyTo = traceId
        }, cancellationToken);

        var preloadedContext = contextResult.Payload?.GetValueOrDefault("context")?.ToString() ?? "";
        _elasticMemory.Store($"ctx_{traceId}", preloadedContext[..Math.Min(preloadedContext.Length, 500)]);

        var routingResult = await _routing.ProcessAsync(new Handshake
        {
            To = "routing", Action = "select_provider",
            Payload = inputResult.Payload, ReplyTo = traceId
        }, cancellationToken);

        var model = routingResult.Payload?.GetValueOrDefault("model")?.ToString() ?? DefaultModel;
        var temperature = routingResult.Payload?.GetValueOrDefault("temperature") is float t ? t : 0.3f;
        var context = contextResult.Payload?.GetValueOrDefault("context")?.ToString() ?? "";
        var fullPrompt = string.IsNullOrEmpty(context) ? query : $"Context:\n{context}\n\nQuery: {query}";

        if (_bavtRouter.BudgetRatio < 0.1)
        {
            _logger.LogWarning("BAVT: budget nearly exhausted (ratio={Ratio:F2}), using fast path", _bavtRouter.BudgetRatio);
            var fastResp = await _llm.GetResponseAsync(fullPrompt, new ChatOptions { ModelId = FlashModel, Temperature = 0.3f }, cancellationToken);
            _erlLoop.RecordTrial(query[..Math.Min(query.Length, 60)], fastResp.Text ?? "", "budget_fast", 0.5, true);
            return GovernorOutput.Success(fastResp.Text ?? "", traceId);
        }

        string response;
        var maxTokens = _options.Value.AI.MaxTokens > 0 ? _options.Value.AI.MaxTokens : 4096;
        var options = new ChatOptions { ModelId = model, Temperature = temperature, MaxOutputTokens = maxTokens, Tools = _toolRegistry.GetTools().ToList() };

        try
        {
            response = label switch
            {
                "fast" or "reflex" => (await _llm.GetResponseAsync(fullPrompt, options, cancellationToken)).Text ?? "",
                _ when _fanOut != null => (await _fanOut.RaceAsync(fullPrompt, maxConcurrent: 3, cancellationToken: cancellationToken)).Answer,
                _ => await CollaborativeChatAsync(fullPrompt, options, cancellationToken)
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var fallbackModel = GetDegradedModel(model);
            if (fallbackModel != model)
            {
                var reflection = _reflectionEngine.Reflect(model, ex.Message, 1);
                _logger.LogWarning("Model {Model} failed, reflection={Action} fallback={Fallback}: {Error}",
                    model, reflection.Action, fallbackModel, ex.Message);
                options.ModelId = fallbackModel;
                options.Temperature = 0.3f;
                response = (await _llm.GetResponseAsync(fullPrompt, options, cancellationToken)).Text ?? "";

                _evolutionStore?.RecordLesson(new EvolutionLesson
                {
                    Category = LessonCategory.ModelDegradation.ToString(),
                    Severity = 0.6f,
                    Summary = $"Model {model} failed and degraded to {fallbackModel}",
                    Mitigation = $"Use {fallbackModel} as fallback; monitor {model} error rate",
                    SourceRun = traceId,
                    SourceStage = "l2_response"
                });
            }
            else { throw; }
        }

        _bavtRouter.Spend(1.0);
        _erlLoop.RecordTrial(query[..Math.Min(query.Length, 60)], response[..Math.Min(response.Length, 100)], "l2_response", 0.7, true);
        _elasticMemory.Store($"lts_{traceId}", response[..Math.Min(response.Length, 200)]);
        _echoDetector.RecordResponse(model, response[..Math.Min(response.Length, 500)]);
        if (_taskPipeline.HasPending) _logger.LogDebug("TaskPipeline: {Count} pending tasks", _taskPipeline.GetStats()["pending"]);

        if (Interlocked.Increment(ref _requestCount) % TrainingInterval == 0 && _options.Value.AI.OnnxEnabled)
        {
            _workQueue.Enqueue(async ct => { try { await Task.Run(() => TriggerPeriodicTraining(), ct); } catch { } }, "PeriodicTraining");
        }

        if (_dna != null)
        {
            try
            {
                var outputSafety = await _dna.Safety.EvaluateOutputAsync(response, cancellationToken);
                if (!outputSafety.Allowed)
                    return GovernorOutput.Blocked(outputSafety.BlockReason);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "DNA output safety check skipped"); }
        }

        var outputResult = await _output.ProcessAsync(new Handshake
        {
            To = "output", Action = "review",
            Payload = new Dictionary<string, object?> { ["response"] = response },
            ReplyTo = traceId
        }, cancellationToken);

        _context.AddTurn(query, response);
        response = outputResult.Payload?.GetValueOrDefault("response")?.ToString() ?? response;

        _duplexRouter?.CacheResponse(query, response, label, "general", 0.7f);

        if (_reasoning != null && !string.IsNullOrEmpty(response))
        {
            try { response = await _reasoning.EnhanceResponse(query, response); }
            catch (Exception ex) { _logger.LogDebug(ex, "Reasoning enhancement skipped"); }
        }

        _synapticMemory?.Store(new SynapticExperience
        {
            Type = SynapseType.Interaction, Query = query, Response = response,
            Label = label, Confidence = 0.7f, Reward = 0.7f,
            Metadata = $"model={model},trace={traceId}"
        });

        _dreamCycle?.RecordInteraction();

        _workQueue.Enqueue(async ct =>
        {
            try { await _self.ProcessAsync(new Handshake
            {
                To = "self", Action = "start_trace",
                Payload = new Dictionary<string, object?> { ["trace_id"] = traceId }
            }, ct); }
            catch { }
        }, "SelfGovernor trace");

        return GovernorOutput.Success(response, traceId);
    }

    private async Task<string> CollaborativeChatAsync(string prompt, ChatOptions baseOptions, CancellationToken ct)
    {
        var history = _context.CompressHistory();
        var iterativePrompt = string.IsNullOrEmpty(history)
            ? prompt
            : $"Previous conversation:\n{history}\n\nCurrent query:\n{prompt}\n\nPlease provide a thorough, well-reasoned response.";

        var messages = new List<ChatMessage> { new(ChatRole.User, iterativePrompt) };
        var sb = new System.Text.StringBuilder();
        string? lastModel = null;

        await foreach (var update in _llm.GetStreamingResponseAsync(messages, baseOptions, ct))
        {
            if (!string.IsNullOrEmpty(update.Text))
                sb.Append(update.Text);
            lastModel ??= update.ModelId;
        }
        var response = sb.ToString();

        if (string.IsNullOrWhiteSpace(response))
            throw new InvalidOperationException("Empty streaming response");

        // Fire-and-forget review: doesn't block the critical path
        var capturedResponse = response;
        var capturedPrompt = iterativePrompt;
        _workQueue.Enqueue(async ct =>
        {
            try
            {
                var reviewPrompt = $"Review this response for accuracy and completeness. If it needs improvement, provide the improved version:\n\n{capturedResponse}";
                var reviewOptions = new ChatOptions { ModelId = baseOptions.ModelId, Temperature = 0.1f, MaxOutputTokens = 2048 };
                var reviewed = await _llm.CompleteAsync(reviewPrompt, reviewOptions, ct);
                if (!string.IsNullOrWhiteSpace(reviewed))
                {
                    _synapticMemory?.Store(new SynapticExperience
                    {
                        Type = SynapseType.Correction, Query = capturedPrompt, Response = reviewed,
                        Label = "reviewed", Confidence = 0.85f, Reward = 0.9f,
                        Metadata = $"model={baseOptions.ModelId},original_len={capturedResponse.Length}"
                    });
                }
            }
            catch { }
        }, "LLM review");

        return response;
    }

    private async Task SilentSelfCheckAsync(string response)
    {
        try { await _output.SilentSelfCheckAsync(response); }
        catch (Exception ex) { _logger.LogDebug(ex, "Silent self-check skipped"); }
    }

    /// Periodically triggers ONNX retraining from collected synaptic experiences.
    /// Called every ~50 requests as a background fire-and-forget task.
    private async Task TriggerPeriodicTraining()
    {
        try
        {
            var samples = _synapticMemory?.GetTrainingSamples(maxCount: TrainingInterval) ?? new();

            if (samples.Count >= 10)
            {
                // Train and export ONNX weights
                var synapticDir = Path.Combine(AppContext.BaseDirectory, "synaptic");
                var trainer = new SynapticTrainer(Path.Combine(synapticDir, "models"));
                var result = trainer.TrainIntentClassifier(samples);

                if (result.Success)
                {
                    var inference = new SynapticInference();
                    if (!string.IsNullOrEmpty(result.OnnxPath) && inference.LoadOnnxModel(result.OnnxPath))
                    {
                        _logger.LogInformation("ONNX retraining: accuracy={Acc:F2} samples={N} onnx={Path}",
                            result.Accuracy, result.TrainingSamples, result.OnnxPath);

                        _verifiableRegistry?.RegisterMeasurement(new NumericMeasurement
                        {
                            Key = "onnx_training_accuracy",
                            Condition = $"samples={result.TrainingSamples}",
                            Value = result.Accuracy,
                            SourceExperiment = "lts_periodic_training",
                            Domain = "intent_classifier",
                            IsVerified = true,
                            Provenance = result.OnnxPath
                        });

                        if (result.Accuracy < 0.5f)
                        {
                            _evolutionStore?.RecordLesson(new EvolutionLesson
                            {
                                Category = LessonCategory.QualityRegression.ToString(),
                                Severity = 0.5f,
                                Summary = $"ONNX training accuracy low ({result.Accuracy:F2})",
                                Mitigation = "Increase training samples or adjust hyperparameters",
                                SourceRun = result.OnnxPath,
                                SourceStage = "onnx_training"
                            });
                        }

                        inference.Dispose();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Periodic training skipped");
        }
    }

    private void RecordSelfHealingLesson(string query, int retryLevel, string checkType)
    {
        if (_evolutionStore == null) return;

        try
        {
            _evolutionStore.RecordLesson(new EvolutionLesson
            {
                Category = LessonCategory.QualityRegression.ToString(),
                Severity = Math.Min(0.3f + retryLevel * 0.2f, 1.0f),
                Summary = $"Repeated grounding failures (L{retryLevel} retries, type={checkType}): {query[..Math.Min(query.Length, 80)]}",
                Mitigation = retryLevel >= 3
                    ? "Add Layer1 pattern or pre-emptive tool execution for this query type"
                    : "Enable stricter grounding verification for this domain",
                SourceRun = Guid.NewGuid().ToString("N")[..8],
                SourceStage = "l5_retry_escalation"
            });
            _logger.LogInformation("Self-healing: recorded lesson (severity={Sev}, L{Level})",
                Math.Min(0.3f + retryLevel * 0.2f, 1.0f), retryLevel);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to record self-healing lesson");
        }
    }

    private async Task<string?> ForceExecuteForRetryAsync(string query, CancellationToken ct)
    {
        // Force auto-search as last-resort data injection before retry
        if (_toolRegistry.HasTool("web_search"))
        {
            try
            {
                var result = await _toolRegistry.InvokeAsync("web_search",
                    new Dictionary<string, object?> { ["query"] = query, ["maxResults"] = 3 }, ct);
                var text = result?.ToString();
                if (!string.IsNullOrWhiteSpace(text) && text.Length > 50)
                {
                    var truncated = text.Length > 2000 ? text[..2000] : text;
                    return $"【强制搜索】以下是为确保事实准确性而强制执行的搜索结果：\n{truncated}";
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ForceExecuteForRetry web_search failed");
            }
        }

        // Also try filesystem_list for relevant queries
        if (_toolRegistry.HasTool("shell_exec") && query.Contains("文件"))
        {
            try
            {
                var result = await _toolRegistry.InvokeAsync("shell_exec",
                    new Dictionary<string, object?> { ["command"] = query.Contains("目录") ? "ls -la" : "dir", ["workingDirectory"] = null! }, ct);
                var text = result?.ToString();
                if (!string.IsNullOrWhiteSpace(text) && text.Length > 10)
                    return $"【强制命令执行】以下是为确保准确性而强制执行的命令结果：\n{text[..Math.Min(text.Length, 2000)]}";
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ForceExecuteForRetry shell_exec failed");
            }
        }

        return null;
    }

    private enum EscalationAction { Continue, Break, YieldAndBreak }

    private sealed record EscalationResult(
        EscalationAction Action,
        List<string>? YieldChunks = null,
        string? RetryMessage = null)
    {
        public static EscalationResult ContinueLoop(string msg) => new(EscalationAction.Continue, RetryMessage: msg);
        public static EscalationResult BreakLoop => new(EscalationAction.Break);
        public static EscalationResult YieldAndBreak(List<string> chunks) => new(EscalationAction.YieldAndBreak, YieldChunks: chunks);
    }

    private async Task<EscalationResult> EscalateGroundingFailure(
        string query, int retryLevel, GroundingResult verification,
        List<ChatMessage> messages, string? layer1Context, string? layer2Context,
        string? autoSearchContext, CancellationToken ct)
    {
        var metaMetrics = _metaCognition.GetMetrics();
        var avgFamiliarity = metaMetrics.TryGetValue("avg_familiarity", out var af)
            ? Convert.ToSingle(af) : 0.1f;
        var erlSuccessRate = _erlLoop.SuccessRate;

        // Blend ERL global success rate (actual outcomes) with MetaCog domain familiarity (learning).
        // High ERL success → system is generally doing well → fewer retries needed.
        // High familiarity → domain is well-known → fewer retries needed.
        var blendedConfidence = (float)(avgFamiliarity * 0.6 + erlSuccessRate * 0.4);
        var maxRetries = Math.Clamp((int)(6 - blendedConfidence * 5), 2, 5);
        var forceToolLevel = blendedConfidence < 0.3f ? 1 : 2;

        _logger.LogWarning("Grounding check failed L{Level}/{Max}: {Issue} (type={Type}, fam={Fam:F2}, erl={ERL:F2})",
            retryLevel, maxRetries, verification.Issue, verification.CheckType, avgFamiliarity, erlSuccessRate);

        if (retryLevel >= maxRetries)
        {
            _metaCognition.RecordOutcome(query, false);
            RecordSelfHealingLesson(query, retryLevel, verification.CheckType);

            var allContext = new List<string>();
            if (layer1Context != null) allContext.Add(layer1Context);
            if (layer2Context != null) allContext.Add(layer2Context);
            if (autoSearchContext != null) allContext.Add(autoSearchContext);

            if (allContext.Count > 0)
                return EscalationResult.YieldAndBreak(allContext);

            allContext.Add("抱歉，经过多次尝试仍无法提供可靠的答案。建议换个方式提问或提供更多具体信息。");
            return EscalationResult.YieldAndBreak(allContext);
        }

        if (retryLevel >= forceToolLevel)
        {
            var forcedContext = await ForceExecuteForRetryAsync(query, ct);
            if (forcedContext != null)
                messages.Add(new ChatMessage(ChatRole.System,
                    $"【系统强制工具执行 L{retryLevel}】以下是为确保回答准确而强制获取的数据，必须基于此回答：\n{forcedContext}"));
        }

        var severity = retryLevel >= maxRetries - 1 ? "【严重警告】" : "";
        return EscalationResult.ContinueLoop(
            $"{severity}【事实核查失败 L{retryLevel} - {verification.CheckType}】{verification.RetryInstruction}");
    }

    private (List<ChatMessage> Messages, ChatOptions Options) BuildSystemMessages(
        string model,
        string? layer1Context, string? autoSearchContext, string? layer2Context,
        string? metaContext, MetaCognitiveAssessment metaAssessment, string label,
        int toolCount, string dateTag, string query,
        List<AITool> selectedTools)
    {
        var messages = new List<ChatMessage>();
        var options = new ChatOptions
        {
            ModelId = model,
            Temperature = 0.3f,
            MaxOutputTokens = 4096,
            Tools = selectedTools
        };

        if (layer1Context != null)
            messages.Add(new ChatMessage(ChatRole.System, layer1Context));
        if (autoSearchContext != null)
            messages.Add(new ChatMessage(ChatRole.System, autoSearchContext));
        if (layer2Context != null)
            messages.Add(new ChatMessage(ChatRole.System, layer2Context));

        var allLayersEmpty = layer1Context == null && layer2Context == null && autoSearchContext == null;
        if (allLayersEmpty && metaAssessment.ShouldDelegate && label != "fast" && label != "reflex")
        {
            messages.Add(new ChatMessage(ChatRole.System,
                "【系统提示】所有自动工具和搜索均未能获取到相关数据。你必须如实告知用户当前无法回答该问题。" +
                "严禁编造任何具体数字、名称或事实。可以建议用户提供更多信息或换个方式提问。"));
        }

        var selCount = selectedTools.Count;
        if (selCount > 0)
        {
            var toolNames = string.Join("、", selectedTools.Take(10).Select(t => t.Name));
            if (layer1Context != null)
            {
                messages.Add(new ChatMessage(ChatRole.System,
                    $"你可以使用以下工具: {toolNames} 等共 {selCount} 个。" +
                    "【关键规则】上面已经通过自动工具获取了真实数据，你的任务是：" +
                    "1) 严格基于上述【Layer1 自动执行工具】的结果回答用户，一字一句都要有数据依据。" +
                    "2) 严禁自行推测、联想或编造任何工具结果中不存在的信息。" +
                    "3) 如果工具结果为空或报错，必须如实告知，不得猜测原因。" +
                    "4) 不得建议用户去执行命令——系统已经执行过了。"));
            }
            else
            {
                messages.Add(new ChatMessage(ChatRole.System,
                    $"你可以使用以下工具: {toolNames} 等共 {selCount} 个。" +
                    "重要规则: 1) 遇到需要实时信息、外部数据或事实核查的问题，必须先调用工具再回答。" +
                    "2) 回答时只能陈述工具返回的事实数据，严禁自行推测、联想或编造任何信息。" +
                    "3) 如果工具返回空结果或不确定信息，必须如实告知用户'未找到相关信息'。" +
                    "4) 声称使用了工具（如\"已使用shell_exec\"）必须在响应中发出 tool_call，否则视为编造。"));
            }
        }

        if (metaContext != null)
            messages.Add(new ChatMessage(ChatRole.System, metaContext));

        messages.Add(new ChatMessage(ChatRole.User, $"{dateTag}\n{query}"));
        return (messages, options);
    }

    public async ValueTask DisposeAsync()
    {
        await _workQueue.DisposeAsync();
        GC.SuppressFinalize(this);
    }

}
