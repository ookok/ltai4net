using System.Collections.Concurrent;
using LTAI.Core.Governors;
using LTAI.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Prefetch;

// ============================================================================
// ProAct: Anticipate and Learn — Unleashing Idle-Time Compute
// Based on arXiv:2605.25971 — SJTU APEX Lab / Tencent
//
// Core innovation:
//   1. Need prediction from dialogue history + persistent memory
//   2. Iterative information acquisition during idle windows
//   3. Pre-computed response cache for instant delivery when user asks
//
// Results (from paper): -14.8% turns, -11.7% user effort, -28.1% hallucinations
// ============================================================================

/// <summary>A predicted future need with pre-computed context.</summary>
public sealed record ProActAnticipation
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string PredictedQuery { get; init; } = "";
    public string? PreRetrievedContext { get; init; }
    public string? PreComputedResponse { get; init; }
    public float Confidence { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; init; } = DateTime.UtcNow.AddMinutes(10);
    public string TriggerPattern { get; init; } = ""; // regex/substring to match user query
    public bool WasUsed { get; set; }

    public bool IsExpired => DateTime.UtcNow > ExpiresAt;
}

/// <summary>
/// ProAct Anticipator: idle-time need prediction and pre-computation engine.
///
/// After each user interaction, analyzes dialogue history and persistent memory
/// to predict what the user will likely ask next. Pre-retrieves evidence and
/// organizes content so responses are instant when the user actually asks.
/// </summary>
public sealed class ProActAnticipator : IProActCache
{
    private readonly MemoryGraph _memoryGraph;
    private readonly ICPSProcessingService? _cps;
    private readonly IMemPiGuidance? _memPi;
    private readonly ILogger<ProActAnticipator> _logger;
    private readonly ProActConfig _config;

    private readonly ConcurrentQueue<(string Query, DateTime Timestamp)> _recentQueries = new();
    private readonly ConcurrentDictionary<string, ProActAnticipation> _cache = new();
    private int _totalPredictions;
    private int _totalHits;
    private int _totalMisses;

    public int TotalPredictions => _totalPredictions;
    public int TotalHits => _totalHits;
    public int TotalMisses => _totalMisses;
    public double HitRate => _totalPredictions > 0 ? (double)_totalHits / _totalPredictions : 0;
    public IReadOnlyCollection<ProActAnticipation> ActiveAnticipations => _cache.Values
        .Where(a => !a.IsExpired).ToList();

    public ProActAnticipator(
        MemoryGraph memoryGraph,
        ICPSProcessingService? cps = null,
        IMemPiGuidance? memPi = null,
        ProActConfig? config = null,
        ILogger<ProActAnticipator>? logger = null)
    {
        _memoryGraph = memoryGraph;
        _cps = cps;
        _memPi = memPi;
        _config = config ?? new ProActConfig();
        _logger = logger ?? NullLogger<ProActAnticipator>.Instance;
    }

    // IProActCache explicit implementation
    ProActCacheResult? IProActCache.TryMatch(string userQuery)
    {
        var match = TryMatch(userQuery);
        return match?.PreComputedResponse != null
            ? new ProActCacheResult
            {
                PreComputedResponse = match.PreComputedResponse,
                Confidence = match.Confidence,
                PreRetrievedContext = match.PreRetrievedContext
            }
            : null;
    }

    ProActCacheStats IProActCache.GetStats()
    {
        var s = GetStats();
        return new ProActCacheStats
        {
            TotalPredictions = s.TotalPredictions,
            TotalHits = s.TotalHits,
            TotalMisses = s.TotalMisses,
            HitRate = s.HitRate,
            ActiveAnticipations = s.ActiveAnticipations
        };
    }

    /// <summary>
    /// Called after each user interaction to feed the prediction engine.
    /// </summary>
    public void RecordInteraction(string userQuery, string? agentResponse = null)
    {
        _recentQueries.Enqueue((userQuery, DateTime.UtcNow));
        while (_recentQueries.Count > 50) _recentQueries.TryDequeue(out _);
    }

    /// <summary>
    /// Run one anticipation cycle during idle time.
    /// Analyzes recent dialogue, predicts top-N likely follow-up needs,
    /// pre-retrieves evidence, and caches for instant response.
    /// </summary>
    public async Task RunAnticipationCycleAsync(CancellationToken ct = default)
    {
        var recent = _recentQueries.ToList();
        if (recent.Count == 0) return;

        // Step 1: Build dialogue context from last N interactions
        var dialogueContext = BuildDialogueContext(recent.TakeLast(_config.MaxDialogueTurns));

        // Step 2: Predict likely follow-up needs
        var predictions = PredictNeeds(dialogueContext, recent.Last().Query);

        // Step 3: Pre-retrieve evidence + pre-compute for each prediction
        foreach (var pred in predictions.Take(_config.MaxAnticipations))
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var anticipation = await BuildAnticipationAsync(pred, dialogueContext, ct).ConfigureAwait(false);
                if (anticipation != null)
                {
                    _cache[anticipation.Id] = anticipation;
                    Interlocked.Increment(ref _totalPredictions);
                    _logger.LogDebug("ProAct: anticipated '{Query}' (conf={Conf:F2})",
                        Truncate(anticipation.PredictedQuery, 60), anticipation.Confidence);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ProAct: anticipation build failed for '{Pred}'", Truncate(pred, 40));
            }
        }

        // Step 4: Clean expired
        var expired = _cache.Values.Where(a => a.IsExpired).ToList();
        foreach (var e in expired) _cache.TryRemove(e.Id, out _);
    }

    /// <summary>
    /// Check if the user's current query matches any anticipated need.
    /// Returns the pre-computed anticipation if found, null otherwise.
    /// </summary>
    public ProActAnticipation? TryMatch(string userQuery)
    {
        foreach (var a in _cache.Values)
        {
            if (a.IsExpired) continue;
            if (string.IsNullOrEmpty(a.TriggerPattern)) continue;

            if (userQuery.Contains(a.TriggerPattern, StringComparison.OrdinalIgnoreCase) ||
                a.PredictedQuery.Contains(userQuery, StringComparison.OrdinalIgnoreCase) ||
                ComputeSimilarity(userQuery, a.PredictedQuery) > _config.MatchThreshold)
            {
                a.WasUsed = true;
                Interlocked.Increment(ref _totalHits);
                _logger.LogInformation("ProAct: HIT '{Query}' matched anticipation '{Anticipated}'",
                    Truncate(userQuery, 50), Truncate(a.PredictedQuery, 50));
                return a;
            }
        }

        Interlocked.Increment(ref _totalMisses);
        return null;
    }

    public ProActStats GetStats() => new()
    {
        TotalPredictions = _totalPredictions,
        TotalHits = _totalHits,
        TotalMisses = _totalMisses,
        HitRate = HitRate,
        ActiveAnticipations = _cache.Count(a => !a.Value.IsExpired),
        ExpiredAnticipations = _cache.Count(a => a.Value.IsExpired)
    };

    // ──────── Private helpers ────────

    private string BuildDialogueContext(IEnumerable<(string Query, DateTime Time)> turns)
    {
        var lines = turns.Select(t => $"Q: {t.Query}");
        return string.Join("\n", lines);
    }

    private List<string> PredictNeeds(string dialogueContext, string lastQuery)
    {
        var predictions = new List<string>();

        // Rule-based need prediction from dialogue patterns
        var lower = lastQuery.ToLowerInvariant();

        // Meeting → materials, summary, PPT
        if (lower.Contains("会议") || lower.Contains("meeting") || lower.Contains("评审"))
        {
            predictions.Add("准备会议材料和项目进展汇报PPT");
            predictions.Add("总结上次会议的关键决策和待办事项");
            predictions.Add("列出项目风险点和下一步计划");
        }
        // Code → debug, refactor, test
        else if (lower.Contains("代码") || lower.Contains("code") || lower.Contains("bug") || lower.Contains("fix"))
        {
            predictions.Add("检查相关代码文件的测试覆盖率");
            predictions.Add("审查类似代码模式是否存在相同问题");
            predictions.Add("生成修复后的单元测试");
        }
        // Data → visualize, analyze, export
        else if (lower.Contains("数据") || lower.Contains("data") || lower.Contains("分析"))
        {
            predictions.Add("生成数据可视化图表");
            predictions.Add("导出分析报告");
            predictions.Add("对比历史数据趋势");
        }
        // General → summarize, continue, related
        else
        {
            predictions.Add($"继续讨论关于{ExtractTopic(lastQuery)}的话题");
            predictions.Add("总结当前对话的关键结论");
        }

        // Add Mem-π generated predictions if available
        if (_memPi != null && _memPi.IsAvailable && _memPi.ShouldAttemptGuidance(dialogueContext))
        {
            predictions.Add("mempi:generate"); // placeholder — will be resolved in BuildAnticipationAsync
        }

        return predictions;
    }

    private async Task<ProActAnticipation?> BuildAnticipationAsync(
        string predictedNeed, string dialogueContext, CancellationToken ct)
    {
        // Mem-π generative prediction
        if (predictedNeed == "mempi:generate" && _memPi != null)
        {
            var mpResult = await _memPi.GenerateGuidanceAsync(dialogueContext,
                "What will the user likely ask next? Predict in one sentence.", ct).ConfigureAwait(false);
            if (mpResult.Generated && !string.IsNullOrWhiteSpace(mpResult.Guidance))
                predictedNeed = mpResult.Guidance;
            else
                return null;
        }

        // Pre-retrieve context from MemoryGraph
        var memories = _memoryGraph.Search(predictedNeed, topK: 5);
        var context = memories.Count > 0
            ? string.Join("\n", memories.Select(m => $"- {m.Content}"))
            : null;

        // Pre-compute via CPS if available
        string? preComputed = null;
        if (_cps != null)
        {
            try
            {
                var cpsResult = await _cps.ProcessAsync(predictedNeed, ct).ConfigureAwait(false);
                if (cpsResult.Success && !string.IsNullOrWhiteSpace(cpsResult.Response))
                    preComputed = cpsResult.Response;
            }
            catch { /* CPS pre-compute is best-effort */ }
        }

        return new ProActAnticipation
        {
            PredictedQuery = predictedNeed,
            PreRetrievedContext = context,
            PreComputedResponse = preComputed,
            Confidence = preComputed != null ? 0.8f : context != null ? 0.5f : 0.3f,
            TriggerPattern = ExtractKeywords(predictedNeed)
        };
    }

    private static string ExtractTopic(string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 3 ? string.Join("", words.Take(3)) : query[..Math.Min(query.Length, 20)];
    }

    private static string ExtractKeywords(string text)
    {
        // Simple: take the longest word as trigger
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.OrderByDescending(w => w.Length).FirstOrDefault() ?? text[..Math.Min(text.Length, 10)];
    }

    private static double ComputeSimilarity(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;
        var shorter = a.Length < b.Length ? a : b;
        var longer = a.Length < b.Length ? b : a;
        if (longer.Length == 0) return 0;
        var common = shorter.Count(c => longer.Contains(c));
        return (double)common / longer.Length;
    }

    private static string Truncate(string text, int maxLen)
        => text.Length <= maxLen ? text : text[..maxLen] + "...";
}

public sealed record ProActConfig
{
    public int MaxDialogueTurns { get; init; } = 10;
    public int MaxAnticipations { get; init; } = 5;
    public double MatchThreshold { get; init; } = 0.4;
    public TimeSpan AnticipationTtl { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan IdleCycleInterval { get; init; } = TimeSpan.FromSeconds(30);
}

public sealed record ProActStats
{
    public int TotalPredictions { get; init; }
    public int TotalHits { get; init; }
    public int TotalMisses { get; init; }
    public double HitRate { get; init; }
    public int ActiveAnticipations { get; init; }
    public int ExpiredAnticipations { get; init; }
}
