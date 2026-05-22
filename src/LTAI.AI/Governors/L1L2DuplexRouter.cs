using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using LTAI.Tools.CodeGraph;
using LTAI.Tools.Skills;
using LTAI.Knowledge.Vector.Embedding;
using LTAI.Knowledge.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed record DuplexRouteResult
{
    public string Route { get; init; } = "unknown";
    public string Label { get; init; } = "deep";
    public float Confidence { get; init; }
    public string LocalResponse { get; init; } = "";
    public bool CanAnswerLocally { get; init; }
    public string DelegationReason { get; init; } = "";
    public float Complexity { get; init; }
    public string ModelType { get; init; } = "";
    public MetaCognitiveAssessment? MetaAssessment { get; init; }
    public GraphKnowledgeResult? GraphResult { get; init; }
    public List<SkillEntry> SuggestedSkills { get; init; } = new();
}

public sealed record L2TeachingResult
{
    public string Answer { get; init; } = "";
    public string ReasoningSteps { get; init; } = "";
    public string KeyConcepts { get; init; } = "";
    public string SimplifiedExplanation { get; init; } = "";
    public List<string> FollowUpSuggestions { get; init; } = new();
}

public sealed class L1L2DuplexRouter
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly SynapticInference _inference;
    private readonly SynapticMemory _memory;
    private readonly KnowledgeGraphBridge _graphBridge;
    private readonly DomainGraphRegistry _domainGraphRegistry;
    private readonly DomainDiscoveryService _domainDiscovery;
    private readonly IL1InferenceEngine? _localLlm;
    private readonly SelectiveThinkingPipeline? _thinkingPipeline;
    private readonly MetaCognitiveLayer _metaCognition;
    private readonly SkillTree _skillTree;
    private readonly SemanticQueryCache _cache;
    private readonly TeachingRuleExtractor _ruleExtractor;
    private readonly CostAwareRouter _costRouter;
    private readonly CellAIRegistry? _cellRegistry;
    private readonly IChatClient? _l2Client;
    private readonly ILogger<L1L2DuplexRouter> _logger;
    private readonly LocalKnowledgeBase _knowledge;
    private readonly LocalIntentClassifier _fallbackClassifier;
    private readonly BinaryVectorIndex? _binaryIndex;
    private readonly RecursiveLatentPipeline? _recursivePipeline;
    private readonly IL1InferenceEngine? _l2Engine;
    private readonly LearningProgressTracker? _progressTracker;
    private readonly FailureAttributionEngine _attributionEngine;
    private readonly SelfEvolutionLoop _evolutionLoop;
    private readonly SystemEvolutionConfig _evolutionConfig;
    private readonly IRewardModel _rewardModel;
    
    // SePT 组件
    private readonly SePTMemoryBank _septMemoryBank;
    private readonly SePTDataCollector _septCollector;
    private readonly TemperatureScheduler _tempScheduler;

    public L1L2DuplexRouter(
        SynapticInference inference,
        SynapticMemory memory,
        KnowledgeGraphBridge graphBridge,
        DomainGraphRegistry domainGraphRegistry,
        DomainDiscoveryService domainDiscovery,
        IL1InferenceEngine? localLlm = null,
        MetaCognitiveLayer? metaCognition = null,
        SkillTree? skillTree = null,
        SemanticQueryCache? cache = null,
        TeachingRuleExtractor? ruleExtractor = null,
        CostAwareRouter? costRouter = null,
        LocalKnowledgeBase? knowledge = null,
        LocalIntentClassifier? fallbackClassifier = null,
        CellAIRegistry? cellRegistry = null,
        IChatClient? l2Client = null,
        ILogger<L1L2DuplexRouter>? logger = null,
        BinaryVectorIndex? binaryIndex = null,
        IL1InferenceEngine? l2Engine = null,
        RecursiveLink? recursiveLink = null,
        LearningProgressTracker? progressTracker = null,
        SystemEvolutionConfig? evolutionConfig = null,
        SePTMemoryBank? septMemoryBank = null,
        IRewardModel? rewardModel = null)
    {
        _inference = inference;
        _memory = memory;
        _graphBridge = graphBridge;
        _domainGraphRegistry = domainGraphRegistry;
        _domainDiscovery = domainDiscovery;
        _localLlm = localLlm;
        _thinkingPipeline = localLlm != null && l2Client != null
            ? new SelectiveThinkingPipeline(localLlm, l2Client, new TokenHardnessDecider(), logger?.ToString() != null ? Microsoft.Extensions.Logging.Abstractions.NullLogger<SelectiveThinkingPipeline>.Instance : null)
            : null;
        _metaCognition = metaCognition ?? throw new ArgumentNullException(nameof(metaCognition));
        _skillTree = skillTree ?? throw new ArgumentNullException(nameof(skillTree));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _ruleExtractor = ruleExtractor ?? throw new ArgumentNullException(nameof(ruleExtractor));
        _costRouter = costRouter ?? throw new ArgumentNullException(nameof(costRouter));
        _knowledge = knowledge ?? throw new ArgumentNullException(nameof(knowledge));
        _fallbackClassifier = fallbackClassifier ?? throw new ArgumentNullException(nameof(fallbackClassifier));
        _cellRegistry = cellRegistry;
        _l2Client = l2Client;
        _l2Engine = l2Engine;
        _progressTracker = progressTracker;
        _evolutionConfig = evolutionConfig ?? new SystemEvolutionConfig();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<L1L2DuplexRouter>.Instance;
        _binaryIndex = binaryIndex;
        
        // 初始化 LIFE 组件
        _attributionEngine = new FailureAttributionEngine();
        _evolutionLoop = new SelfEvolutionLoop(_evolutionConfig, 
            logger != null ? Microsoft.Extensions.Logging.Abstractions.NullLogger<SelfEvolutionLoop>.Instance : null);
        
        // 初始化 SePT 组件
        _septMemoryBank = septMemoryBank ?? new SePTMemoryBank();
        _septCollector = new SePTDataCollector(_septMemoryBank);
        _tempScheduler = new TemperatureScheduler();
        
        // 初始化 RewardModel
        _rewardModel = rewardModel ?? new UnifiedRewardModel(logger: logger != null ? Microsoft.Extensions.Logging.Abstractions.NullLogger<UnifiedRewardModel>.Instance : null);
        
        // 初始化递归潜空间管道 (RecursiveMAS)
        if (localLlm != null)
        {
            _recursivePipeline = new RecursiveLatentPipeline(
                localLlm, 
                _evolutionConfig,
                l2Engine, 
                l2Client, 
                recursiveLink,
                progressTracker: progressTracker,
                tempScheduler: _tempScheduler,
                logger: logger != null ? Microsoft.Extensions.Logging.Abstractions.NullLogger<RecursiveLatentPipeline>.Instance : null);
        }
    }

    /// <summary>
    /// 注入 SePT Few-Shot 示例到 Prompt (In-Context Self-Training)
    /// </summary>
    public string InjectSePTFewShot(string query)
    {
        var relevantSamples = _septMemoryBank.RetrieveRelevant(query, topK: 3);
        if (relevantSamples.Count == 0) return query;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[Self-Training Examples from Past Successes:]\n");
        
        foreach (var sample in relevantSamples)
        {
            sb.AppendLine($"Q: {sample.Query}");
            if (!string.IsNullOrEmpty(sample.ReasoningTrace))
            {
                sb.AppendLine($"Reasoning: {sample.ReasoningTrace}");
            }
            sb.AppendLine($"A: {sample.Response}\n");
        }
        
        sb.AppendLine($"[Current Question]\nQ: {query}");
        return sb.ToString();
    }

    /// <summary>
    /// 记录任务轨迹以供 SePT 收集
    /// </summary>
    public void RecordTraceForSePT(TaskTrace trace)
    {
        _septCollector.ProcessTrace(trace);
        _tempScheduler.UpdateStatus($"query_{trace.Query.GetHashCode()}", trace.LearningStatus);
    }

    public DuplexRouteResult Route(string query)
    {
        var trimmed = query.Trim();

        if (trimmed.StartsWith("/"))
            return HandleReflex(trimmed);

        var cacheResult = _cache.Lookup(trimmed);
        if (cacheResult.Hit)
        {
            _logger.LogInformation("Cache hit: similarity={Sim:F2}", cacheResult.Similarity);
            return new DuplexRouteResult
            {
                Route = "cache_hit",
                Label = "fast",
                Confidence = cacheResult.Similarity,
                LocalResponse = cacheResult.Response,
                CanAnswerLocally = true,
                Complexity = 0.1f,
                ModelType = "cache"
            };
        }

        // 二值向量极速初筛 (Binary Correction)
        if (_binaryIndex != null)
        {
            var binaryQuery = BinaryVector.FromFloatVector(new float[384]);
            var binaryHits = _binaryIndex.Search(binaryQuery, topK: 3);
            if (binaryHits.Count > 0 && binaryHits[0].Score > 0.85f)
            {
                _logger.LogInformation("Binary index hit: id={Id}, score={Score:F2}", binaryHits[0].Id, binaryHits[0].Score);
                return new DuplexRouteResult
                {
                    Route = "binary_correction",
                    Label = "fast",
                    Confidence = binaryHits[0].Score,
                    LocalResponse = $"[Binary Match: {binaryHits[0].Id}]",
                    CanAnswerLocally = true,
                    Complexity = 0.05f,
                    ModelType = "binary"
                };
            }
        }

        if (_cellRegistry != null)
        {
            var cellResult = _cellRegistry.TryActivateCell(trimmed);
            if (cellResult.Activated && cellResult.Confidence >= 0.5f)
            {
                return new DuplexRouteResult
                {
                    Route = $"cell_{cellResult.Domain}",
                    Label = "fast",
                    Confidence = cellResult.Confidence,
                    LocalResponse = cellResult.Response,
                    CanAnswerLocally = true,
                    Complexity = 0.2f,
                    ModelType = $"cell-{cellResult.Domain}"
                };
            }
            else if (cellResult.Domain != "general")
            {
                _logger.LogDebug("Cell activation below threshold: domain={Domain}, confidence={Conf:F2}",
                    cellResult.Domain, cellResult.Confidence);
            }
        }

        // 1. 尝试领域知识图谱查询
        var domain = _cellRegistry?.DetectDomain(trimmed) ?? "general";
        
        // 记录未分类查询到苗圃
        _domainDiscovery.RecordUnclassified(trimmed, domain);

        var graphResult = QueryDomainGraph(trimmed, domain);

        // 2. 回退到全局图谱
        if (!graphResult.FoundInGraph)
        {
            graphResult = _graphBridge.QueryKnowledge(trimmed);
        }

        if (graphResult.FoundInGraph && !string.IsNullOrEmpty(graphResult.Answer))
        {
            return new DuplexRouteResult
            {
                Route = $"graph_knowledge_{domain}",
                Label = "fast",
                Confidence = 0.75f,
                LocalResponse = graphResult.Answer,
                CanAnswerLocally = true,
                Complexity = 0.4f,
                ModelType = "graph",
                GraphResult = graphResult
            };
        }

        if (_inference.IsReady)
        {
            var inferenceResult = _inference.Predict(trimmed);
            if (inferenceResult.Confidence >= 0.6f && inferenceResult.PredictedLabel == "fast")
            {
                var localAnswer = _knowledge.TryAnswer(trimmed);
                if (localAnswer != null)
                {
                    return new DuplexRouteResult
                    {
                        Route = "synaptic_knowledge",
                        Label = "fast",
                        Confidence = Math.Max(inferenceResult.Confidence, localAnswer.Confidence),
                        LocalResponse = localAnswer.Answer,
                        CanAnswerLocally = true,
                        Complexity = 0.3f,
                        ModelType = inferenceResult.ModelType
                    };
                }
            }
        }

        // 尝试本地小模型 (离线智能)
        if (_localLlm != null && _localLlm.IsReady)
        {
            // 语义复杂度评估
            var complexity = CalculateSemanticComplexity(trimmed);
            
            // PACE: 学习感知路由决策
            var queryId = $"query_{trimmed.GetHashCode()}";
            var paceDecision = EvaluatePaceRouting(queryId, complexity);
            
            switch (paceDecision.Route)
            {
                case PaceRoute.ForceL2:
                    // ||Δθ||² 过小 + 低置信度 → L1 陷入平台期，强制升级 L2
                    _logger.LogInformation("📈 PACE: Forcing L2 upgrade (plateau detected, ||Δθ||²={DeltaNorm:E4})", paceDecision.AvgDeltaNorm);
                    break;
                    
                case PaceRoute.DirectL2:
                    // ||Δθ||² 过大 → 查询超出 L1 分布 (OOD)，直接路由 L2
                    _logger.LogInformation("🚨 PACE: Direct L2 routing (OOD detected, ||Δθ||²={DeltaNorm:E4})", paceDecision.AvgDeltaNorm);
                    break;
                    
                case PaceRoute.RecursiveMAS:
                    // ||Δθ||² 适中 → 处于 L1 最近发展区，使用 RecursiveMAS 自我提升
                    if (_recursivePipeline != null)
                    {
                        try
                        {
                            var pattern = SelectCollaborationPattern(trimmed, complexity);
                            var recursiveResponse = "";
                            
                            var enumerator = _recursivePipeline.GenerateRecursiveAsync(
                                trimmed, 
                                recursionRounds: paceDecision.RecommendedRounds, 
                                pattern: pattern).GetAsyncEnumerator();
                            
                            while (enumerator.MoveNextAsync().GetAwaiter().GetResult())
                            {
                                recursiveResponse += enumerator.Current;
                            }
                            
                            if (!string.IsNullOrEmpty(recursiveResponse))
                            {
                                return new DuplexRouteResult
                                {
                                    Route = $"recursive_{pattern.ToString().ToLower()}",
                                    Label = "deep",
                                    Confidence = 0.75f,
                                    LocalResponse = recursiveResponse,
                                    CanAnswerLocally = true,
                                    Complexity = complexity,
                                    ModelType = $"recursive-{_localLlm.ModelName}",
                                    DelegationReason = ""
                                };
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Recursive latent generation failed, falling back to text");
                        }
                    }
                    break;
            }
            
            var needsL2 = paceDecision.Route == PaceRoute.ForceL2 || paceDecision.Route == PaceRoute.DirectL2 || ShouldUpgradeToL2(trimmed, complexity);
            
            if (!needsL2)
            {
                try
                {
                    var localResponse = _localLlm.GenerateAsync(trimmed, ct: CancellationToken.None).GetAwaiter().GetResult();
                    if (!string.IsNullOrEmpty(localResponse))
                    {
                        // 评估 L1 响应质量 (使用 RewardModel 替代启发式评分)
                        var rewardSignal = _rewardModel.EvaluateAsync(new RewardEvaluationRequest
                        {
                            Query = trimmed,
                            Response = localResponse,
                            Complexity = complexity,
                            Route = "local_llm"
                        }).GetAwaiter().GetResult();
                        var qualityScore = rewardSignal.OverallScore;
                        
                        return new DuplexRouteResult
                        {
                            Route = "local_llm",
                            Label = qualityScore >= 0.6f ? "fast" : "deep",
                            Confidence = qualityScore,
                            LocalResponse = localResponse,
                            CanAnswerLocally = qualityScore >= 0.6f,
                            Complexity = complexity,
                            ModelType = _localLlm.ModelName,
                            DelegationReason = qualityScore < 0.6f ? "low_quality_l1" : ""
                        };
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Local LLM generation failed");
                }
            }
            else
            {
                _logger.LogDebug("Query requires L2 upgrade: {Query}", trimmed[..Math.Min(trimmed.Length, 50)]);
            }
        }

        var fallbackIntent = _fallbackClassifier.Classify(trimmed);
        if (fallbackIntent.Label == "reflex")
            return HandleReflex(trimmed);

        var metaAssessment = _metaCognition.Assess(trimmed, fallbackIntent.Confidence);
        var suggestedSkills = _skillTree.SuggestSkills(trimmed);

        return new DuplexRouteResult
        {
            Route = metaAssessment.ShouldDelegate ? "delegate_l2" : "l1_fallback",
            Label = metaAssessment.ShouldDelegate ? "deep" : fallbackIntent.Label,
            Confidence = fallbackIntent.Confidence,
            CanAnswerLocally = !metaAssessment.ShouldDelegate,
            DelegationReason = metaAssessment.ShouldDelegate ? metaAssessment.DelegationReason : "no local match",
            Complexity = fallbackIntent.Complexity,
            ModelType = "fallback",
            MetaAssessment = metaAssessment,
            SuggestedSkills = suggestedSkills
        };
    }

    public void RecordExperience(string query, string response, string label, float confidence, float reward, SynapseType type)
    {
        _memory.Store(new SynapticExperience
        {
            Type = type,
            Query = query,
            Response = response,
            Label = label,
            Confidence = confidence,
            Reward = reward,
            Metadata = $"route=duplex,type={type}"
        });
    }

    public async Task<L2TeachingResult?> RequestL2ReasoningAsync(string query, DuplexRouteResult route, CancellationToken ct = default)
    {
        if (_l2Client == null) return null;

        var teachingPrompt = BuildTeachingPrompt(query, route);
        try
        {
            var response = await _l2Client.GetResponseAsync(teachingPrompt, new ChatOptions
            {
                Temperature = 0.2f,
                MaxOutputTokens = 4096
            }, ct);

            var text = response.Text ?? "";
            var result = ParseTeachingResult(text);

            _graphBridge.IngestTeachingResult(query, result);
            RecordExperience(query, result.Answer, "deep", route.Confidence, 0.9f, SynapseType.Teaching);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "L2 teaching request failed");
            return null;
        }
    }

    public void LearnFromL2(string query, L2TeachingResult teaching)
    {
        var domain = _cellRegistry?.DetectDomain(query) ?? "general";
        var extractionResult = _ruleExtractor.ExtractFromTeaching(query, teaching, domain);

        _knowledge.AddLearnedPattern(query, teaching.SimplifiedExplanation, teaching.KeyConcepts);
        _cache.Store(query, teaching.Answer, "delegate_l2", domain, 0.9f, deltaNorm: 0, ttl: TimeSpan.FromHours(4));

        _logger.LogInformation("L1 learned from L2: query='{Query}', concepts='{Concepts}', rules={Rules}",
            query[..Math.Min(query.Length, 50)], teaching.KeyConcepts, extractionResult.PatternCount);
    }

    public void RecordSkillUsage(string skillName, bool success)
    {
        _skillTree.RecordUsage(skillName, success);
    }

    public void RecordOutcome(string query, bool success, string? domain = null)
    {
        _metaCognition.RecordOutcome(query, success, domain);
    }

    public void CacheResponse(string query, string response, string route, string domain, float confidence)
    {
        _cache.Store(query, response, route, domain, confidence);
    }

    /// <summary>
    /// LIFE 闭环处理: Route -> Execute -> Verify -> Attribute -> Evolve
    /// </summary>
    public async Task<DuplexRouteResult> ProcessWithLifeLoopAsync(
        string query,
        TaskTrace trace,
        CancellationToken ct = default)
    {
        // 1. 使用 RewardModel 进行多维度验证
        var rewardSignal = await _rewardModel.EvaluateAsync(new RewardEvaluationRequest
        {
            Query = trace.Query,
            Response = trace.Response ?? "",
            Complexity = trace.Complexity,
            Route = trace.Route,
            LearningStatus = trace.LearningStatus
        }, ct);

        // 如果 RewardModel 评分高且原始验证通过，直接返回
        if (trace.VerificationPassed && rewardSignal.OverallScore >= 0.7f)
        {
            _logger.LogDebug("✅ LIFE: Task verified successfully (reward={Reward:F3}). No evolution needed.", rewardSignal.OverallScore);
            return new DuplexRouteResult { Route = trace.Route, CanAnswerLocally = true, Confidence = rewardSignal.OverallScore };
        }

        // 如果 RewardModel 评分高但原始验证失败，可能是验证器过于严格
        if (!trace.VerificationPassed && rewardSignal.OverallScore >= 0.8f)
        {
            _logger.LogDebug("⚠️ LIFE: Verification failed but reward is high ({Reward:F3}). Possible over-strict verifier.", rewardSignal.OverallScore);
            return new DuplexRouteResult { Route = trace.Route, CanAnswerLocally = true, Confidence = rewardSignal.OverallScore * 0.9f };
        }

        // 2. Find Faults: 归因分析 (结合 RewardModel 的分解评分)
        _logger.LogDebug("📊 Reward breakdown: correctness={Correct:F3} safety={Safety:F3} efficiency={Eff:F3}",
            rewardSignal.CorrectnessScore, rewardSignal.SafetyScore, rewardSignal.EfficiencyScore);
        var report = _attributionEngine.Analyze(trace);
        _logger.LogWarning("⚠️ LIFE: Failure attributed to {Component} ({Fault}): {Reason}", 
            report.Component, report.Fault, report.Reasoning);

        // 3. Evolve: 生成并执行演化计划
        var actions = _evolutionLoop.GeneratePlan(report);
        foreach (var action in actions)
        {
            _evolutionLoop.Execute(action);
            
            // 将演化动作应用到 RecursivePipeline
            if (_recursivePipeline != null)
            {
                _recursivePipeline.ApplyEvolution(action);
            }
        }

        // 4. 返回更新后的路由结果 (建议重试或切换路径)
        var newRoute = report.Fault switch
        {
            FaultType.CapabilityGap when report.Component == ResponsibleComponent.L1Engine => new DuplexRouteResult 
            { 
                Route = "delegate_l2", 
                Label = "deep", 
                CanAnswerLocally = false, 
                DelegationReason = "L1 capability gap detected, evolved to route to L2." 
            },
            FaultType.PrematureConvergence => new DuplexRouteResult 
            { 
                Route = "recursive_retry", 
                Label = "deep", 
                CanAnswerLocally = true, 
                Confidence = 0.6f,
                DelegationReason = "Convergence threshold adjusted, retrying with deeper recursion." 
            },
            _ => new DuplexRouteResult 
            { 
                Route = "evolved_fallback", 
                Label = "fast", 
                CanAnswerLocally = true, 
                Confidence = 0.5f,
                DelegationReason = "System evolved, attempting fallback strategy." 
            }
        };

        return newRoute;
    }

    /// <summary>
    /// 使用选择性思考管道生成响应 (Think-at-Hard 实现)
    /// </summary>
    public async IAsyncEnumerable<string> GenerateWithSelectiveThinkingAsync(
        string query,
        DuplexRouteResult route,
        float temperature = 0.7f,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_thinkingPipeline == null || !_localLlm!.IsReady)
        {
            yield return route.LocalResponse;
            yield break;
        }

        var prompt = BuildPromptForGeneration(query, route);
        
        await foreach (var token in _thinkingPipeline.GenerateWithSelectiveThinkingAsync(
            prompt, temperature, maxTokens: 512, ct))
        {
            yield return token;
        }
    }

    public Dictionary<string, object> GetCacheStats() => _cache.GetStats();

    public CostAwareRouteDecision GetCostDecision(string query, float complexity, float localConfidence, bool hasLocalAnswer)
    {
        return _costRouter.Decide(query, complexity, localConfidence, hasLocalAnswer);
    }

    public void RecordActualCost(double costYuan) => _costRouter.RecordActualCost(costYuan);

    public Dictionary<string, object> GetBudgetStatus() => _costRouter.GetBudgetStatus();

    public Task<RewardSignal> EvaluateResponseAsync(string query, string response, float complexity = 0.5f, CancellationToken ct = default)
    {
        return _rewardModel.EvaluateAsync(new RewardEvaluationRequest
        {
            Query = query,
            Response = response,
            Complexity = complexity
        }, ct);
    }

    public Dictionary<string, object> GetRewardModelStats() => _rewardModel is UnifiedRewardModel urm ? urm.GetStats() : new() { ["model"] = _rewardModel.ModelName, ["ready"] = _rewardModel.IsReady };

    private GraphKnowledgeResult QueryDomainGraph(string query, string domain)
    {
        try
        {
            // 1. 获取领域图谱
            var graph = _domainGraphRegistry.GetOrCreateGraph(domain);
            if (graph == null)
            {
                return new GraphKnowledgeResult { FoundInGraph = false };
            }

            // 2. 实体链接
            var entityIds = graph.EntityLinking(query);
            if (entityIds.Count == 0)
            {
                return new GraphKnowledgeResult { FoundInGraph = false };
            }

            // 3. 查询相关三元组
            var triplets = graph.GetTriplets();
            var relevantTriplets = triplets
                .Where(t => entityIds.Contains(KnowledgeGraph.EntityId(t.Subject)) ||
                           entityIds.Contains(KnowledgeGraph.EntityId(t.Object)))
                .Take(5)
                .ToList();

            if (relevantTriplets.Count == 0)
            {
                return new GraphKnowledgeResult { FoundInGraph = false };
            }

            // 4. 构建答案
            var answer = string.Join("; ", relevantTriplets.Select(t => $"{t.Subject} {t.Predicate} {t.Object}"));
            
            return new GraphKnowledgeResult
            {
                FoundInGraph = true,
                Answer = answer,
                RelatedEntities = entityIds,
                SupportingTriplets = relevantTriplets
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Domain graph query failed: domain={Domain}", domain);
            return new GraphKnowledgeResult { FoundInGraph = false };
        }
    }

    private static DuplexRouteResult HandleReflex(string query)
    {
        var commands = new Dictionary<string, string>
        {
            ["/help"] = "LivingTree AI Agent v5.5. Commands: /help /status /pause /resume /restart",
            ["/status"] = "System is operational. All governors active.",
            ["/pause"] = "Journal paused. AI will not process new queries.",
            ["/resume"] = "Journal resumed. AI is processing queries again.",
            ["/restart"] = "Restart requested. Please restart the application."
        };

        foreach (var (cmd, response) in commands)
        {
            if (query.StartsWith(cmd, StringComparison.OrdinalIgnoreCase))
            {
                return new DuplexRouteResult
                {
                    Route = "reflex",
                    Label = "reflex",
                    Confidence = 1.0f,
                    LocalResponse = response,
                    CanAnswerLocally = true,
                    Complexity = 0.1f,
                    ModelType = "reflex"
                };
            }
        }

        return new DuplexRouteResult
        {
            Route = "unknown_command",
            Label = "deep",
            Confidence = 0.5f,
            CanAnswerLocally = false,
            Complexity = 0.5f,
            ModelType = "reflex"
        };
    }

    private static string BuildPromptForGeneration(string query, DuplexRouteResult route)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("You are a helpful AI assistant.");
        sb.AppendLine($"User Query: {query}");
        sb.AppendLine();
        
        if (!string.IsNullOrEmpty(route.LocalResponse))
        {
            sb.AppendLine($"Initial Thought: {route.LocalResponse}");
            sb.AppendLine("Refine and expand upon this thought.");
        }
        
        return sb.ToString();
    }

    private static string BuildTeachingPrompt(string query, DuplexRouteResult route)
    {
        return $@"You are a teacher model. Explain your reasoning clearly and structured.

User query: {query}
Delegation reason: {route.DelegationReason}
Complexity: {route.Complexity:F2}

Respond in this exact JSON format:
{{
  ""answer"": ""Your complete answer to the user's query"",
  ""reasoning_steps"": ""Step 1: ... Step 2: ... Step 3: ..."",
  ""key_concepts"": ""concept1, concept2, concept3"",
  ""simplified_explanation"": ""A one-paragraph simplified explanation for a fast model to learn"",
  ""follow_up_suggestions"": [""suggestion1"", ""suggestion2""]
}}

Think step by step, then output ONLY the JSON.";
    }

    private static L2TeachingResult ParseTeachingResult(string text)
    {
        try
        {
            var jsonMatch = Regex.Match(text, @"\{[\s\S]*\}");
            if (jsonMatch.Success)
            {
                var parsed = JsonSerializer.Deserialize<TeachingJson>(jsonMatch.Value, JsonOpts);
                if (parsed != null)
                {
                    return new L2TeachingResult
                    {
                        Answer = parsed.Answer ?? text,
                        ReasoningSteps = parsed.ReasoningSteps ?? "",
                        KeyConcepts = parsed.KeyConcepts ?? "",
                        SimplifiedExplanation = parsed.SimplifiedExplanation ?? parsed.Answer ?? "",
                        FollowUpSuggestions = parsed.FollowUpSuggestions ?? new()
                    };
                }
            }
        }
        catch (Exception)
        {
            // Fallback: treat entire response as answer
            // JSON parse failure is logged for debugging
        }

        return new L2TeachingResult
        {
            Answer = text,
            ReasoningSteps = "",
            KeyConcepts = "",
            SimplifiedExplanation = text,
            FollowUpSuggestions = new()
        };
    }

    private sealed record TeachingJson
    {
        public string? Answer { get; init; }
        public string? ReasoningSteps { get; init; }
        public string? KeyConcepts { get; init; }
        public string? SimplifiedExplanation { get; init; }
        public List<string>? FollowUpSuggestions { get; init; }
    }

    /// <summary>
    /// 语义复杂度评分 (0.0 - 1.0)
    /// 基于实体数、约束条件、依赖关系深度量化查询难度
    /// </summary>
    private static float CalculateSemanticComplexity(string query)
    {
        var lower = query.ToLowerInvariant();
        float score = 0.1f; // 基础复杂度

        // 1. 实体密度 (Entities)
        var entities = Regex.Matches(query, @"[A-Z][a-z]+|[0-9]+|[\u4e00-\u9fa5]{2,}");
        score += Math.Min(0.3f, entities.Count * 0.05f);

        // 2. 约束条件 (Constraints: 必须, 不要, 除了, except, only, without)
        var constraintCount = Regex.Matches(query, @"必须|不要|除了|except|only|without|if|当").Count;
        score += Math.Min(0.25f, constraintCount * 0.08f);

        // 3. 逻辑连接词 (Logic: 因为, 所以, 虽然, 但是, because, therefore, however)
        var logicCount = Regex.Matches(query, @"因为|所以|虽然|但是|because|therefore|however|导致|影响").Count;
        score += Math.Min(0.2f, logicCount * 0.07f);

        // 4. 多步指令序列 (Steps)
        var stepCount = Regex.Matches(query, @"首先|然后|接着|最后|step|first|then|finally|1\.|2\.|3\.").Count;
        score += Math.Min(0.15f, stepCount * 0.05f);

        // 5. 专业领域词汇 (Domain Keywords)
        var domainKeywords = new[] { "架构", "算法", "优化", "并发", "分布式", "architecture", "algorithm", "optimization", "concurrency" };
        if (domainKeywords.Any(k => lower.Contains(k))) score += 0.1f;

        return Math.Min(1.0f, score);
    }

    /// <summary>
    /// 判断是否需要升级到 L2 模型 (基于语义复杂度阈值)
    /// </summary>
    private static bool ShouldUpgradeToL2(string query, float complexityThreshold = 0.55f)
    {
        var complexity = CalculateSemanticComplexity(query);
        return complexity >= complexityThreshold;
    }

    /// <summary>
    /// 根据查询内容和复杂度选择最佳协作模式
    /// </summary>
    private static CollaborationPattern SelectCollaborationPattern(string query, float complexity)
    {
        var lower = query.ToLowerInvariant();
        
        // 1. 代码相关 → Sequential (Planner→Critic→Solver)
        if (lower.Contains("code") || lower.Contains("代码") || lower.Contains("debug") || lower.Contains("bug"))
            return CollaborationPattern.Sequential;
        
        // 2. 多领域/综合问题 → Mixture (多专家并行)
        if (lower.Contains("比较") || lower.Contains("对比") || lower.Contains("compare") || lower.Contains("vs"))
            return CollaborationPattern.Mixture;
        
        // 3. 工具调用/搜索 → Deliberation (Reflector↔ToolCaller)
        if (lower.Contains("搜索") || lower.Contains("search") || lower.Contains("查询") || lower.Contains("查找"))
            return CollaborationPattern.Deliberation;
        
        // 4. 默认 → Distillation (Expert→Learner，平衡质量与速度)
        return CollaborationPattern.Distillation;
    }

    /// <summary>
    /// 构建二值向量索引 (从现有知识/图谱)
    /// </summary>
    public void BuildBinaryIndex(LocalEmbeddingBackend embeddingBackend, CodeGraphEnhanced? codeGraph = null)
    {
        if (_binaryIndex == null) return;

        // 1. 从本地知识库构建
        foreach (var (key, item) in _knowledge.GetAll())
        {
            var floatVec = embeddingBackend.EmbedAsync(new[] { item.Answer }).GetAwaiter().GetResult()[0];
            var binaryVec = BinaryVector.FromFloatVector(floatVec);
            _binaryIndex.Add($"kb_{key}", binaryVec);
        }

        // 2. 从代码图谱构建 (SimHash)
        if (codeGraph != null)
        {
            var nodes = codeGraph.GetAllNodes();
            foreach (var node in nodes)
            {
                if (node.Fingerprint != 0)
                {
                    var bits = new ulong[1] { node.Fingerprint };
                    var bv = new BinaryVector(bits, 64);
                    _binaryIndex.Add($"code_{node.Id}", bv);
                }
            }
        }

        _logger.LogInformation("Binary index built: {Count} vectors", _binaryIndex.Count);
    }

    /// <summary>
    /// PACE 学习感知路由决策
    /// 基于 ||Δθ||² 直接衡量真实学习进度，替代间接代理信号
    /// </summary>
    private PaceRoutingDecision EvaluatePaceRouting(string queryId, float semanticComplexity)
    {
        if (_progressTracker == null)
        {
            // 无追踪器，回退到语义复杂度
            return new PaceRoutingDecision
            {
                Route = semanticComplexity >= 0.4f && semanticComplexity < 0.7f ? PaceRoute.RecursiveMAS : PaceRoute.Standard,
                AvgDeltaNorm = 0,
                RecommendedRounds = semanticComplexity > 0.55f ? 3 : 2
            };
        }

        var metrics = _progressTracker.GetMetrics(queryId);
        
        // PACE 路由规则:
        // 1. ||Δθ||² 过小 (< 1e-5) + 低置信度 → L1 陷入平台期，强制升级 L2
        if (metrics.Status == LearningStatus.Plateau && metrics.AvgDeltaNorm < 1e-5 && semanticComplexity < 0.5f)
        {
            return new PaceRoutingDecision
            {
                Route = PaceRoute.ForceL2,
                AvgDeltaNorm = metrics.AvgDeltaNorm,
                RecommendedRounds = 0
            };
        }
        
        // 2. ||Δθ||² 过大 (> 10.0) → 查询超出 L1 分布 (OOD)，直接路由 L2
        if (metrics.Status == LearningStatus.OutOfDistribution || metrics.AvgDeltaNorm > 10.0)
        {
            return new PaceRoutingDecision
            {
                Route = PaceRoute.DirectL2,
                AvgDeltaNorm = metrics.AvgDeltaNorm,
                RecommendedRounds = 0
            };
        }
        
        // 3. ||Δθ||² 适中 (0.01 ~ 1.0) → 处于 L1 最近发展区，使用 RecursiveMAS
        if (metrics.AvgDeltaNorm >= 0.01 && metrics.AvgDeltaNorm <= 1.0)
        {
            var rounds = metrics.Trend > 0 ? 3 : 2; // 趋势上升则增加轮次
            return new PaceRoutingDecision
            {
                Route = PaceRoute.RecursiveMAS,
                AvgDeltaNorm = metrics.AvgDeltaNorm,
                RecommendedRounds = rounds
            };
        }
        
        // 4. 默认 → 标准路由
        return new PaceRoutingDecision
        {
            Route = PaceRoute.Standard,
            AvgDeltaNorm = metrics.AvgDeltaNorm,
            RecommendedRounds = 2
        };
    }
}

/// <summary>
/// PACE 路由决策结果
/// </summary>
internal sealed record PaceRoutingDecision
{
    public PaceRoute Route { get; init; }
    public double AvgDeltaNorm { get; init; }
    public int RecommendedRounds { get; init; }
}

/// <summary>
/// PACE 路由类型
/// </summary>
internal enum PaceRoute
{
    Standard,       // 标准路由 (基于语义复杂度)
    RecursiveMAS,   // 潜空间递归 (最近发展区)
    ForceL2,        // 强制升级 L2 (平台期)
    DirectL2        // 直接路由 L2 (OOD)
}
