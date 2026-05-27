using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.Governors;

public interface ICPSProcessingService
{
    Task<CPSResult> ProcessAsync(string query, CancellationToken ct = default);
    IReadOnlyList<string> GetRouteDistribution();
    int GetTotalProcessed();
}

public sealed record CPSPerformanceStats
{
    public int TotalProcessed { get; init; }
    public long AvgLatencyMs { get; init; }
    public long EstimatedTotalTokens { get; init; }
    public Dictionary<string, int> RouteDistribution { get; init; } = new();
    public List<string> RecentDecisions { get; init; } = new();
}

public sealed record CPSResult
{
    public bool Success { get; init; }
    public string Route { get; init; } = "local";
    public string Response { get; init; } = "";
    public float Confidence { get; init; }
    public string? Source { get; init; } // "l0_cache", "l1_fast", "l2_deep", "gene_rule"
    public long LatencyMs { get; init; }
    public string? TokenBudgetRemaining { get; init; }
    public Dictionary<string, object> Metadata { get; init; } = new();
}

public sealed class CPSProcessingService : ICPSProcessingService
{
    private readonly ParetoRouter _paretoRouter;
    private readonly Func<string, CancellationToken, string>? _intentClassifier;
    private readonly Func<string, int, float[]>? _embedder;
    private readonly BootstrapTeacher _teacher;
    private readonly GenePool _genePool;
    private readonly SimulatedAnnealer _annealer;
    private readonly GeneToRule _geneToRule;
    private readonly Func<string, CancellationToken, Task<string>> _l0Invoke;
    private readonly Func<string, CancellationToken, Task<string>> _l1Invoke;
    private readonly Func<string, CancellationToken, Task<string>> _l2Invoke;
    private readonly ILogger<CPSProcessingService> _logger;

    private readonly ConcurrentDictionary<string, int> _routeDistribution = new();
    private readonly LoopTrapDetector? _loopDetector;
    private readonly RecursiveCausalAudit? _causalAudit;
    private readonly SemanticAnchor? _semanticAnchor;
    private int _totalProcessed;
    private long _totalLatencyMs;
    private long _totalTokensEstimated;
    private readonly ConcurrentQueue<string> _recentDecisions = new();

    private static readonly TimeSpan L3DecisionTimeout = TimeSpan.FromMilliseconds(50);

    public CPSProcessingService(
        ParetoRouter paretoRouter,
        Func<string, CancellationToken, string>? intentClassifier,
        BootstrapTeacher teacher,
        GenePool genePool,
        SimulatedAnnealer annealer,
        GeneToRule geneToRule,
        Func<string, CancellationToken, Task<string>> l1Invoke,
        Func<string, CancellationToken, Task<string>> l2Invoke,
        ILogger<CPSProcessingService>? logger = null,
        LoopTrapDetector? loopDetector = null,
        Func<string, CancellationToken, Task<string>>? l0Invoke = null,
        Func<string, int, float[]>? embedder = null,
        RecursiveCausalAudit? causalAudit = null,
        SemanticAnchor? semanticAnchor = null)
    {
        _paretoRouter = paretoRouter;
        _intentClassifier = intentClassifier;
        _embedder = embedder;
        _teacher = teacher;
        _genePool = genePool;
        _annealer = annealer;
        _geneToRule = geneToRule;
        _l0Invoke = l0Invoke;
        _l1Invoke = l1Invoke;
        _l2Invoke = l2Invoke;
        _logger = logger ?? NullLogger<CPSProcessingService>.Instance;
        _loopDetector = loopDetector;
        _causalAudit = causalAudit;
        _semanticAnchor = semanticAnchor;
    }

    public async Task<CPSResult> ProcessAsync(string query, CancellationToken ct = default)
    {
        var sw = global::System.Diagnostics.Stopwatch.StartNew();
        Interlocked.Increment(ref _totalProcessed);

        var intentLabel = _intentClassifier?.Invoke(query, ct) ?? "general";
        var embedding = _embedder != null
            ? _embedder(query, 768)
            : HashEmbedWithProfile(query, intentLabel, 768);

        var trap = _loopDetector?.Check("process", query, embedding);
        if (trap?.Trapped == true)
        {
            var strategy = trap.SuggestedActions.FirstOrDefault() ?? "route_up";
            _loopDetector?.RecordBreak(strategy);
            _logger.LogWarning("[CPS-LoopTrap] {Type} detected: {Reason}, breaking via {Strategy}",
                trap.TrapType, trap.Reason, strategy);

            sw.Stop();
            return new CPSResult
            {
                Success = true,
                Route = "L2",
                Response = await HandleL2Async(
                    $"[Loop trap break: {strategy}] {query}", ct).ConfigureAwait(false),
                Confidence = 0.95f,
                Source = "l2_loop_break",
                LatencyMs = sw.ElapsedMilliseconds,
                Metadata = new Dictionary<string, object>
                {
                    { "trap_type", trap.TrapType },
                    { "break_strategy", strategy },
                    { "severity", trap.Severity }
                }
            };
        }

        var useL2 = await _teacher.ShouldUseL2Async(embedding, ct).ConfigureAwait(false);
        var decision = _paretoRouter.Decide(embedding);

        string response;
        string source;

        if (decision.Route == "reflex")
        {
            response = HandleReflex(query, intentLabel);
            source = "gene_rule";
        }
        else if (decision.Route == "local")
        {
            response = await HandleLocalAsync(query, ct).ConfigureAwait(false);
            source = "l0_cache";
        }
        else if (decision.Route == "L1" || (!useL2 && decision.Confidence > 0.6f))
        {
            response = await HandleL1Async(query, ct).ConfigureAwait(false);
            source = "l1_fast";
            await _teacher.RecordL0DecisionAsync(embedding, decision.Route, ct).ConfigureAwait(false);
        }
        else
        {
            response = await HandleL2Async(query, ct).ConfigureAwait(false);
            source = "l2_deep";

            var (q, s, c) = EstimateMetrics(response);
            await _teacher.RecordL2DecisionAsync(embedding, decision.Route, q, s, c, ct).ConfigureAwait(false);

            if (q > 0.5f)
            {
                var geneId = Guid.NewGuid().ToString("N")[..12];
                _genePool.AddGene(new Gene
                {
                    Id = geneId,
                    Condition = $"intent == \"{intentLabel}\" && complexity < 0.6",
                    Action = $"route:{decision.Route}",
                    Weight = decision.Confidence,
                    Fitness = q,
                    Niche = intentLabel,
                    Source = $"l2_taught_{DateTime.UtcNow:yyyyMMdd}"
                });
            }
        }

        _routeDistribution.AddOrUpdate(decision.Route, 1, (_, v) => v + 1);

        sw.Stop();

        var totalMs = sw.ElapsedMilliseconds;
        Interlocked.Add(ref _totalLatencyMs, totalMs);
        Interlocked.Add(ref _totalTokensEstimated, EstimateTokens(response));

        var decisionTrace = $"route={decision.Route} conf={decision.Confidence:F2} intent={intentLabel} latency={totalMs}ms";
        _recentDecisions.Enqueue(decisionTrace);
        while (_recentDecisions.Count > 100) _recentDecisions.TryDequeue(out _);
        if (totalMs > L3DecisionTimeout.TotalMilliseconds)
        {
            _logger.LogWarning("CPS L3 decision exceeded SLA: {LatMs}ms (limit {LimitMs}ms) for '{Query}'",
                totalMs, (int)L3DecisionTimeout.TotalMilliseconds, query[..Math.Min(query.Length, 40)]);
        }

        if (_semanticAnchor != null && _semanticAnchor.AnchoredLabels == 0)
        {
            _logger.LogWarning("CPS semantic anchor has no anchored labels — routing may be unstable");
        }

        if (_causalAudit != null && source == "l2_deep")
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var auditResult = await _causalAudit.AuditAsync(query, response, response,
                        CancellationToken.None).ConfigureAwait(false);
                    if (!auditResult.Passed)
                    {
                        _logger.LogWarning("CPS causal audit failed for L2 response: {Violations}",
                            string.Join("; ", auditResult.Violations.Take(3)));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "CPS causal audit background task failed");
                }
            });
        }

        _logger.LogInformation("CPS[{Route}] latency={LatMs}ms query='{Query}'",
            decision.Route, totalMs, query[..Math.Min(query.Length, 60)]);

        return new CPSResult
        {
            Success = true,
            Route = decision.Route,
            Response = response,
            Confidence = decision.Confidence,
            Source = source,
            LatencyMs = sw.ElapsedMilliseconds,
            Metadata = new Dictionary<string, object>
            {
                ["intent"] = intentLabel,
                ["shadow"] = decision.IsShadowRouted,
                ["phase"] = _teacher.GetStats().Phase.ToString()
            }
        };
    }

    private string HandleReflex(string query, string intent)
    {
        var rules = _genePool.AllGenes
            .Where(g => g.Fitness > 0.5 && g.Niche == intent)
            .Take(3)
            .ToList();

        if (rules.Count > 0)
            return string.Join("\n", rules.Select(r => r.Action));

        return $"ack:{intent}";
    }

    private async Task<string> HandleLocalAsync(string query, CancellationToken ct)
    {
        if (_l0Invoke != null)
        {
            try
            {
                var result = await _l0Invoke(query, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(result))
                    return result;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "L0 local model invocation failed");
            }
        }
        return $"ack: local fallback for '{query[..Math.Min(query.Length, 30)]}'";
    }

    private async Task<string> HandleL1Async(string query, CancellationToken ct, bool isL2Fallback = false)
    {
        try
        {
            var prompt = isL2Fallback
                ? $"Provide a detailed answer to: {query}"
                : $"Answer concisely: {query}";
            return await _l1Invoke(prompt, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "L1 invocation failed");
            return await HandleLocalAsync(query, ct).ConfigureAwait(false);
        }
    }

    private async Task<string> HandleL2Async(string query, CancellationToken ct)
    {
        try
        {
            var prompt = $"Provide a thorough answer to: {query}";
            return await _l2Invoke(prompt, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "L2 invocation failed, falling back to L1");
            return await HandleL1Async(query, ct, isL2Fallback: true).ConfigureAwait(false);
        }
    }

    private static (float quality, float speed, float cost) EstimateMetrics(string response)
    {
        var len = response.Length;
        var quality = len > 200 ? 0.9f : len > 50 ? 0.7f : 0.5f;
        var speed = 0.3f;
        var cost = 0.8f;
        return (quality, speed, cost);
    }

    /// <summary>
    /// Deterministic projection of text → float[dim] via FNV-1a hash mixing.
    /// Produces well-distributed vectors for ParetoRouter routing decisions.
    /// Replaces the old raw-byte dump that produced collapsed, semantically meaningless embeddings.
    /// </summary>
    private static float[] HashEmbedWithProfile(string text, string domain, int dim)
    {
        var emb = new float[dim];
        var data = $"{domain}|{text}";
        Span<byte> bytes = stackalloc byte[global::System.Text.Encoding.UTF8.GetMaxByteCount(data.Length)];
        var byteCount = global::System.Text.Encoding.UTF8.GetBytes(data, bytes);

        // FNV-1a hash per dimension with offset seeding for distribution
        for (var d = 0; d < dim; d++)
        {
            uint hash = 2166136261u ^ (uint)(d * 16777619);
            for (var i = 0; i < byteCount; i++)
            {
                hash ^= bytes[i];
                hash *= 16777619;
            }
            // Re-hash to spread across dimension axes
            hash ^= (uint)(d * 2654435761);
            hash *= 16777619;
            emb[d] = (hash % 10000u) / 10000f;
        }

        return emb;
    }

    public IReadOnlyList<string> GetRouteDistribution()
    {
        return _routeDistribution
            .OrderByDescending(kv => kv.Value)
            .Select(kv => $"{kv.Key}:{kv.Value}")
            .ToList();
    }

    public int GetTotalProcessed() => _totalProcessed;

    public CPSPerformanceStats GetPerformanceStats()
    {
        var total = _totalProcessed > 0 ? _totalProcessed : 1;
        return new CPSPerformanceStats
        {
            TotalProcessed = _totalProcessed,
            AvgLatencyMs = _totalLatencyMs / total,
            EstimatedTotalTokens = _totalTokensEstimated,
            RouteDistribution = _routeDistribution.ToDictionary(kv => kv.Key, kv => kv.Value),
            RecentDecisions = _recentDecisions.ToList()
        };
    }

    public string ExplainLastDecision()
    {
        var decisions = _recentDecisions.ToList();
        if (decisions.Count == 0) return "No decisions recorded yet.";

        var last = decisions.Last();
        var stats = GetPerformanceStats();
        return $"## CPS Decision Trace\n\n" +
               $"**Last**: {last}\n\n" +
               $"**Summary**: {stats.TotalProcessed} queries processed, " +
               $"avg {stats.AvgLatencyMs}ms latency, " +
               $"~{stats.EstimatedTotalTokens} tokens\n\n" +
               $"**Routes**: {string.Join(", ", stats.RouteDistribution.Select(kv => $"{kv.Key}={kv.Value}"))}";
    }

    private static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return text.Length / 4;
    }
}
