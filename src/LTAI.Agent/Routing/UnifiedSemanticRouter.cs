using System.Diagnostics;
using LTAI.Core.Configuration;
using LTAI.Knowledge.Vector.Interfaces;
using LTAI.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.Agent.Routing;

public sealed record SemanticRoute(
    AgentType Intent,
    AgentType TargetAgent,
    float SemanticScore,
    float KeywordScore,
    float FinalConfidence,
    string? QueryShape,
    bool UseWorkflow,
    bool ShouldBlock)
{
    public static SemanticRoute Rejected(string reason) =>
        new(AgentType.Chat, AgentType.Chat, 0, 0, 0, null, false, ShouldBlock: true);
}

public sealed class UnifiedSemanticRouter
{
    private readonly IVectorStore _vectorStore;
    private readonly IntentRouter _keywordFallback;
    private readonly ILogger<UnifiedSemanticRouter> _logger;
    private readonly float _semanticRejectThreshold;
    private readonly float _keywordRejectThreshold;
    private readonly float _semanticPreferThreshold;
    private readonly float _semanticFusionWeight;
    private readonly float _keywordFusionWeight;
    private readonly float _workflowConfidenceThreshold;
    private readonly int _workflowLengthThreshold;
    private float[][]? _routeEmbeddings; // lazy init
    private bool _initialized;

    private static readonly (AgentType Intent, AgentType Agent, string Description)[] RouteDefinitions =
    {
        (AgentType.Code, AgentType.Code, "write code, debug, refactor, AST analysis, compile, test, programming"),
        (AgentType.EIA, AgentType.EIA, "environmental impact, air quality, emission, GIS, plume dispersion, water quality"),
        (AgentType.EiaCritic, AgentType.EiaCritic, "review EIA report, compliance check, audit standards, report review"),
        (AgentType.Reasoning, AgentType.Reasoning, "analyze deeply, compare, evaluate, logic, architecture design, planning"),
        (AgentType.Chat, AgentType.Chat, "casual conversation, help, general questions, greeting, chat"),
    };

    public UnifiedSemanticRouter(
        IVectorStore vectorStore,
        IntentRouter keywordFallback,
        ILogger<UnifiedSemanticRouter> logger,
        IOptions<LTAIOptions> options)
    {
        _vectorStore = vectorStore;
        _keywordFallback = keywordFallback;
        _logger = logger;
        var t = options.Value.Thresholds;
        _semanticRejectThreshold = t.SemanticRejectThreshold;
        _keywordRejectThreshold = t.KeywordRejectThreshold;
        _semanticPreferThreshold = t.SemanticPreferThreshold;
        _semanticFusionWeight = t.SemanticFusionWeight;
        _keywordFusionWeight = t.KeywordFusionWeight;
        _workflowConfidenceThreshold = t.WorkflowConfidenceThreshold;
        _workflowLengthThreshold = t.WorkflowLengthThreshold;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;
        _routeEmbeddings = new float[RouteDefinitions.Length][];
        for (int i = 0; i < RouteDefinitions.Length; i++)
        {
            _routeEmbeddings[i] = await _vectorStore.EmbedAsync(RouteDefinitions[i].Description, ct);
        }
        _initialized = true;
        _logger.LogInformation("UnifiedSemanticRouter: initialized {Count} route embeddings", RouteDefinitions.Length);
    }

    public async Task<SemanticRoute> RouteAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new SemanticRoute(AgentType.Chat, AgentType.Chat, 0, 0, 1.0f, null, false, false);

        // Layer 1: semantic vector similarity (try; fall back to keyword if unavailable)
        float bestSemanticScore = 0;
        int bestSemanticIdx = 4; // default: Chat

        if (_initialized && _routeEmbeddings != null)
        {
            try
            {
                var queryEmbedding = await _vectorStore.EmbedAsync(text, ct);
                for (int i = 0; i < RouteDefinitions.Length; i++)
                {
                    var score = CosineSimilarity(queryEmbedding, _routeEmbeddings[i]);
                    if (score > bestSemanticScore) { bestSemanticScore = score; bestSemanticIdx = i; }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "UnifiedSemanticRouter: embedding failed, falling back to keyword");
            }
        }

        // Layer 2: keyword fallback
        var keywordRoute = _keywordFallback.Classify(text);
        var keywordScore = keywordRoute.Confidence;
        int keywordIdx = Array.FindIndex(RouteDefinitions, r =>
            r.Agent == keywordRoute.TargetAgent);
        if (keywordIdx < 0) keywordIdx = 4; // Chat

        // Layer 3: confidence circuit breaker
        if (bestSemanticScore < _semanticRejectThreshold && keywordScore < _keywordRejectThreshold)
        {
            _logger.LogWarning("UnifiedSemanticRouter: both scores low (sem={Sem:F2}, kw={Kw:F2}), rejecting",
                bestSemanticScore, keywordScore);
            return SemanticRoute.Rejected("Low confidence across both semantic and keyword routing");
        }

        // Layer 4: fusion
        var finalIdx = bestSemanticScore > _semanticPreferThreshold ? bestSemanticIdx : keywordIdx;
        var finalConfidence = bestSemanticScore > _semanticPreferThreshold
            ? bestSemanticScore * _semanticFusionWeight + keywordScore * _keywordFusionWeight
            : keywordScore * _semanticFusionWeight + bestSemanticScore * _keywordFusionWeight;

        var useWorkflow = finalConfidence < _workflowConfidenceThreshold || text.Length > _workflowLengthThreshold;

        return new SemanticRoute(
            Intent: RouteDefinitions[finalIdx].Intent,
            TargetAgent: RouteDefinitions[finalIdx].Agent,
            SemanticScore: bestSemanticScore,
            KeywordScore: keywordScore,
            FinalConfidence: finalConfidence,
            QueryShape: DetectQueryShape(text),
            UseWorkflow: useWorkflow,
            ShouldBlock: false
        );
    }

    public async Task<IReadOnlyList<SemanticRoute>> RouteAllAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new[] { new SemanticRoute(AgentType.Chat, AgentType.Chat, 0, 0, 1.0f, null, false, false) };

        var keywordRoutes = _keywordFallback.ClassifyAll(text);
        return keywordRoutes.Select(kr =>
        {
            var idx = Array.FindIndex(RouteDefinitions, r =>
                r.Agent == kr.TargetAgent);
            return new SemanticRoute(kr.Intent, kr.TargetAgent, 0, kr.Confidence, kr.Confidence,
                null, kr.Confidence < 0.7f, false);
        }).ToList();
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        float dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        return (float)(dot / (Math.Sqrt(normA) * Math.Sqrt(normB) + 1e-8));
    }

    private static string? DetectQueryShape(string text)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Contains("compare") || lower.Contains("对比")) return "ComparativeAnalysis";
        if (lower.Contains("how to") || lower.Contains("怎么做")) return "ProceduralHowTo";
        if (lower.Contains("calculate") || lower.Contains("计算")) return "NumericCalculation";
        if (lower.Contains("explain") || lower.Contains("解释")) return "SemanticConcept";
        return null;
    }
}
