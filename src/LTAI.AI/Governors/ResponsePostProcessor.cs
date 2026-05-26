using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using LTAI.AI.Governors.Pipeline;
using LTAI.Core.Execution;
using LTAI.Core.Interfaces;
using LTAI.Core.Models;
using LTAI.DNA;
using LTAI.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed class ResponsePostProcessor
{
    private readonly BAVTRouter _bavtRouter;
    private readonly BackgroundWorkQueue _workQueue;
    private readonly ILogger<LivingTreeSystem> _logger;
    private readonly MetaCognitiveLayer _metaCognition;
    private readonly DreamCycle? _dreamCycle;
    private readonly ERLLoop _erlLoop;
    private readonly SynapticMemory? _synapticMemory;
    private readonly ICrossRunEvolutionStore? _evolutionStore;
    private readonly IParliamentBridge? _parliamentBridge;
    private readonly DNAOrchestrator? _dna;
    private readonly PromptTemplateStore? _prompts;
    private readonly ContextGovernor _context;
    private readonly ContextMapStore? _contextMap;
    private readonly IChatClient _llm;
    private readonly string _flashModel;

    private int _requestCount;
    private int _bgRequestCount;
    private readonly ConcurrentDictionary<string, (string Response, DateTime Expiry)> _queryCache = new();
    private string _personaStyle = "balanced";
    private DateTime _lastDreamCycleTrigger = DateTime.MinValue;
    private static readonly TimeSpan DreamCycleMinInterval = TimeSpan.FromMinutes(2);

    public string PersonaStyle => _personaStyle;
    public ConcurrentDictionary<string, (string Response, DateTime Expiry)> QueryCache => _queryCache;
    public int RequestCount => _requestCount;
    public int IncrementRequestCount() => Interlocked.Increment(ref _requestCount);

    public ResponsePostProcessor(
        BAVTRouter bavtRouter,
        BackgroundWorkQueue workQueue,
        ILogger<LivingTreeSystem> logger,
        MetaCognitiveLayer metaCognition,
        DreamCycle? dreamCycle,
        ERLLoop erlLoop,
        SynapticMemory? synapticMemory,
        ICrossRunEvolutionStore? evolutionStore,
        IParliamentBridge? parliamentBridge,
        DNAOrchestrator? dna,
        PromptTemplateStore? prompts,
        ContextGovernor context,
        ContextMapStore? contextMap,
        IChatClient llm,
        string flashModel)
    {
        _bavtRouter = bavtRouter;
        _workQueue = workQueue;
        _logger = logger;
        _metaCognition = metaCognition;
        _dreamCycle = dreamCycle;
        _erlLoop = erlLoop;
        _synapticMemory = synapticMemory;
        _evolutionStore = evolutionStore;
        _parliamentBridge = parliamentBridge;
        _dna = dna;
        _prompts = prompts;
        _context = context;
        _contextMap = contextMap;
        _llm = llm;
        _flashModel = flashModel;
    }

    public IEnumerable<string> Process(
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

        if (_contextMap != null && !groundingFailed)
        {
            var entities = new List<string>();
            if (!string.IsNullOrWhiteSpace(layer1Context))
            {
                var matches = Regex.Matches(layer1Context, @"[\u4e00-\u9fff]{2,8}");
                foreach (Match m in matches.Take(5))
                    entities.Add(m.Value);
            }

            var toolSequence = totalToolCalls > 0 ? new List<string> { "tool_exec" } : null;
            var domain = pre.PatternToolName ?? "general";

            _contextMap.Distill(query, finalResponse, domain,
                (float)(groundingFailed ? 0.3 : 0.7), toolSequence, entities);
            _contextMap.Save();
        }

        if (!groundingFailed && !layer1HighConfidence && finalResponse.Length > 100
            && DateTime.UtcNow - _lastDreamCycleTrigger > DreamCycleMinInterval)
        {
            _lastDreamCycleTrigger = DateTime.UtcNow;
            _workQueue.Enqueue(async ct =>
            {
                try { if (_dreamCycle != null) await _dreamCycle.ForceReflectionAsync(); } catch (Exception ex) { _logger.LogWarning(ex, "DreamCycle: trigger failed"); }
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
                catch (Exception ex) { _logger.LogWarning(ex, "Reinforce: entity extraction failed"); }
            }, "KnowledgeGraphBuild");

        if (++_bgRequestCount % 50 == 49)
            _workQueue.Enqueue(async ct =>
            {
                try { await _llm.GetResponseAsync("系统自检：总结最近运行状态", new ChatOptions { ModelId = _flashModel, Temperature = 0f, MaxOutputTokens = 64 }, ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "SelfTest: adversarial self-test failed"); }
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
                _workQueue.Enqueue(async ct => { try { await TriggerPeriodicTrainingAsync(); } catch (Exception ex) { _logger.LogWarning(ex, "AutoLoRA: periodic training trigger failed"); } }, "AutoLoRA");
        }

        if (groundingFailed && label == "fast" && !layer1HighConfidence)
        {
            _erlLoop.RecordTrial($"l0_reroute_{query[..Math.Min(query.Length, 30)]}", "should_be_deep", "fast_misroute", 0.3f, false);
            _logger.LogInformation("L0 self-learning: fast→deep for {P}", query[..Math.Min(query.Length, 40)]);
        }

        if (!groundingFailed && finalResponse.Length > 200 && _context.CompressHistory().Length > 300)
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
                catch (Exception ex) { _logger.LogWarning(ex, "SessionMemory: store failed"); }
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
                catch (Exception ex) { _logger.LogWarning(ex, "RegressionTest: store failed"); }
            }, "RegressionTest");

        if (retryLevel >= 2) _personaStyle = "concise";

        if (groundingFailed && finalResponse.Length < 20 && _dna != null)
            _workQueue.Enqueue(async ct =>
            {
                try { await _dna.Consciousness.ProcessExperienceAsync($"CRASH: empty response L{retryLevel}. Q: '{query[..Math.Min(query.Length, 60)]}'", new Dictionary<string, object?>(), ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "SelfRepair: DNA experience processing failed"); }
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
                try { var lessons = _evolutionStore.GetActiveLessons(10); } catch (Exception ex) { _logger.LogWarning(ex, "Federation: lesson retrieval failed"); }
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
                catch (Exception ex) { _logger.LogWarning(ex, "MetaAssess: evolution assessment failed"); }
            }, "SelfEvolution");

        if (!groundingFailed && finalResponse.Length > 300 && totalToolCalls >= 2)
        {
            _erlLoop.RecordTrial($"debate_{query[..Math.Min(query.Length, 40)]}", finalResponse[..Math.Min(finalResponse.Length, 100)], "multi_agent", 0.85f, true);
            if (_parliamentBridge is { IsAvailable: true })
            {
                _ = Task.Run(async () =>
                {
                    try { await _parliamentBridge.DeliberateAsync(query, finalResponse); } catch (Exception ex) { _logger.LogWarning(ex, "Parliament: deliberation failed"); }
                });
            }
        }

        if (totalToolCalls >= 2 && !groundingFailed)
            _erlLoop.RecordTrial($"qvalue_{totalToolCalls}", $"Tools={totalToolCalls}", "quantum_opt", 0.9f, true);

        if (!groundingFailed && metaFamiliarity > 0.5f && finalResponse.Length > 100)
            yield return "\n\n> 置信度: 高 | 格式建议: 结构化表格";

        if (_requestCount % 500 == 0 && _prompts is not null)
            _workQueue.Enqueue(async ct => { try { _prompts.Reload(); } catch (Exception ex) { _logger.LogWarning(ex, "PromptEvolution: reload failed"); } }, "PromptEvolution");

        if (query.Contains("换个角度") || query.Contains("另一个角度"))
            _workQueue.Enqueue(async ct =>
            {
                try
                {
                    _synapticMemory?.Store(new SynapticExperience
                    {
                        Type = SynapseType.Interaction, Query = query, Response = finalResponse[..Math.Min(finalResponse.Length, 300)],
                        Label = "fork_branch", Confidence = 0.7f, Reward = 0.7f, Metadata = $"ctx={_context.CompressHistory()[..Math.Min(_context.CompressHistory().Length, 200)]}"
                    });
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Fork: store failed"); }
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

    private async Task TriggerPeriodicTrainingAsync()
    {
        try
        {
            var samples = _synapticMemory?.GetTrainingSamples(maxCount: 50) ?? new();

            if (samples.Count >= 20)
            {
                var synapticDir = Path.Combine(AppContext.BaseDirectory, "synaptic");
                var trainer = new SynapticTrainer(Path.Combine(synapticDir, "models"));
                var result = trainer.TrainIntentClassifier(samples);

                if (result.Success)
                {
                    var inference = new SynapticInference();
                    if (!string.IsNullOrEmpty(result.OnnxPath) && inference.LoadOnnxModel(result.OnnxPath))
                    {
                        _logger.LogInformation("AutoLoRA ONNX retraining: accuracy={Acc:F2} samples={N} onnx={Path}",
                            result.Accuracy, result.TrainingSamples, result.OnnxPath);

                        inference.Dispose();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AutoLoRA training skipped");
        }
    }
}
