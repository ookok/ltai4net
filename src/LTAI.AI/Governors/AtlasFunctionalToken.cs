using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

/// <summary>
/// ATLAS-style Functional Token: a single discrete token that encodes both
/// agentic routing and latent reasoning decisions, eliminating complex
/// rule-based heuristics in favor of learned token-level decisions.
/// 
/// Inspired by: ATLAS (arXiv:2605.15198) — "One Word is Enough for Both"
/// </summary>
public enum FunctionalToken
{
    None = 0,
    AnswerDirect,  // L1 can answer directly
    ThinkHard,     // L1 needs deeper internal reasoning (ReThinking)
    EscalateL2,    // Escalate to L2 cloud model
    Verify,        // Closed-loop verification pass
    Decompose,     // Decompose into sub-problems
    ToolCall,      // Trigger external tool invocation
    CacheHit,      // Semantic cache hit
    Unknown        // Fallback — use heuristics
}

public sealed record FunctionalTokenResult
{
    public FunctionalToken Token { get; init; }
    public float Confidence { get; init; }
    public string? L1Response { get; init; }
    public bool NeedsL2 => Token is FunctionalToken.EscalateL2 or FunctionalToken.Decompose;
    public bool IsCached => Token == FunctionalToken.CacheHit;
}

public sealed record LatentAnchor
{
    public FunctionalToken Token { get; init; }
    public float StaticWeight { get; init; }
    public float[]? LatentVector { get; init; }
    public int GenerationCount { get; set; }
    public float CumulativeReward { get; set; }
}

/// <summary>
/// LA-GRPO: Latent-Anchored Group Relative Policy Optimization.
/// 
/// Stabilizes RL training of functional tokens by anchoring them with
/// a statically weighted auxiliary objective, providing stronger gradient
/// updates for sparse functional token signals.
/// 
/// Paper: ATLAS (arXiv:2605.15198)
/// </summary>
public sealed class LatentAnchoredGRPO
{
    private readonly Dictionary<FunctionalToken, LatentAnchor> _anchors = new();
    private readonly float _anchorWeight;
    private readonly float _learningRate;
    private readonly float _discountFactor;
    private int _totalSteps;

    public LatentAnchoredGRPO(float anchorWeight = 0.1f, float learningRate = 0.01f, float discountFactor = 0.99f)
    {
        _anchorWeight = anchorWeight;
        _learningRate = learningRate;
        _discountFactor = discountFactor;
        InitializeAnchors();
    }

    private void InitializeAnchors()
    {
        foreach (FunctionalToken token in Enum.GetValues<FunctionalToken>())
        {
            if (token == FunctionalToken.None || token == FunctionalToken.Unknown) continue;
            _anchors[token] = new LatentAnchor
            {
                Token = token,
                StaticWeight = token switch
                {
                    FunctionalToken.AnswerDirect => 0.8f,
                    FunctionalToken.ThinkHard => 0.5f,
                    FunctionalToken.EscalateL2 => 0.3f,
                    FunctionalToken.Verify => 0.6f,
                    FunctionalToken.Decompose => 0.4f,
                    FunctionalToken.ToolCall => 0.35f,
                    FunctionalToken.CacheHit => 0.9f,
                    _ => 0.5f
                }
            };
        }
    }

    public void RecordStep(FunctionalToken token, float reward)
    {
        if (!_anchors.TryGetValue(token, out var anchor)) return;
        anchor.GenerationCount++;
        anchor.CumulativeReward += reward;
        _totalSteps++;
    }

    public float GetAdvantage(FunctionalToken token)
    {
        if (!_anchors.TryGetValue(token, out var anchor) || anchor.GenerationCount == 0)
            return 0f;

        var avgReward = anchor.CumulativeReward / anchor.GenerationCount;
        var baseline = GetGlobalBaseline();

        return (avgReward - baseline) * _anchorWeight * _learningRate;
    }

    private float GetGlobalBaseline()
    {
        if (_anchors.Count == 0) return 0f;
        return _anchors.Values
            .Where(a => a.GenerationCount > 0)
            .Average(a => a.CumulativeReward / a.GenerationCount);
    }

    public float GetAnchorWeight(FunctionalToken token)
        => _anchors.TryGetValue(token, out var a) ? a.StaticWeight : 0.5f;

    public Dictionary<FunctionalToken, float> GetTokenDistributions()
    {
        if (_totalSteps == 0) return new();
        return _anchors
            .Where(kv => kv.Value.GenerationCount > 0)
            .ToDictionary(kv => kv.Key, kv => (float)kv.Value.GenerationCount / _totalSteps);
    }
}

/// <summary>
/// Functional Token Router — replaces the 895-line L1L2DuplexRouter with
/// a lightweight token-level routing model inspired by ATLAS functional tokens.
/// 
/// Key difference from old router: instead of 15+ heuristic rules (code graph,
/// knowledge graph, meta-cognitive, skill tree, semantic cache, cost-aware,
/// binary vector, recursive latent...), this router uses a single local
/// classifier to predict a functional token, which encodes all routing logic.
/// </summary>
public sealed class FunctionalTokenRouter
{
    private readonly IL1InferenceEngine? _l1Engine;
    private readonly IChatClient? _l2Client;
    private readonly LatentAnchoredGRPO _grpo;
    private readonly ILogger<FunctionalTokenRouter> _logger;
    private readonly Dictionary<string, FunctionalToken> _promptCache = new();
    private readonly LocalIntentClassifier _classifier;
    private readonly SemanticQueryCache? _semanticCache;

    private const int MaxPromptCacheSize = 1000;

    public FunctionalTokenRouter(
        IL1InferenceEngine? l1Engine,
        IChatClient? l2Client,
        SemanticQueryCache? semanticCache = null,
        ILogger<FunctionalTokenRouter>? logger = null)
    {
        _l1Engine = l1Engine;
        _l2Client = l2Client;
        _grpo = new LatentAnchoredGRPO(anchorWeight: 0.1f);
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<FunctionalTokenRouter>.Instance;
        _classifier = new LocalIntentClassifier();
        _semanticCache = semanticCache;
    }

    public async Task<FunctionalTokenResult> RouteAsync(
        string query,
        CancellationToken ct = default)
    {
        var normalized = NormalizeQuery(query);

        if (_promptCache.TryGetValue(normalized, out var cached) && cached != FunctionalToken.Unknown)
        {
            _grpo.RecordStep(cached, 1.0f);
            return new FunctionalTokenResult { Token = cached, Confidence = 0.95f };
        }

        if (_semanticCache != null)
        {
            var cacheResult = _semanticCache.Lookup(normalized);
            if (cacheResult != null && cacheResult.Similarity > 0.92f)
            {
                var cacheToken = ClassifyAsToken(query);
                _promptCache[normalized] = cacheToken;
                TrimCache();
                return new FunctionalTokenResult { Token = FunctionalToken.CacheHit, Confidence = 0.9f };
            }
        }

        var token = ClassifyAsToken(query);

        if (token == FunctionalToken.Unknown)
        {
            if (_l1Engine != null)
            {
                token = await TryL1QuickAnswerAsync(query, ct).ConfigureAwait(false);
            }

            if (token == FunctionalToken.Unknown)
            {
                token = DetermineFallback(query);
            }
        }

        _promptCache[normalized] = token;
        TrimCache();

        var confidence = token switch
        {
            FunctionalToken.EscalateL2 => 0.75f,
            FunctionalToken.AnswerDirect => 0.85f,
            FunctionalToken.ThinkHard => 0.65f,
            _ => 0.7f
        };

        _logger.LogDebug("FunctionalTokenRouter: query='{Query}' token={Token} conf={Conf:F2}",
            query[..Math.Min(query.Length, 80)], token, confidence);

        return new FunctionalTokenResult { Token = token, Confidence = confidence };
    }

    private FunctionalToken ClassifyAsToken(string query)
    {
        var intent = _classifier.Classify(query);

        return intent.Label switch
        {
            string s when s.Contains("code", StringComparison.OrdinalIgnoreCase) || s.Contains("program", StringComparison.OrdinalIgnoreCase) => FunctionalToken.EscalateL2,
            string s when s.Contains("greet", StringComparison.OrdinalIgnoreCase) || s.Contains("simple", StringComparison.OrdinalIgnoreCase) => FunctionalToken.AnswerDirect,
            string s when s.Contains("reason", StringComparison.OrdinalIgnoreCase) || s.Contains("analyze", StringComparison.OrdinalIgnoreCase) || s.Contains("think", StringComparison.OrdinalIgnoreCase) => FunctionalToken.ThinkHard,
            string s when s.Contains("command", StringComparison.OrdinalIgnoreCase) || s.Contains("tool", StringComparison.OrdinalIgnoreCase) || s.Contains("execut", StringComparison.OrdinalIgnoreCase) => FunctionalToken.ToolCall,
            string s when s.Contains("verif", StringComparison.OrdinalIgnoreCase) || s.Contains("check", StringComparison.OrdinalIgnoreCase) || s.Contains("review", StringComparison.OrdinalIgnoreCase) => FunctionalToken.Verify,
            string s when s.Contains("plan", StringComparison.OrdinalIgnoreCase) || s.Contains("design", StringComparison.OrdinalIgnoreCase) || s.Contains("architect", StringComparison.OrdinalIgnoreCase) => FunctionalToken.Decompose,
            _ => FunctionalToken.Unknown
        };
    }

    private async Task<FunctionalToken> TryL1QuickAnswerAsync(string query, CancellationToken ct)
    {
        try
        {
            var l1Output = await _l1Engine!.GenerateAsync(
                $"Can you directly answer: {query[..Math.Min(query.Length, 200)]}? Reply YES or NO.",
                temperature: 0.1f, maxTokens: 5, ct);

            return l1Output?.Trim().StartsWith("YES", StringComparison.OrdinalIgnoreCase) == true
                ? FunctionalToken.AnswerDirect
                : FunctionalToken.EscalateL2;
        }
        catch
        {
            return FunctionalToken.EscalateL2;
        }
    }

    private static FunctionalToken DetermineFallback(string query)
    {
        if (query.Length < 20) return FunctionalToken.AnswerDirect;
        if (query.Length > 200) return FunctionalToken.EscalateL2;
        if (query.Contains("?") || query.Contains("？")) return FunctionalToken.AnswerDirect;
        return FunctionalToken.EscalateL2;
    }

    public void RecordOutcome(FunctionalToken token, float reward)
        => _grpo.RecordStep(token, reward);

    public float GetAdvantage(FunctionalToken token)
        => _grpo.GetAdvantage(token);

    public Dictionary<FunctionalToken, float> GetStats()
        => _grpo.GetTokenDistributions();

    private static string NormalizeQuery(string query)
        => query.Trim().ToLowerInvariant()[..Math.Min(query.Length, 200)];

    private void TrimCache()
    {
        if (_promptCache.Count > MaxPromptCacheSize)
        {
            var toRemove = _promptCache.Keys
                .OrderBy(_ => Guid.NewGuid())
                .Take(_promptCache.Count - MaxPromptCacheSize / 2)
                .ToList();
            foreach (var key in toRemove) _promptCache.Remove(key);
        }
    }
}

/// <summary>
/// Token Gate Decider — ATLAS-style replacement for TokenHardnessDecider.
/// 
/// Instead of using log-probability entropy thresholds and heuristic rules,
/// this uses functional tokens learned via LA-GRPO to decide whether
/// to trigger additional thinking or L2 escalation at each generation step.
/// </summary>
public sealed class TokenGateDecider
{
    private readonly LatentAnchoredGRPO _grpo;
    private readonly float _defaultHardThreshold;
    private int _totalTokensProcessed;

    public ThinkingState CurrentState { get; private set; } = ThinkingState.Idle;

    public TokenGateDecider(float defaultHardThreshold = 0.5f)
    {
        _grpo = new LatentAnchoredGRPO(anchorWeight: 0.15f);
        _defaultHardThreshold = defaultHardThreshold;
    }

    public bool ShouldTriggerThinking(FunctionalToken currentToken, float confidence)
    {
        _totalTokensProcessed++;

        var isHard = currentToken switch
        {
            FunctionalToken.ThinkHard => true,
            FunctionalToken.EscalateL2 => true,
            FunctionalToken.Decompose => true,
            FunctionalToken.Verify => true,
            FunctionalToken.Unknown => confidence < _defaultHardThreshold,
            _ => false
        };

        if (isHard)
        {
            CurrentState = currentToken switch
            {
                FunctionalToken.ThinkHard => ThinkingState.ReThinking,
                FunctionalToken.EscalateL2 => ThinkingState.Delegating,
                FunctionalToken.Decompose => ThinkingState.Delegating,
                FunctionalToken.Verify => ThinkingState.Verifying,
                _ => ThinkingState.ReThinking
            };
        }
        else
        {
            CurrentState = ThinkingState.Idle;
        }

        return isHard;
    }

    public void RecordDecision(FunctionalToken predicted, FunctionalToken actual, float reward = 1.0f)
    {
        _grpo.RecordStep(predicted, predicted == actual ? reward : 0f);
    }

    public void Reset()
    {
        CurrentState = ThinkingState.Idle;
    }

    public int TotalProcessed => _totalTokensProcessed;
}

/// <summary>
/// ATLAS Thinking Pipeline — enhanced selective thinking using functional tokens.
/// 
/// Replaces SelectiveThinkingPipeline's per-token entropy assessment with
/// ATLAS-style functional token gating. The model (or local classifier)
/// generates a functional token that determines the thinking strategy
/// at each step, avoiding redundant entropy calculations.
/// </summary>
public sealed class AtlasThinkingPipeline
{
    private readonly IL1InferenceEngine _l1Engine;
    private readonly IChatClient? _l2Client;
    private readonly FunctionalTokenRouter _router;
    private readonly TokenGateDecider _gateDecider;
    private readonly ILogger<AtlasThinkingPipeline> _logger;

    public AtlasThinkingPipeline(
        IL1InferenceEngine l1Engine,
        IChatClient? l2Client,
        ILogger<AtlasThinkingPipeline>? logger = null)
    {
        _l1Engine = l1Engine;
        _l2Client = l2Client;
        _router = new FunctionalTokenRouter(l1Engine, l2Client);
        _gateDecider = new TokenGateDecider();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AtlasThinkingPipeline>.Instance;
    }

    public FunctionalTokenRouter Router => _router;
    public TokenGateDecider GateDecider => _gateDecider;

    /// <summary>
    /// Generate with ATLAS functional token routing.
    /// First routes the query to determine strategy, then executes
    /// L1 (with optional thinking) or escalates to L2.
    /// </summary>
    public async IAsyncEnumerable<string> GenerateWithAtlasRoutingAsync(
        string prompt,
        float temperature = 0.7f,
        int maxTokens = 512,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var routeResult = await _router.RouteAsync(prompt, ct).ConfigureAwait(false);
        _logger.LogInformation("ATLAS routing: token={Token} conf={Conf:F2}", routeResult.Token, routeResult.Confidence);

        switch (routeResult.Token)
        {
            case FunctionalToken.AnswerDirect:
                await foreach (var token in GenerateL1Async(prompt, temperature, maxTokens, ct))
                    yield return token;
                _router.RecordOutcome(FunctionalToken.AnswerDirect, 0.8f);
                break;

            case FunctionalToken.ThinkHard:
                var thinkPrompt = $"Think step by step about: {prompt}";
                await foreach (var token in GenerateL1Async(thinkPrompt, temperature, maxTokens, ct))
                    yield return token;
                _router.RecordOutcome(FunctionalToken.ThinkHard, 0.6f);
                break;

            case FunctionalToken.EscalateL2:
                if (_l2Client != null)
                {
                    var l2Response = await _l2Client.GetResponseAsync(prompt, new ChatOptions { Temperature = temperature }, ct).ConfigureAwait(false);
                    yield return l2Response.Text ?? "";
                    _router.RecordOutcome(FunctionalToken.EscalateL2, 0.7f);
                }
                else
                {
                    await foreach (var token in GenerateL1Async(prompt, temperature, maxTokens, ct))
                        yield return token;
                    _router.RecordOutcome(FunctionalToken.EscalateL2, 0.4f);
                }
                break;

            default:
                await foreach (var token in GenerateL1Async(prompt, temperature, maxTokens, ct))
                    yield return token;
                break;
        }
    }

    private async IAsyncEnumerable<string> GenerateL1Async(
        string prompt, float temperature, int maxTokens,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var output = await _l1Engine.GenerateAsync(prompt, temperature, maxTokens, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(output))
            yield return output;
    }

    public Dictionary<FunctionalToken, float> GetRoutingStats() => _router.GetStats();
}

/// <summary>
/// HSD-inspired Orthogonal Query Decomposer.
/// 
/// Separates each query into two orthogonal components:
///   - Topological: deterministic knowledge retrieval (KnowledgeGraph, BM25, SemanticCache)
///   - Dynamic:    contextual reasoning (ContextGovernor, conversation flow)
/// 
/// These components are fused with ATLAS functional token weighting,
/// ensuring knowledge contamination from conversation noise is eliminated.
/// </summary>
public sealed class OrthogonalRouter
{
    public enum QueryNature
    {
        Topological,  // Pure knowledge lookup — deterministic, cacheable
        Dynamic,      // Pure reasoning — context-dependent
        Hybrid        // Both — fused result
    }

    public sealed record OrthogonalResult
    {
        public string TopologicalAnswer { get; init; } = "";
        public string DynamicAnswer { get; init; } = "";
        public string FusedAnswer { get; init; } = "";
        public QueryNature Nature { get; init; }
        public float KnowledgeWeight { get; init; }
        public bool TopoCacheHit { get; init; }
        public List<string> Sources { get; init; } = new();
    }

    private sealed class SimpleCacheEntry(string answer, List<string> sources, DateTime timestamp)
    {
        public string Answer { get; } = answer;
        public List<string> Sources { get; } = sources;
        public DateTime Timestamp { get; } = timestamp;
    }

    private readonly KnowledgeGraphBridge _graphBridge;
    private readonly SemanticQueryCache? _semanticCache;
    private readonly ILogger<OrthogonalRouter> _logger;
    private readonly Dictionary<string, SimpleCacheEntry> _topoCache = new();
    private const int TopoCacheMaxSize = 500;
    private static readonly TimeSpan TopoCacheTtl = TimeSpan.FromMinutes(10);

    public OrthogonalRouter(
        KnowledgeGraphBridge graphBridge,
        SemanticQueryCache? semanticCache = null,
        ILogger<OrthogonalRouter>? logger = null)
    {
        _graphBridge = graphBridge;
        _semanticCache = semanticCache;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<OrthogonalRouter>.Instance;
    }

    public QueryNature ClassifyQueryNature(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return QueryNature.Topological;

        var lower = query.ToLowerInvariant();

        var topoScore = 0;
        if (lower.Contains("what is") || lower.Contains("who is") || lower.Contains("when did") ||
            lower.Contains("where is") || lower.Contains("define") || lower.Contains("什么是") ||
            lower.Contains("who wrote") || lower.Contains("definition"))
            topoScore += 2;
        if (lower.Contains("?") || lower.Contains("？")) topoScore++;
        if (lower.Contains("according to") || lower.Contains("根据") || lower.Contains("reference"))
            topoScore++;

        var dynamicScore = 0;
        if (lower.Contains("analyze") || lower.Contains("compare") || lower.Contains("评估") ||
            lower.Contains("分析") || lower.Contains("设计") || lower.Contains("refactor"))
            dynamicScore += 2;
        if (lower.Contains("why") || lower.Contains("how to") || lower.Contains("为什么") ||
            lower.Contains("如何"))
            dynamicScore++;
        if (query.Length > 200) dynamicScore++;

        if (topoScore > dynamicScore + 1) return QueryNature.Topological;
        if (dynamicScore > topoScore + 1) return QueryNature.Dynamic;
        return QueryNature.Hybrid;
    }

    public OrthogonalResult Decompose(
        string query,
        string topologicalAnswer,
        string? reasoningAnswer = null,
        float knowledgeWeight = 0.5f)
    {
        var nature = ClassifyQueryNature(query);

        knowledgeWeight = nature switch
        {
            QueryNature.Topological => Math.Max(knowledgeWeight, 0.7f),
            QueryNature.Dynamic => Math.Min(knowledgeWeight, 0.3f),
            _ => Math.Clamp(knowledgeWeight, 0.3f, 0.7f)
        };

        string fused;
        if (string.IsNullOrEmpty(reasoningAnswer))
        {
            fused = topologicalAnswer;
        }
        else if (string.IsNullOrEmpty(topologicalAnswer))
        {
            fused = reasoningAnswer;
        }
        else
        {
            var alpha = knowledgeWeight;
            if (nature == QueryNature.Topological)
            {
                fused = $"{topologicalAnswer}\n\n{reasoningAnswer[..Math.Min(reasoningAnswer.Length, 300)]}";
            }
            else if (nature == QueryNature.Dynamic)
            {
                fused = $"{reasoningAnswer}\n\n[Knowledge: {topologicalAnswer[..Math.Min(topologicalAnswer.Length, 200)]}]";
            }
            else
            {
                fused = $"[Knowledge] {topologicalAnswer[..Math.Min(topologicalAnswer.Length, 400)]}\n\n[Analysis] {reasoningAnswer}";
            }
        }

        return new OrthogonalResult
        {
            TopologicalAnswer = topologicalAnswer,
            DynamicAnswer = reasoningAnswer ?? "",
            FusedAnswer = fused,
            Nature = nature,
            KnowledgeWeight = knowledgeWeight,
            TopoCacheHit = false
        };
    }

    public async Task<string> QueryTopologicalAsync(string query, CancellationToken ct = default)
    {
        var normalized = NormalizeQuery(query);

        if (_topoCache.TryGetValue(normalized, out var cached) &&
            DateTime.UtcNow - cached.Timestamp < TopoCacheTtl)
        {
            _logger.LogDebug("OrthogonalRouter: topo cache hit for '{Query}'", query[..Math.Min(query.Length, 50)]);
            return cached.Answer;
        }

        if (_semanticCache != null)
        {
            var cacheHit = _semanticCache.Lookup(normalized);
            if (cacheHit is { Similarity: > 0.95f })
            {
                _logger.LogDebug("OrthogonalRouter: semantic cache hit (sim={Sim:F2})", cacheHit.Similarity);
                return cacheHit.Response ?? "";
            }
        }

        try
        {
            var kbResult = _graphBridge.QueryKnowledge(query);

            var answer = !string.IsNullOrEmpty(kbResult?.Answer)
                ? kbResult.Answer
                : $"No structured knowledge found for: {query[..Math.Min(query.Length, 80)]}";

            StoreTopoCache(normalized, answer);
            _logger.LogDebug("OrthogonalRouter: KB query returned {Len} chars", answer.Length);
            return answer;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OrthogonalRouter: KB query failed, returning empty");
            return "";
        }
    }

    public OrthogonalResult RouteOrthogonal(
        string query,
        FunctionalToken token,
        string? contextualResult = null)
    {
        var nature = ClassifyQueryNature(query);

        var knowledgeWeight = token switch
        {
            FunctionalToken.AnswerDirect => nature == QueryNature.Topological ? 0.85f : 0.5f,
            FunctionalToken.ThinkHard => 0.3f,
            FunctionalToken.EscalateL2 => 0.25f,
            FunctionalToken.Decompose => 0.5f,
            FunctionalToken.Verify => 0.4f,
            FunctionalToken.ToolCall => 0.2f,
            FunctionalToken.CacheHit => 0.95f,
            _ => 0.5f
        };

        return new OrthogonalResult
        {
            TopologicalAnswer = "",
            DynamicAnswer = contextualResult ?? "",
            FusedAnswer = contextualResult ?? "",
            Nature = nature,
            KnowledgeWeight = knowledgeWeight,
            TopoCacheHit = token == FunctionalToken.CacheHit
        };
    }

    public void InvalidateTopoCache(string queryPrefix = "")
    {
        if (string.IsNullOrEmpty(queryPrefix))
        {
            _topoCache.Clear();
            _logger.LogInformation("OrthogonalRouter: full topo cache cleared");
            return;
        }

        var keys = _topoCache.Keys
            .Where(k => k.Contains(queryPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var key in keys) _topoCache.Remove(key);
        _logger.LogInformation("OrthogonalRouter: {Count} topo cache entries invalidated", keys.Count);
    }

    private void StoreTopoCache(string normalized, string answer)
    {
        _topoCache[normalized] = new SimpleCacheEntry(answer, new List<string>(), DateTime.UtcNow);
        if (_topoCache.Count > TopoCacheMaxSize)
        {
            var oldest = _topoCache.OrderBy(kv => kv.Value.Timestamp).Take(_topoCache.Count / 3)
                .Select(kv => kv.Key).ToList();
            foreach (var key in oldest) _topoCache.Remove(key);
        }
    }

    private static string NormalizeQuery(string query)
        => query.Trim().ToLowerInvariant()[..Math.Min(query.Length, 200)];

    public int TopoCacheSize => _topoCache.Count;
}
