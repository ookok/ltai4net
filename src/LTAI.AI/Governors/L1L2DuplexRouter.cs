using System.Text.Json;
using System.Text.RegularExpressions;
using LTAI.Capability.Skills;
using LTAI.Vector.Knowledge;
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

    public L1L2DuplexRouter(
        SynapticInference inference,
        SynapticMemory memory,
        KnowledgeGraphBridge graphBridge,
        MetaCognitiveLayer metaCognition,
        SkillTree skillTree,
        SemanticQueryCache cache,
        TeachingRuleExtractor ruleExtractor,
        CostAwareRouter costRouter,
        LocalKnowledgeBase knowledge,
        LocalIntentClassifier fallbackClassifier,
        CellAIRegistry? cellRegistry = null,
        IChatClient? l2Client = null,
        ILogger<L1L2DuplexRouter>? logger = null)
    {
        _inference = inference;
        _memory = memory;
        _graphBridge = graphBridge;
        _metaCognition = metaCognition;
        _skillTree = skillTree;
        _cache = cache;
        _ruleExtractor = ruleExtractor;
        _costRouter = costRouter;
        _knowledge = knowledge;
        _fallbackClassifier = fallbackClassifier;
        _cellRegistry = cellRegistry;
        _l2Client = l2Client;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<L1L2DuplexRouter>.Instance;
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

        var graphResult = _graphBridge.QueryKnowledge(trimmed);
        if (graphResult.FoundInGraph && !string.IsNullOrEmpty(graphResult.Answer))
        {
            return new DuplexRouteResult
            {
                Route = "graph_knowledge",
                Label = "fast",
                Confidence = 0.7f,
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
        _cache.Store(query, teaching.Answer, "delegate_l2", domain, 0.9f, TimeSpan.FromHours(4));

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

    public Dictionary<string, object> GetCacheStats() => _cache.GetStats();

    public CostAwareRouteDecision GetCostDecision(string query, float complexity, float localConfidence, bool hasLocalAnswer)
    {
        return _costRouter.Decide(query, complexity, localConfidence, hasLocalAnswer);
    }

    public void RecordActualCost(double costYuan) => _costRouter.RecordActualCost(costYuan);

    public Dictionary<string, object> GetBudgetStatus() => _costRouter.GetBudgetStatus();

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
            Route = "reflex_unknown",
            Label = "reflex",
            Confidence = 0.5f,
            LocalResponse = $"Unknown command: {query}",
            CanAnswerLocally = true,
            Complexity = 0.1f,
            ModelType = "reflex"
        };
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
        catch (Exception ex)
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
}
