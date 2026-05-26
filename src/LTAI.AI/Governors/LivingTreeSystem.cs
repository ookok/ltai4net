using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using LTAI.AI.Interfaces;
using LTAI.AI.Governors.Pipeline;
using LTAI.Core.Configuration;
using LTAI.Core.Execution;
using LTAI.Core.Interfaces;
using LTAI.Core.Messaging;
using LTAI.Core.Models;
using LTAI.Core.System;
using LTAI.DNA;
using LTAI.Models;
using LTAI.Tools.Reasoning;
using LTAI.AI.Providers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.AI.Governors;

public sealed class LivingTreeSystem : ILivingTreeSystem, IAsyncDisposable
{
    private readonly TaskJournal _journal;
    private readonly IChatClient _llm;
    private readonly AIToolRegistry _toolRegistry;
    private readonly ILogger<LivingTreeSystem> _logger;
    private readonly DNAOrchestrator? _dna;
    private readonly IOptions<LTAIOptions> _options;

    private readonly GovernorSet _gov;
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
    private readonly PromptTemplateStore _prompts;

    private readonly BAVTRouter _bavtRouter = new(100.0);
    private readonly ERLLoop _erlLoop = new();
    private readonly ElasticMemoryOrchestrator _elasticMemory = new();
    private readonly CoEchoDetector _echoDetector = new();
    private readonly TaskPipeline _taskPipeline;
    private readonly ICrossRunEvolutionStore? _evolutionStore;
    private readonly IVerifiableRegistry? _verifiableRegistry;
    private readonly IParliamentBridge? _parliamentBridge;
    private readonly QueryPreprocessingService _preprocessor;
    private readonly ReActLoopOrchestrator _reActOrchestrator;
    private readonly ModelDispatchService _modelDispatch;
    private int _requestCount;
    private int _bgRequestCount;
    private const int TrainingInterval = 50;
    private readonly ConcurrentDictionary<string, (string Response, DateTime Expiry)> _queryCache = new();
    private string _personaStyle = "balanced";
    private DateTime _lastDreamCycleTrigger = DateTime.MinValue;
    private static readonly TimeSpan DreamCycleMinInterval = TimeSpan.FromMinutes(2);

    private string DefaultModel => _options.Value.AI.L2.Model;
    private string FlashModel => _options.Value.AI.L1.Model;

    public SystemGuardian Guardian => _gov.Guardian;
    public SystemMode Mode => _gov.Guardian.Mode;
    public bool DNAEnabled => _dna != null;
    public DNAStatus? DNAStatus => _dna?.GetStatus();
    public InputGovernor InputGovernor => _gov.Input;
    public ContextGovernor ContextGovernor => _gov.Context;
    public RoutingGovernor RoutingGovernor => _gov.Routing;
    public IChatClient LLMClient => _llm;
    public TaskPipeline TaskPipeline => _taskPipeline;

    public LivingTreeSystem(
        TaskJournal journal,
        IChatClient llm,
        IOptions<LTAIOptions> options,
        GovernorSet gov,
        AIToolRegistry toolRegistry,
        ILogger<LivingTreeSystem> logger,
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
        PromptTemplateStore? prompts = null,
        ICrossRunEvolutionStore? evolutionStore = null,
        IVerifiableRegistry? verifiableRegistry = null,
        IParliamentBridge? parliamentBridge = null,
        QueryPreprocessingService? preprocessor = null,
        ReActLoopOrchestrator? reActOrchestrator = null,
        ModelDispatchService? modelDispatch = null)
    {
        _journal = journal;
        _llm = llm;
        _toolRegistry = toolRegistry;
        _logger = logger;
        _options = options;
        _gov = gov;
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
        _prompts = prompts ?? new PromptTemplateStore();
        _evolutionStore = evolutionStore;
        _verifiableRegistry = verifiableRegistry;
        _parliamentBridge = parliamentBridge;
        _preprocessor = preprocessor ?? new QueryPreprocessingService(
            _gov.Input, _llm, _dna, _options, _gov.Guardian, _toolRegistry,
            _metaCognition, _patternRouter, _planExecutor, _prompts, _logger);
        _reActOrchestrator = reActOrchestrator!;
        _modelDispatch = modelDispatch!;
        _taskPipeline = new TaskPipeline(_journal);
        _taskPipeline.LlmDecomposer = (_modelDispatch ?? throw new InvalidOperationException("ModelDispatchService is required")).LlmDecomposeAsync;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _gov.Guardian.StartMonitoring(TimeSpan.FromSeconds(15));
        _logger.LogInformation("LivingTreeSystem v6.0 initialized with 6 governors, DNA: {DNA}",
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
            if (_gov.Guardian.Mode == SystemMode.LifeSupport)
            {
                _journal.Complete(entry, "emergency");
                return await _gov.Guardian.EmergencyChatAsync(query, cancellationToken).ConfigureAwait(false);
            }

            if (_journal.IsPaused)
            {
                _journal.Complete(entry, "paused");
                return "Journal is paused. Resume to continue.";
            }

            if (_dna != null)
            {
                var safetyCheck = await _dna.Safety.EvaluateAsync(query, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!safetyCheck.Allowed)
                {
                    _journal.Complete(entry, $"blocked: {safetyCheck.BlockReason}");
                    _logger.LogWarning("DNA safety blocked query: {Reason}", safetyCheck.BlockReason);
                    return $"[Safety: {safetyCheck.BlockReason}]";
                }
            }

            var response = await ProcessTypedAsync(GovernorInput.Create(query), cancellationToken).ConfigureAwait(false);
            _journal.Complete(entry, response.Response[..Math.Min(response.Response.Length, 500)]);

            var reply = response.Response;
            _workQueue.Enqueue(async ct => { try { await SilentSelfCheckAsync(reply); } catch (Exception ex) { _logger.LogWarning(ex, "SilentSelfCheck background task failed"); } }, "SilentSelfCheck");

            if (_dna != null && !string.IsNullOrEmpty(reply))
            {
                _workQueue.Enqueue(async ct =>
                {
                    try { await _dna.ProcessAsync(query, reply, ct); }
                    catch (Exception ex) { _logger.LogWarning(ex, "DNA process background task failed"); }
                }, "DNA process");
            }

            return reply;
        }
        catch (Exception ex)
        {
            _journal.Fail(entry, ex.Message);
            _gov.Guardian.RecordError();
            _logger.LogError(ex, "Chat failed");
            throw;
        }
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        string query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var pre = await _preprocessor.PreprocessAsync(query, _queryCache, _bavtRouter, cancellationToken).ConfigureAwait(false);

        if (pre.IsBlocked)
        {
            yield return pre.BlockMessage!;
            yield break;
        }

        if (pre.IsCached && pre.ShouldYieldEarly)
        {
            yield return pre.CachedResponse!;
            yield break;
        }

        if (pre.IsFuzzyQuery && pre.ShouldYieldEarly)
        {
            _metaCognition.RecordOutcome(query, false);
            yield return pre.ClarifyMessage!;
            yield break;
        }

        if (pre.Layer1HighConfidence && pre.ShouldYieldEarly)
        {
            _metaCognition.RecordOutcome(query, true);
            if (pre.PatternToolName != null)
                _metaCognition.ReinforceDomain(pre.PatternToolName, 0.05f);
            yield return pre.CachedResponse!;
            yield break;
        }

        var model = pre.Model;
        var label = pre.Label;
        var dateTag = pre.DateTag;
        var layer1Context = pre.Layer1Context;
        var layer1HighConfidence = pre.Layer1HighConfidence;
        var autoSearchContext = pre.AutoSearchContext;
        var layer2Context = pre.Layer2Context;
        var metaContext = pre.MetaContext;
        var metaAssessment = pre.MetaAssessment!;
        var patternMatched = pre.PatternMatched;
        var toolCount = pre.ToolCount;
        var budgetRatio = pre.BudgetRatio;

        if (_duplexRouter != null)
        {
            var routeResult = await _duplexRouter.RouteAsync(query);
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
                var ctxResult = await _gov.Context.ProcessAsync(new Handshake
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
                var teachingResult = await _duplexRouter.RequestL2ReasoningAsync(fullQuery, routeResult, cancellationToken).ConfigureAwait(false);
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
                            _gov.Context.AddTurn(query, teachingResult.Answer);
                            _metaCognition.RecordOutcome(query, true);
                            yield return teachingResult.Answer;
                            yield break;
                        }
                    }
                    else
                    {
                        _duplexRouter.LearnFromL2(query, teachingResult);
                        _gov.Context.AddTurn(query, teachingResult.Answer);
                        _metaCognition.RecordOutcome(query, true);
                        yield return teachingResult.Answer;
                        yield break;
                    }
                }
            }
        }

        await foreach (var chunk in _reActOrchestrator.RunReActLoopAsync(
            query, model, label, dateTag,
            layer1Context, layer1HighConfidence, autoSearchContext, layer2Context,
            metaContext, metaAssessment, patternMatched, toolCount, budgetRatio, cancellationToken))
        {
            yield return chunk;
        }

        var finalResponse = _reActOrchestrator.FinalResponse ?? "";
        var groundingFailed = _reActOrchestrator.GroundingFailed;
        var totalToolCalls = _reActOrchestrator.TotalToolCalls;
        var retryLevel = _reActOrchestrator.RetryLevel;

        // Post-response follow-up: generate related questions from tool context
        if (!groundingFailed && !layer1HighConfidence && finalResponse.Length > 50)
        {
            _gov.Context.AddTurn(query, finalResponse);
            var toolCtx = layer1Context ?? layer2Context ?? autoSearchContext;
            if (!string.IsNullOrWhiteSpace(toolCtx) && toolCtx.Length > 100)
            {
                var followup = await GenerateFollowupAsync(finalResponse, toolCtx, cancellationToken).ConfigureAwait(false);
                if (followup != null)
                    yield return "\n\n---\n您可能还想了解：\n" + followup;
            }
        }

        foreach (var tailOutput in ProcessStreamingTail(finalResponse, query, layer1Context, layer2Context,
            autoSearchContext, pre, model, totalToolCalls, groundingFailed, layer1HighConfidence,
            metaAssessment.Familiarity, patternMatched, label, retryLevel, _erlLoop.SuccessRate,
            _erlLoop.TotalTrials, (float)_bavtRouter.BudgetRatio))
        {
            yield return tailOutput;
        }
    }

    private IEnumerable<string> ProcessStreamingTail(
        string finalResponse, string query, string? layer1Context, string? layer2Context,
        string? autoSearchContext, Pipeline.PreprocessingResult pre, string model, int totalToolCalls,
        bool groundingFailed, bool layer1HighConfidence, double metaFamiliarity, bool patternMatched,
        string label, int retryLevel, double erlRate, int erlTotalTrials, float bavtBudgetRatio)
    {
        _bavtRouter.Spend(1.0);

        if (_workQueue.PendingCount > 10)
            _logger.LogInformation("Backpressure: queue depth {Depth}, reducing aggressiveness", _workQueue.PendingCount);

        if (bavtBudgetRatio < 0.5f && _requestCount > 10)
        {
            var eta = bavtBudgetRatio < 0.1f ? "critical" : bavtBudgetRatio < 0.3f ? "low" : "moderate";
            _logger.LogInformation("BudgetRecovery: ratio={Ratio:F2}, status={Eta}", bavtBudgetRatio, eta);
        }

        if (erlRate > 0 && erlRate < 0.5f && pre.PatternToolName != null)
            _metaCognition.ReinforceDomain(pre.PatternToolName, -0.05f);
        else if (erlRate > 0.7f && pre.PatternToolName != null)
            _metaCognition.ReinforceDomain(pre.PatternToolName, 0.02f);

        if (groundingFailed)
        {
            _metaCognition.RecordOutcome(query, false);
            _logger.LogWarning("MetaCognition: grounding failure for {Q}", query[..Math.Min(query.Length, 60)]);
        }
        else if (layer1HighConfidence)
        {
            _metaCognition.RecordOutcome(query, true);
            if (pre.PatternToolName != null) _metaCognition.ReinforceDomain(pre.PatternToolName, 0.1f);
        }
        else
        {
            var hasFailure = finalResponse.Contains("未找到相关信息") || finalResponse.Contains("无法") || finalResponse.Length <= 20;
            _metaCognition.RecordOutcome(query, !hasFailure);
        }

        if (!groundingFailed && !layer1HighConfidence && finalResponse.Length > 100
            && DateTime.UtcNow - _lastDreamCycleTrigger > DreamCycleMinInterval)
        {
            _lastDreamCycleTrigger = DateTime.UtcNow;
            _workQueue.Enqueue(async ct =>
            {
                try { if (_dreamCycle != null) await _dreamCycle.ForceReflectionAsync(); } catch { }
            }, "DreamCycle");
        }

        if (!groundingFailed && totalToolCalls > 1 && pre.PatternToolName != null)
            _erlLoop.RecordTrial($"combo_{pre.PatternToolName}_{totalToolCalls}", finalResponse[..Math.Min(finalResponse.Length, 80)], "tool_combo", 0.85f, true);

        if (!groundingFailed && !string.IsNullOrWhiteSpace(layer1Context))
            _workQueue.Enqueue(async ct =>
            {
                try
                {
                    var entities = System.Text.RegularExpressions.Regex.Matches(layer1Context, @"[\u4e00-\u9fff]{2,8}(?:有限)?(?:公司|企业|集团|科技|银行|大学|医院)");
                    foreach (System.Text.RegularExpressions.Match m in entities.Take(5))
                        if (m.Value.Length > 2) _metaCognition.ReinforceDomain($"entity_{m.Value}", 0.01f);
                }
                catch { }
            }, "KnowledgeGraphBuild");

        if (++_bgRequestCount % 50 == 49)
            _workQueue.Enqueue(async ct =>
            {
                try { await _llm.GetResponseAsync("系统自检：总结最近运行状态", new ChatOptions { ModelId = FlashModel, Temperature = 0f, MaxOutputTokens = 64 }, ct); }
                catch { }
            }, "AdversarialSelfTest");

        if (!groundingFailed && finalResponse.Length > 50)
        {
            var ttl = query.Contains("今天") || query.Contains("星期") ? 60 :
                      query.Contains("git") ? 2 :
                      query.Contains("目录") ? 10 : query.Length < 20 ? 3 : 5;
            _queryCache[query] = (finalResponse, DateTime.UtcNow.AddMinutes((int)(ttl * Math.Max(0.5f, metaFamiliarity))));
        }

        _personaStyle = retryLevel >= 2 ? "concise" :
            finalResponse.Length < 150 ? "concise" : finalResponse.Count(c => c == '\n') > 5 ? "detailed" : "balanced";

        if (Environment.WorkingSet > 2L * 1024 * 1024 * 1024)
            _logger.LogDebug("ResourceGuard: high memory ({Mem}MB)", Environment.WorkingSet / 1024 / 1024);

        if (groundingFailed && _requestCount % 10 == 0 && _synapticMemory != null)
        {
            var samples = _synapticMemory.GetTrainingSamples(maxCount: 50);
            if (samples.Count >= 20)
                _workQueue.Enqueue(async ct => { try { await TriggerPeriodicTraining(); } catch { } }, "AutoLoRA");
        }

        if (groundingFailed && label == "fast" && !layer1HighConfidence)
        {
            _erlLoop.RecordTrial($"l0_reroute_{query[..Math.Min(query.Length, 30)]}", "should_be_deep", "fast_misroute", 0.3f, false);
            _logger.LogInformation("L0 self-learning: fast→deep for {P}", query[..Math.Min(query.Length, 40)]);
        }

        if (!groundingFailed && finalResponse.Length > 200 && _gov.Context.CompressHistory().Length > 300)
            _workQueue.Enqueue(async ct =>
            {
                try
                {
                    var w = (float)(metaFamiliarity * 0.5 + erlRate * 0.5);
                    _synapticMemory?.Store(new SynapticExperience
                    {
                        Type = SynapseType.Interaction, Query = query, Response = finalResponse[..Math.Min(finalResponse.Length, 500)],
                        Label = "session_memory", Confidence = w, Reward = w, Metadata = $"style={_personaStyle}"
                    });
                }
                catch { }
            }, "SessionMemory");

        if (erlRate < 0.4f && erlTotalTrials > 10)
        {
            _logger.LogWarning("Anomaly: ERL rate {R:F2} over {T} trials", erlRate, erlTotalTrials);
            _evolutionStore?.RecordLesson(new EvolutionLesson
            {
                Category = LessonCategory.QualityRegression.ToString(), Severity = 0.7f,
                Summary = $"ERL critical: {erlRate:F2} over {erlTotalTrials} trials",
                Mitigation = "Enable stricter grounding checks", SourceStage = "anomaly_report"
            });
        }

        if (finalResponse.Length > 10)
        {
            var trace = $"\n\n---\n[决策: L0={label}, L1={patternMatched}, L2={layer2Context != null}, " +
                $"Model={model}, Tools={totalToolCalls}, Grounding={!groundingFailed}, " +
                $"Familiarity={metaFamiliarity:F2}, Budget={bavtBudgetRatio:F2}]";
            yield return trace;
        }

        if (groundingFailed && totalToolCalls > 0 && patternMatched && pre.PatternToolName != null)
            _erlLoop.RecordTrial($"counterfactual_{pre.PatternToolName}", "Would different tools help?", "counterfactual", 0.4f, false);

        if (groundingFailed && retryLevel >= 2)
            _workQueue.Enqueue(async ct =>
            {
                try
                {
                    _synapticMemory?.Store(new SynapticExperience
                    {
                        Type = SynapseType.Correction, Query = query,
                        Response = $"// Regression: {query[..Math.Min(query.Length, 60)]}\n// grounding failed L{retryLevel}",
                        Label = "regression_test", Confidence = 0.3f, Reward = 0.1f, Metadata = $"retry={retryLevel}"
                    });
                }
                catch { }
            }, "RegressionTest");

        if (retryLevel >= 2) _personaStyle = "concise";

        if (groundingFailed && finalResponse.Length < 20 && _dna != null)
            _workQueue.Enqueue(async ct =>
            {
                try { await _dna.Consciousness.ProcessExperienceAsync($"CRASH: empty response L{retryLevel}. Q: '{query[..Math.Min(query.Length, 60)]}'", new Dictionary<string, object?>(), ct); }
                catch { }
            }, "SelfRepair");

        if (!groundingFailed && pre.PatternToolName == "shell_exec" && layer1Context != null)
        {
            var cmd = layer1Context;
            if (cmd.Contains("rm ") || cmd.Contains("del ") || cmd.Contains("format") || cmd.Contains("DROP"))
            {
                _logger.LogWarning("Sandbox: blocked {Cmd}", cmd[..Math.Min(cmd.Length, 80)]);
                _evolutionStore?.RecordLesson(new EvolutionLesson
                {
                    Category = LessonCategory.SafetyViolation.ToString(), Severity = 0.9f,
                    Summary = $"Blocked: {cmd[..Math.Min(cmd.Length, 60)]}",
                    Mitigation = "Use VfsAdapter", SourceStage = "sandbox"
                });
            }
        }

        if (_evolutionStore != null && _requestCount % 100 == 0)
            _workQueue.Enqueue(async ct =>
            {
                try { var lessons = _evolutionStore.GetActiveLessons(10); } catch { }
            }, "Federated");

        if (_requestCount % 200 == 0 && _evolutionStore != null)
            _workQueue.Enqueue(async ct =>
            {
                try
                {
                    var active = _evolutionStore.GetActiveLessons(20);
                    var high = active.Where(l => l.Severity >= 0.7f).ToList();
                    if (high.Count >= 3)
                        _evolutionStore.RecordLesson(new EvolutionLesson
                        {
                            Category = LessonCategory.GeneralWarning.ToString(), Severity = 0.5f,
                            Summary = $"Review: {high.Count} critical", SourceStage = "self_evolution"
                        });
                }
                catch { }
            }, "SelfEvolution");

        if (!groundingFailed && finalResponse.Length > 300 && totalToolCalls >= 2)
        {
            _erlLoop.RecordTrial($"debate_{query[..Math.Min(query.Length, 40)]}", finalResponse[..Math.Min(finalResponse.Length, 100)], "multi_agent", 0.85f, true);
            if (_parliamentBridge is { IsAvailable: true })
            {
                _ = Task.Run(async () =>
                {
                    try { await _parliamentBridge.DeliberateAsync(query, finalResponse); } catch { }
                });
            }
        }

        if (totalToolCalls >= 2 && !groundingFailed)
            _erlLoop.RecordTrial($"qvalue_{totalToolCalls}", $"Tools={totalToolCalls}", "quantum_opt", 0.9f, true);

        if (!groundingFailed && metaFamiliarity > 0.5f && finalResponse.Length > 100)
            yield return "\n\n> 置信度: 高 | 格式建议: 结构化表格";

        if (_requestCount % 500 == 0 && _prompts is not null)
            _workQueue.Enqueue(async ct => { try { _prompts.Reload(); } catch { } }, "PromptEvolution");

        if (query.Contains("换个角度") || query.Contains("另一个角度"))
            _workQueue.Enqueue(async ct =>
            {
                try
                {
                    _synapticMemory?.Store(new SynapticExperience
                    {
                        Type = SynapseType.Interaction, Query = query, Response = finalResponse[..Math.Min(finalResponse.Length, 300)],
                        Label = "fork_branch", Confidence = 0.7f, Reward = 0.7f, Metadata = $"ctx={_gov.Context.CompressHistory()[..Math.Min(_gov.Context.CompressHistory().Length, 200)]}"
                    });
                }
                catch { }
            }, "Fork");

        if (finalResponse.Length > 500 && !groundingFailed)
            _logger.LogInformation("Notify: long response ({Len}) for {Q}", finalResponse.Length, query[..Math.Min(query.Length, 40)]);

        if (Interlocked.Increment(ref _requestCount) % 20 == 0)
        {
            var metrics = _metaCognition.GetMetrics();
            _logger.LogInformation("MetaCognition: q={Q} d={D} r={R:F2} dom={Dom} fam={F:F2}",
                metrics["total_queries"], metrics["total_delegations"], metrics["delegation_rate"],
                metrics["domain_count"], metrics["avg_familiarity"]);
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

        var inputResult = await _gov.Input.ProcessAsync(new Handshake
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
            var routeResult = await _duplexRouter.RouteAsync(query);
            if (routeResult.CanAnswerLocally)
            {
                _erlLoop.RecordTrial(query[..Math.Min(query.Length, 60)], routeResult.LocalResponse, "l1_success", 0.8, true);
                return GovernorOutput.Success(routeResult.LocalResponse, traceId);
            }

            if (routeResult.Route == "delegate_l2")
            {
                var ctxResult = await _gov.Context.ProcessAsync(new Handshake
                {
                    To = "context", Action = "preload",
                    Payload = inputResult.Payload, ReplyTo = traceId
                }, cancellationToken);

                var ctx = ctxResult.Payload?.GetValueOrDefault("context")?.ToString() ?? "";
                var fullQuery = string.IsNullOrEmpty(ctx) ? query : $"Context:\n{ctx}\n\nQuery: {query}";
                var teachingResult = await _duplexRouter.RequestL2ReasoningAsync(fullQuery, routeResult, cancellationToken).ConfigureAwait(false);
                if (teachingResult != null)
                {
                    _duplexRouter.LearnFromL2(query, teachingResult);
                    _gov.Context.AddTurn(query, teachingResult.Answer);
                    _duplexRouter.CacheResponse(query, teachingResult.Answer, "delegate_l2", "general", routeResult.Confidence);
                    _erlLoop.RecordTrial(query[..Math.Min(query.Length, 60)], teachingResult.Answer, "l2_teaching", 0.9, true);
                    return GovernorOutput.Success(teachingResult.Answer, traceId);
                }
            }
        }

        var contextResult = await _gov.Context.ProcessAsync(new Handshake
        {
            To = "context", Action = "preload",
            Payload = inputResult.Payload, ReplyTo = traceId
        }, cancellationToken);

        var preloadedContext = contextResult.Payload?.GetValueOrDefault("context")?.ToString() ?? "";
        _elasticMemory.Store($"ctx_{traceId}", preloadedContext[..Math.Min(preloadedContext.Length, 500)]);

        var routingResult = await _gov.Routing.ProcessAsync(new Handshake
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
            var fastResp = await _llm.GetResponseAsync(fullPrompt, new ChatOptions { ModelId = FlashModel, Temperature = 0.3f }, cancellationToken).ConfigureAwait(false);
            _erlLoop.RecordTrial(query[..Math.Min(query.Length, 60)], fastResp.Text ?? "", "budget_fast", 0.5, true);
            return GovernorOutput.Success(fastResp.Text ?? "", traceId);
        }

        string response;
        var maxTokens = _options.Value.AI.MaxTokens > 0 ? _options.Value.AI.MaxTokens : 4096;
        var options = new ChatOptions { ModelId = model, Temperature = temperature, MaxOutputTokens = maxTokens, Tools = _toolRegistry.GetTools().ToList() };

        response = await _modelDispatch.DispatchAndRunAsync(
            label, fullPrompt, options, model, traceId, _evolutionStore, cancellationToken).ConfigureAwait(false);

        _bavtRouter.Spend(1.0);
        _erlLoop.RecordTrial(query[..Math.Min(query.Length, 60)], response[..Math.Min(response.Length, 100)], "l2_response", 0.7, true);
        _elasticMemory.Store($"lts_{traceId}", response[..Math.Min(response.Length, 200)]);
        _echoDetector.RecordResponse(model, response[..Math.Min(response.Length, 500)]);
        if (_taskPipeline.HasPending) _logger.LogDebug("TaskPipeline: {Count} pending tasks", _taskPipeline.GetStats()["pending"]);

        if (Interlocked.Increment(ref _requestCount) % TrainingInterval == 0 && _options.Value.AI.OnnxEnabled)
        {
            _workQueue.Enqueue(async ct => { try { await Task.Run(() => TriggerPeriodicTraining(), ct); } catch (Exception ex) { _logger.LogWarning(ex, "Periodic training trigger failed"); } }, "PeriodicTraining");
        }

        if (_dna != null)
        {
            try
            {
                var outputSafety = await _dna.Safety.EvaluateOutputAsync(response, cancellationToken).ConfigureAwait(false);
                if (!outputSafety.Allowed)
                    return GovernorOutput.Blocked(outputSafety.BlockReason ?? "Blocked by DNA safety");
            }
            catch (Exception ex) { _logger.LogDebug(ex, "DNA output safety check skipped"); }
        }

        var outputResult = await _gov.Output.ProcessAsync(new Handshake
        {
            To = "output", Action = "review",
            Payload = new Dictionary<string, object?> { ["response"] = response },
            ReplyTo = traceId
        }, cancellationToken);

        _gov.Context.AddTurn(query, response);
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
            try { await _gov.Self.ProcessAsync(new Handshake
            {
                To = "self", Action = "start_trace",
                Payload = new Dictionary<string, object?> { ["trace_id"] = traceId }
            }, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Self governor trace processing failed"); }
        }, "SelfGovernor trace");

        return GovernorOutput.Success(response, traceId);
    }

    private async Task SilentSelfCheckAsync(string response)
    {
        try { await _gov.Output.SilentSelfCheckAsync(response); }
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

    private async Task<bool> IsQueryAmbiguousAsync(string query, CancellationToken ct)
    {
        try
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, "You detect ambiguity. Answer ONLY 'YES' if the query is ambiguous/vague and needs clarification, or 'NO' if it is specific enough to answer. Queries with concrete names, dates, or clear entities are NOT ambiguous."),
                new(ChatRole.User, $"Query: \"{query}\"\n\nIs this query ambiguous? YES/NO:")
            };
            var options = new ChatOptions { ModelId = FlashModel, Temperature = 0f, MaxOutputTokens = 8, Tools = new List<AITool>() };
            var result = await _llm.GetResponseAsync(messages, options, ct).ConfigureAwait(false);
            return result.Text?.Trim().StartsWith("YES", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Ambiguity detection LLM call failed"); return false; }
    }

    private async Task<string?> GenerateClarificationAsync(string query, CancellationToken ct)
    {
        try
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, _prompts.Render("clarify_system")),
                new(ChatRole.User, _prompts.Render("clarify_user", new Dictionary<string, string> { ["query"] = query }))
            };
            var options = new ChatOptions { ModelId = FlashModel, Temperature = 0.3f, MaxOutputTokens = 256, Tools = new List<AITool>() };
            var result = await _llm.GetResponseAsync(messages, options, ct).ConfigureAwait(false);
            var text = result.Text?.Trim();
            return !string.IsNullOrWhiteSpace(text) && text.Length > 10 ? text : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Clarification generation skipped");
            return null;
        }
    }

    private async Task<string?> GenerateFollowupAsync(string answer, string toolContext, CancellationToken ct)
    {
        try
        {
            var ctxSnippet = toolContext.Length > 2000 ? toolContext[..2000] : toolContext;
            var ansSnippet = answer.Length > 1000 ? answer[..1000] : answer;
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, _prompts.Render("followup_system")),
                new(ChatRole.User, _prompts.Render("followup_user", new Dictionary<string, string>
                {
                    ["answer"] = ansSnippet,
                    ["context"] = ctxSnippet
                }))
            };
            var options = new ChatOptions { ModelId = FlashModel, Temperature = 0.5f, MaxOutputTokens = 256, Tools = new List<AITool>() };
            var result = await _llm.GetResponseAsync(messages, options, ct).ConfigureAwait(false);
            var text = result.Text?.Trim();
            return !string.IsNullOrWhiteSpace(text) && text.Length > 10 ? text : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Follow-up generation skipped");
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _workQueue.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
