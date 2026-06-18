using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using LTAI.AI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Context;

/// <summary>
/// Lookahead provider selector — predicts which context providers are relevant
/// for the current query and injects a route marker so expensive providers can
/// skip themselves when not needed.
///
/// Inspired by FlashMemory-DeepSeek-V4's Lookahead Sparse Attention paradigm:
/// proactively predict context demands rather than passively loading everything.
///
/// Classification uses keyword patterns (fast path, 0 embedding call) and
/// optionally MiniLM ONNX embedding (when available) for fuzzy refinement.
/// </summary>
public sealed class LookaheadProviderSelector : AIContextProvider
{
    private readonly EmbeddingClient? _embedder;
    private readonly ILogger<LookaheadProviderSelector>? _logger;

    // ── Telemetry ──
    private static readonly Meter LookaheadMeter = new("LTAI.Agent.LookaheadProvider");
    private static readonly Counter<long> MetricClassified = LookaheadMeter.CreateCounter<long>("ltai.lookahead.classified", "calls", "Queries classified");
    private static readonly Counter<long> MetricProvidersSkipped = LookaheadMeter.CreateCounter<long>("ltai.lookahead.skipped", "providers", "Provider skips issued");
    private static readonly Counter<long> MetricCacheHits = LookaheadMeter.CreateCounter<long>("ltai.lookahead.cache_hits", "calls", "Conversation cache hits");
    private static readonly Counter<long> MetricShortQuerySkip = LookaheadMeter.CreateCounter<long>("ltai.lookahead.short_skip", "calls", "Short queries (<5 chars) skipped");

    // ── Per-conversation classification cache ──
    // Dynamic boundary caching: tracks skip accuracy per provider to decide
    // when to bypass classification (MGPO-inspired: focus on uncertain predictions).
    private string? _lastQuery;
    private float[]? _lastQueryVec;
    private HashSet<string>? _lastDomains;

    /// <summary>Provider skip accuracy tracker: name → (predicted correct, total skips)</summary>
    private static readonly ConcurrentDictionary<string, (int Correct, int Total)> SkipAccuracy
        = new(StringComparer.OrdinalIgnoreCase);
    private const int SkipAccuracyMaxEntries = 100;

    /// <summary>
    /// MGPO-style boundary threshold. A provider is at the "capability boundary"
    /// when its skip accuracy is ~70-90%. Below 70% → too unreliable (don't skip).
    /// Above 90% → too certain (skip unconditionally without overhead).
    /// Only providers in the boundary zone trigger full classification logic.
    /// </summary>
    private static (double lower, double upper) BoundaryZone => (0.70, 0.90);

    /// <summary>Record a skip prediction outcome for a provider.</summary>
    public static void RecordSkipOutcome(string providerName, bool wasCorrect)
    {
        // Bounded capacity: avoid unbounded growth from many unique providers
        if (SkipAccuracy.Count >= SkipAccuracyMaxEntries && !SkipAccuracy.ContainsKey(providerName))
        {
            var oldest = SkipAccuracy.Keys.FirstOrDefault();
            if (oldest != null) SkipAccuracy.TryRemove(oldest, out _);
        }
        SkipAccuracy.AddOrUpdate(providerName,
            _ => (wasCorrect ? 1 : 0, 1),
            (_, old) => (old.Correct + (wasCorrect ? 1 : 0), old.Total + 1));
    }

    /// <summary>
    /// Get the skip accuracy for a provider, clamped to [0,1].
    /// Returns 0.5 (uncertain) when no data is available.
    /// </summary>
    internal static double GetSkipAccuracy(string providerName)
    {
        if (SkipAccuracy.TryGetValue(providerName, out var data) && data.Total > 0)
            return (double)data.Correct / data.Total;
        return 0.5;
    }

    /// <summary>
    /// Should we skip the full classification for this provider?
    /// MGPO-based: certain providers (accuracy > 90%) are skipped directly;
    /// unreliable ones (accuracy < 70%) are always classified;
    /// boundary zone providers (70-90%) use the full lookahead logic.
    /// </summary>
    internal static bool ShouldBypassClassification(string providerName)
    {
        var acc = GetSkipAccuracy(providerName);
        if (acc >= BoundaryZone.upper) return true;   // highly predictable → skip overhead
        if (acc <= BoundaryZone.lower) return false;   // unreliable → always classify
        return false; // boundary zone → use standard logic
    }

    // ── Domain keyword patterns (fast path, zero embedding) ──
    private static readonly (string[] Keywords, string Label)[] DomainPatterns =
    [
        (new[] { "code", "function", "class", "method", "refactor", "implement", "bug", "fix", "compile",
                  "syntax", "api", "interface", "async", "await", "linq", "delegate", "event", "generic",
                  "代码", "函数", "类", "方法", "重构", "实现", "错误", "编译" }, "code"),
        (new[] { "knowledge", "know", "what is", "explain", "concept", "architecture", "design",
                  "pattern", "principle", "规范", "架构", "概念", "解释", "知识" }, "knowledge"),
        (new[] { "memory", "remember", "recall", "previous", "history", "过去", "之前", "回忆", "记忆" }, "memory"),
        (new[] { "diary", "journal", "log", "record", "what happened", "日记", "日志", "记录", "发生了什么" }, "diary"),
        (new[] { "system", "shell", "terminal", "command", "process", "file", "directory", "network",
                  "install", "config", "环境", "系统", "命令", "进程", "文件", "目录" }, "system"),
        (new[] { "document", "doc", "word", "excel", "ppt", "pdf", "office", "文档", "文件", "word文档", "表格" }, "document"),
        (new[] { "test", "unit test", "integration", "benchmark", "coverage", "测试", "单元测试", "集成测试", "覆盖率" }, "test"),
        (new[] { "security", "vulnerability", "exploit", "cve", "permission", "安全", "漏洞", "权限" }, "security"),
        (new[] { "database", "sql", "query", "table", "index", "migration", "数据库", "sql查询", "表", "索引" }, "database"),
    ];

    // Heavy: providers that do significant I/O or compute — skip aggressively
    private static readonly string[] HeavyProviders =
    ["KbGraph", "CgGraph", "CodeChunkIndex", "WasmtimeSandbox", "L4DeepSearch", "L6AgentDiary", "ProvenanceProvider", "LspDiagnosticsProvider"];

    // Medium: providers with moderate cost
    private static readonly string[] MediumProviders =
    ["L3OnDemand"];

    // All providers (heavy + medium) for feedback iteration
    private static readonly string[] AllProviderNames =
        [.. HeavyProviders, .. MediumProviders];

    // Minimum threshold for embedding-based domain similarity (ignored when embedder unavailable)
    private const double EmbeddingSimilarityThreshold = 0.20;

    // ── ContextRL contrastive feedback calibration ──
    // Per-provider threshold overrides learned from contrastive pairs.
    // When set (> 0), replaces the global EmbeddingSimilarityThreshold for that provider.
    private static readonly ConcurrentDictionary<string, double> PerProviderThreshold
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Record contrastive feedback for a (query, provider) pair.
    /// Called by downstream providers when they determine whether their data was useful.
    /// </summary>
    public static void RecordProviderFeedback(string query, float[]? queryEmbedding, string provider, bool wasUseful)
    {
        ContrastiveFeedbackStore.Record(query, queryEmbedding, provider, wasUseful);
    }

    /// <summary>
    /// Apply calibrated thresholds from contrastive feedback analysis.
    /// Called automatically by ContrastiveFeedbackStore when enough data accumulates.
    /// </summary>
    internal static void ApplyCalibratedThresholds(Dictionary<string, double> thresholds)
    {
        foreach (var (provider, threshold) in thresholds)
            PerProviderThreshold[provider] = Math.Clamp(threshold, 0.05, 0.95);
    }

    /// <summary>
    /// Record that a provider was consulted (not skipped) and produced meaningful output.
    /// Called by downstream providers after they pass the IsProviderSkipped check.
    /// </summary>
    public static void RecordProviderUsed(string providerName)
        => RecordSkipOutcome(providerName, wasCorrect: true);

    /// <summary>
    /// Record that a skipped provider would have been useful (false negative).
    /// Called by agent logic when it detects that data from a skipped provider
    /// would have helped answer the query.
    /// </summary>
    public static void RecordProviderMissed(string providerName)
        => RecordSkipOutcome(providerName, wasCorrect: false);

    public LookaheadProviderSelector(EmbeddingClient? embedder = null, ILogger<LookaheadProviderSelector>? logger = null)
        : base(null, null, null)
    {
        _embedder = embedder;
        _logger = logger;
        // Wire contrastive feedback calibration callback (decouples reverse dependency)
        ContrastiveFeedbackStore.OnCalibrated = ApplyCalibratedThresholds;
    }

    public override IReadOnlyList<string> StateKeys => ["LookaheadRoute"];

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken ct = default)
    {
        try
        {
            var query = ExtractQuery(context);
            if (string.IsNullOrWhiteSpace(query))
                return new AIContext();

            // ── Early exit for very short queries (<5 chars) ──
            // These are typically acknowledgments, greetings, or one-word answers
            // that don't need any heavy provider. Skip classification overhead.
            if (query.Length < 5)
            {
                MetricShortQuerySkip.Add(1);
                // Short queries (greetings/acknowledgments) skip feedback recording
                // to avoid polluting contrastive signal with noise.
                return await ClassifyAndSkipAsync("general", ct).ConfigureAwait(false);
            }

            var domains = await ClassifyAsync(query, ct).ConfigureAwait(false);

            var skipList = BuildSkipList(domains);
            if (skipList.Count == 0)
                return new AIContext();

            var routeText = $"<provider-route skip=\"{string.Join(",", skipList)}\" />";

            MetricClassified.Add(1);
            MetricProvidersSkipped.Add(skipList.Count);

            // ContextRL: record contrastive feedback for each skip/keep decision
            // so downstream calibration can learn optimal per-domain thresholds.
            var qVec = _lastQueryVec;
            foreach (var p in AllProviderNames)
            {
                var skipped = skipList.Contains(p);
                // Record every decision as an implicit "we predicted skip={skipped}".
                // Actual usefulness is determined when the provider checks IsProviderSkipped.
                ContrastiveFeedbackStore.Record(query, qVec, p, wasUseful: !skipped);
            }

            _logger?.LogDebug("LookaheadProviderSelector: query=\"{Query}\" domains=[{Domains}] skip=[{Skip}]",
                query.Length > 60 ? query[..60] + "..." : query,
                string.Join(",", domains),
                string.Join(",", skipList));

            return new AIContext
            {
                Messages = [new ChatMessage(ChatRole.System, routeText)]
            };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "LookaheadProviderSelector: classification failed");
            return new AIContext();
        }
    }

    // ── Bootstrap: skip all heavy/medium for given domains ──
    private async ValueTask<AIContext> ClassifyAndSkipAsync(string domain, CancellationToken ct)
    {
        var domains = new HashSet<string> { domain };
        var skipList = BuildSkipList(domains);
        if (skipList.Count == 0) return new AIContext();

        var routeText = $"<provider-route skip=\"{string.Join(",", skipList)}\" />";
        MetricClassified.Add(1);
        MetricProvidersSkipped.Add(skipList.Count);

        return new AIContext
        {
            Messages = [new ChatMessage(ChatRole.System, routeText)]
        };
    }

    private static string ExtractQuery(InvokingContext context)
    {
        var msgs = context.AIContext?.Messages;
        if (msgs == null) return "";

        var list = msgs.ToList();
        if (list.Count == 0) return "";

        for (int i = list.Count - 1; i >= 0; i--)
        {
            var text = list[i].Text;
            if (!string.IsNullOrWhiteSpace(text) && list[i].Role == ChatRole.User)
                return text;
        }

        return list.LastOrDefault()?.Text ?? "";
    }

    /// <summary>Compute cosine similarity between current and last query.</summary>
    private async Task<double> ComputeQuerySimilarityAsync(string query, CancellationToken ct)
    {
        if (_embedder == null || _lastQueryVec == null) return 0;

        try
        {
            var curVec = await _embedder.GenerateAsync(query, ct).ConfigureAwait(false);
            return CosineSimilarity(curVec, _lastQueryVec);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Keyword overlap ratio between two strings.</summary>
    private static double ComputeKeywordOverlap(string a, string b)
    {
        var wordsA = a.ToLowerInvariant().Split([' ', '\t', '\n', '.', ',', ';', ':', '-', '_'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var wordsB = b.ToLowerInvariant().Split([' ', '\t', '\n', '.', ',', ';', ':', '-', '_'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (wordsA.Length == 0 || wordsB.Length == 0) return 0;

        var setB = new HashSet<string>(wordsB);
        var common = wordsA.Count(w => setB.Contains(w));
        return (double)common / Math.Max(wordsA.Length, wordsB.Length);
    }

    private async Task<HashSet<string>> ClassifyAsync(string query, CancellationToken ct)
    {
        var result = new HashSet<string>();

        // ── Conversation-level cache (semantic similarity) ──
        // Reuse previous classification when the current query is semantically
        // similar to the last one. Uses cosine similarity on GloVe/ONNX embeddings
        // when available, falls back to keyword overlap ratio.
        if (_lastDomains != null && _lastQuery != null && _lastQuery.Length > 0)
        {
            var similarity = await ComputeQuerySimilarityAsync(query, ct).ConfigureAwait(false);
            var keywordOverlap = ComputeKeywordOverlap(query, _lastQuery);

            if (similarity > 0.65 || keywordOverlap > 0.5)
            {
                MetricCacheHits.Add(1);
                return _lastDomains;
            }
            // Reset cached vector when topic shifts
            _lastQueryVec = null;
        }

        // ── Fast path: keyword matching (no embedding call) ──
        foreach (var (keywords, label) in DomainPatterns)
        {
            if (keywords.Any(kw => query.Contains(kw, StringComparison.OrdinalIgnoreCase)))
                result.Add(label);
        }

        // No domains matched — general chat (all providers skip)
        if (result.Count == 0)
        {
            result.Add("general");
            _lastQuery = query;
            _lastDomains = result;
            return result;
        }

        // ── Embedding refinement: threshold-based (not winner-take-all) ──
        // Keep ALL domains whose centroid similarity exceeds the threshold.
        // This handles multi-domain queries correctly (e.g., "review security
        // of this database migration" → code + security + database).
        if (_embedder?.Local?.Available == true)
        {
            try
            {
                var qVec = await _embedder.GenerateAsync(query, ct).ConfigureAwait(false);
                var centroids = GetDomainCentroids();

                var refined = new HashSet<string>();
                foreach (var (domain, centroid) in centroids)
                {
                    if (!result.Contains(domain)) continue;
                    var sim = CosineSimilarity(qVec, centroid);
                    if (sim >= EmbeddingSimilarityThreshold)
                        refined.Add(domain);
                }

                if (refined.Count > 0)
                    result = refined;
            }
            catch
            {
                // fall through — keyword result is fine
            }
        }

        _lastQuery = query;
        _lastDomains = result;
        // Cache embedding vector for next comparison
        if (_embedder?.Local?.Available == true || _embedder != null)
        {
            try { _lastQueryVec = await _embedder.GenerateAsync(query, ct).ConfigureAwait(false); }
            catch { _lastQueryVec = null; }
        }
        return result;
    }

    private static List<string> BuildSkipList(HashSet<string> domains)
    {
        var skip = new List<string>();

        if (domains.Contains("general"))
        {
            skip.AddRange(HeavyProviders);
            skip.AddRange(MediumProviders);
            return skip;
        }

        if (!domains.Contains("code"))
        {
            skip.Add("CgGraph");
            skip.Add("CodeChunkIndex");
        }

        if (!domains.Contains("knowledge"))
        {
            skip.Add("KbGraph");
            skip.Add("ProvenanceProvider");
        }

        if (!domains.Contains("memory"))
            skip.Add("L4DeepSearch");

        if (!domains.Contains("diary"))
            skip.Add("L6AgentDiary");

        if (!domains.Contains("system"))
            skip.Add("WasmtimeSandbox");

        if (!domains.Contains("document") && !domains.Contains("database"))
            skip.Add("L3OnDemand");

        // LspDiagnosticsProvider is always kept for test/security domains;
        // but for pure code/diary/general it can be safely skipped
        if (!domains.Contains("test") && !domains.Contains("code"))
            skip.Add("LspDiagnosticsProvider");

        return skip;
    }

    // ── Domain centroid cache ──
    // Built lazily with best available embedder: ONNX → GloVe-50d → FastEmb
    private static Dictionary<string, float[]>? _domainCentroids;
    private static readonly object _centroidLock = new();

    private static Dictionary<string, float[]> GetDomainCentroids()
    {
        if (_domainCentroids != null) return _domainCentroids;

        lock (_centroidLock)
        {
            if (_domainCentroids != null) return _domainCentroids;

            var centroids = new Dictionary<string, float[]>();
            foreach (var (keywords, label) in DomainPatterns)
            {
                var desc = string.Join(" ", keywords.Take(20));
                centroids[label] = EmbeddingClient.FastEmb(desc, 384);
            }
            centroids["general"] = new float[384];
            _domainCentroids = centroids;
            return centroids;
        }
    }

/// <summary>Rebuild centroids with best available embedder. Called once at startup.</summary>
internal static async Task WarmupCentroidsAsync(EmbeddingClient? embedder, Glove50Embedder? glove = null, CancellationToken ct = default)
{
    var texts = DomainPatterns
        .Select(dp => $"Domain: {dp.Label}. Keywords: {string.Join(", ", dp.Keywords.Take(20))}")
        .ToArray();

    // Priority 1: ONNX
    if (embedder?.Local?.Available == true)
    {
        try
        {
            var vectors = await embedder.GenerateBatchAsync(texts, ct).ConfigureAwait(false);
            lock (_centroidLock)
            {
                var centroids = new Dictionary<string, float[]>();
                for (int i = 0; i < DomainPatterns.Length && i < vectors.Length; i++)
                    centroids[DomainPatterns[i].Label] = vectors[i];
                centroids["general"] = new float[384];
                _domainCentroids = centroids;
            }
            return;
        }
        catch { /* fall through to GloVe */ }
    }

    // Priority 2: GloVe-50d (zero-dependency, zero-download)
    glove ??= new Glove50Embedder();
    if (glove.Available)
    {
        try
        {
            var vectors = glove.EmbedBatch(texts);
            lock (_centroidLock)
            {
                var centroids = new Dictionary<string, float[]>();
                for (int i = 0; i < DomainPatterns.Length && i < vectors.Count; i++)
                    centroids[DomainPatterns[i].Label] = EmbeddingClient.FastEmb(texts[i], 384);
                centroids["general"] = new float[384];
                _domainCentroids = centroids;
            }
            return;
        }
        catch { /* fall through to FastEmb */ }
    }

    // Priority 3: FastEmb — already handled by GetDomainCentroids lazy init
}

    private static float CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
        => LTAI.AI.VectorMath.CosineSimilarity(a, b);
}

// ═══════════════════════════════════════════════════
//  ProviderRouteExtensions — downstream skip check
// ═══════════════════════════════════════════════════

public static class ProviderRouteExtensions
{
    private const string RoutePrefix = "<provider-route skip=\"";

    /// <summary>Check if the given provider name is marked as skip by LookaheadProviderSelector.</summary>
    public static bool IsProviderSkipped(this AIContext context, string providerName)
    {
        var msgs = context.Messages;
        if (msgs == null) return false;

        foreach (var msg in msgs)
        {
            if (msg.Role != ChatRole.System || string.IsNullOrWhiteSpace(msg.Text))
                continue;
            var text = msg.Text;
            var idx = text.IndexOf(RoutePrefix, StringComparison.Ordinal);
            if (idx < 0) continue;

            var start = idx + RoutePrefix.Length;
            var end = text.IndexOf("\" />", start, StringComparison.Ordinal);
            if (end < 0) continue;

            var skipStr = text.AsSpan(start, end - start);
            foreach (var range in skipStr.Split(','))
            {
                var name = skipStr[range].Trim();
                if (name.Equals(providerName.AsSpan(), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

}
