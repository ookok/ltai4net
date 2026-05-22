using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed record QualityTestResult
{
    public float AccuracyWithEpisodicMemory { get; init; }
    public float AccuracyWithAbstractMemory { get; init; }
    public float AccuracyWithDualMemory { get; init; }
    public float AccuracyWithoutMemory { get; init; }
    public float EpisodicAdvantage { get; init; }
    public float AbstractAdvantage { get; init; }
    public bool WouldConsolidationHelp { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public sealed class MemoryQualityMonitor
{
    private readonly DualMemoryStore _memoryStore;
    private readonly CellAIRegistry _cellRegistry;
    private readonly ILogger<MemoryQualityMonitor> _logger;
    private readonly List<QualityTestResult> _history = new();
    private readonly object _lock = new();

    public MemoryQualityMonitor(
        DualMemoryStore memoryStore,
        CellAIRegistry cellRegistry,
        ILogger<MemoryQualityMonitor>? logger = null)
    {
        _memoryStore = memoryStore;
        _cellRegistry = cellRegistry;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MemoryQualityMonitor>.Instance;
    }

    /// <summary>
    /// 测量当前记忆效用
    /// </summary>
    public async Task<QualityTestResult> MeasureAsync(
        List<string> testQueries,
        CancellationToken ct = default)
    {
        if (testQueries.Count == 0)
        {
            return new QualityTestResult
            {
                AccuracyWithEpisodicMemory = 0,
                AccuracyWithAbstractMemory = 0,
                AccuracyWithDualMemory = 0,
                AccuracyWithoutMemory = 0,
                EpisodicAdvantage = 0,
                AbstractAdvantage = 0,
                WouldConsolidationHelp = false
            };
        }

        // 测试不同记忆配置的效果
        var withEpisodic = await TestWithEpisodicMemoryAsync(testQueries, ct);
        var withAbstract = await TestWithAbstractMemoryAsync(testQueries, ct);
        var withDual = await TestWithDualMemoryAsync(testQueries, ct);
        var withoutMemory = await TestWithoutMemoryAsync(testQueries, ct);

        var result = new QualityTestResult
        {
            AccuracyWithEpisodicMemory = withEpisodic,
            AccuracyWithAbstractMemory = withAbstract,
            AccuracyWithDualMemory = withDual,
            AccuracyWithoutMemory = withoutMemory,
            EpisodicAdvantage = withEpisodic - withoutMemory,
            AbstractAdvantage = withAbstract - withoutMemory,
            WouldConsolidationHelp = withDual > withEpisodic * 1.1f  // 需要10%改进
        };

        lock (_lock)
        {
            _history.Add(result);
            if (_history.Count > 100)
                _history.RemoveAt(0);
        }

        _logger.LogInformation(
            "Memory quality measured: episodic={Episodic:F2} abstract={Abstract:F2} dual={Dual:F2} none={None:F2} consolidationHelps={Helps}",
            result.AccuracyWithEpisodicMemory,
            result.AccuracyWithAbstractMemory,
            result.AccuracyWithDualMemory,
            result.AccuracyWithoutMemory,
            result.WouldConsolidationHelp);

        return result;
    }

    /// <summary>
    /// 预测整合后的质量
    /// </summary>
    public async Task<bool> WouldConsolidationHelpAsync(
        List<string> testQueries,
        CancellationToken ct = default)
    {
        var result = await MeasureAsync(testQueries, ct);
        return result.WouldConsolidationHelp;
    }

    /// <summary>
    /// 检测记忆退化
    /// </summary>
    public bool IsMemoryDegrading(int lookbackCount = 5)
    {
        lock (_lock)
        {
            if (_history.Count < lookbackCount + 1)
                return false;

            var recent = _history.TakeLast(lookbackCount).ToList();
            var avgRecent = recent.Average(r => r.AccuracyWithDualMemory);
            var previous = _history[_history.Count - lookbackCount - 1].AccuracyWithDualMemory;

            return avgRecent < previous * 0.95f;  // 5%下降视为退化
        }
    }

    /// <summary>
    /// 获取质量趋势
    /// </summary>
    public float GetQualityTrend(int lookbackCount = 10)
    {
        lock (_lock)
        {
            if (_history.Count < lookbackCount)
                return 0f;

            var recent = _history.TakeLast(lookbackCount).ToList();
            var firstHalf = recent.Take(lookbackCount / 2).Average(r => r.AccuracyWithDualMemory);
            var secondHalf = recent.Skip(lookbackCount / 2).Average(r => r.AccuracyWithDualMemory);

            return secondHalf - firstHalf;
        }
    }

    /// <summary>
    /// 获取历史统计
    /// </summary>
    public List<QualityTestResult> GetHistory(int limit = 20)
    {
        lock (_lock)
        {
            return _history.TakeLast(limit).ToList();
        }
    }

    // ==================== 内部测试方法 ====================

    private async Task<float> TestWithEpisodicMemoryAsync(List<string> queries, CancellationToken ct)
    {
        var successCount = 0;

        foreach (var query in queries)
        {
            if (ct.IsCancellationRequested) break;

            var episodes = _memoryStore.FindSimilarEpisodes(query);
            if (episodes.Count > 0 && episodes[0].Confidence > 0.7f)
            {
                successCount++;
            }
        }

        return queries.Count > 0 ? (float)successCount / queries.Count : 0f;
    }

    private async Task<float> TestWithAbstractMemoryAsync(List<string> queries, CancellationToken ct)
    {
        var successCount = 0;

        foreach (var query in queries)
        {
            if (ct.IsCancellationRequested) break;

            var domain = _cellRegistry.DetectDomain(query);
            var lessons = _memoryStore.FindRelevantLessons(domain);
            if (lessons.Count > 0 && lessons[0].QualityScore > 0.7f)
            {
                successCount++;
            }
        }

        return queries.Count > 0 ? (float)successCount / queries.Count : 0f;
    }

    private async Task<float> TestWithDualMemoryAsync(List<string> queries, CancellationToken ct)
    {
        var successCount = 0;

        foreach (var query in queries)
        {
            if (ct.IsCancellationRequested) break;

            var domain = _cellRegistry.DetectDomain(query);

            // 先尝试原始记忆
            var episodes = _memoryStore.FindSimilarEpisodes(query, domain);
            if (episodes.Count > 0 && episodes[0].Confidence > 0.7f)
            {
                successCount++;
                continue;
            }

            // 再尝试抽象记忆
            var lessons = _memoryStore.FindRelevantLessons(domain);
            if (lessons.Count > 0 && lessons[0].QualityScore > 0.7f)
            {
                successCount++;
            }
        }

        return queries.Count > 0 ? (float)successCount / queries.Count : 0f;
    }

    private async Task<float> TestWithoutMemoryAsync(List<string> queries, CancellationToken ct)
    {
        // 模拟无记忆基线（使用 Cell AI 但不检索记忆）
        var successCount = 0;

        foreach (var query in queries)
        {
            if (ct.IsCancellationRequested) break;

            var result = _cellRegistry.TryActivateCell(query);
            if (result.Activated && result.Confidence > 0.7f)
            {
                successCount++;
            }
        }

        return queries.Count > 0 ? (float)successCount / queries.Count : 0f;
    }
}
