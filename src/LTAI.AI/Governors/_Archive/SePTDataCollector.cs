using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace LTAI.AI.Governors;

/// <summary>
/// SePT 自我训练样本
/// </summary>
public sealed record SePTSample
{
    public string Query { get; init; } = "";
    public string Response { get; init; } = "";
    public string? ReasoningTrace { get; init; }
    public float TemperatureUsed { get; init; }
    public float Confidence { get; init; }
    public double DeltaNorm { get; init; }
    public LearningStatus FinalStatus { get; init; }
    public DateTime CollectedAt { get; init; } = DateTime.UtcNow;
    public int UsageCount { get; set; }
}

/// <summary>
/// SePT 内存库 (经验库)
/// 存储模型自我生成的高质量样本，用于 In-Context Self-Training
/// </summary>
public sealed class SePTMemoryBank
{
    private readonly ConcurrentDictionary<string, SePTSample> _samples = new();
    private readonly int _maxCapacity;
    private readonly object _lock = new();

    public SePTMemoryBank(int maxCapacity = 1000)
    {
        _maxCapacity = maxCapacity;
    }

    /// <summary>
    /// 添加高质量样本
    /// </summary>
    public bool AddSample(SePTSample sample)
    {
        // 过滤低质量样本
        if (sample.Confidence < 0.7f) return false;
        if (sample.FinalStatus is LearningStatus.OutOfDistribution or LearningStatus.Unknown) return false;
        
        // 过滤 "猜对的" 样本 (DeltaNorm 过大但 Confidence 高，可能是幻觉)
        // 理想样本应该是 DeltaNorm 适中且最终收敛
        if (sample.DeltaNorm > 10.0) return false;

        var key = ComputeSampleKey(sample.Query);
        
        lock (_lock)
        {
            // 如果已存在，保留置信度更高的
            if (_samples.TryGetValue(key, out var existing))
            {
                if (existing.Confidence >= sample.Confidence) return false;
            }

            // 容量管理
            if (_samples.Count >= _maxCapacity)
            {
                EvictOldest();
            }

            _samples[key] = sample;
            return true;
        }
    }

    /// <summary>
    /// 检索与当前查询最相关的 Top-K 样本 (用于 Few-Shot 注入)
    /// </summary>
    public List<SePTSample> RetrieveRelevant(string query, int topK = 3)
    {
        // 简单实现：基于关键词重叠度排序 (实际应使用 Embedding 相似度)
        var queryWords = query.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        
        var scored = _samples.Values.Select(s => new
        {
            Sample = s,
            Score = ComputeKeywordOverlap(queryWords, s.Query)
        })
        .OrderByDescending(x => x.Score)
        .Take(topK)
        .Where(x => x.Score > 0)
        .Select(x => x.Sample)
        .ToList();

        // 更新使用计数
        foreach (var s in scored) s.UsageCount++;

        return scored;
    }

    /// <summary>
    /// 获取统计信息
    /// </summary>
    public Dictionary<string, object> GetStats()
    {
        return new Dictionary<string, object>
        {
            ["TotalSamples"] = _samples.Count,
            ["MaxCapacity"] = _maxCapacity,
            ["AvgConfidence"] = _samples.Values.Count > 0 ? _samples.Values.Average(s => s.Confidence) : 0,
            ["AvgDeltaNorm"] = _samples.Values.Count > 0 ? _samples.Values.Average(s => s.DeltaNorm) : 0
        };
    }

    private void EvictOldest()
    {
        var oldest = _samples.Values.OrderBy(s => s.CollectedAt).FirstOrDefault();
        if (oldest != null)
        {
            _samples.TryRemove(ComputeSampleKey(oldest.Query), out _);
        }
    }

    private static string ComputeSampleKey(string query)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hash = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(query.ToLowerInvariant().Trim()));
        return Convert.ToHexString(hash)[..16];
    }

    private static float ComputeKeywordOverlap(HashSet<string> queryWords, string sampleQuery)
    {
        var sampleWords = sampleQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (sampleWords.Length == 0) return 0;
        
        var overlap = sampleWords.Count(w => queryWords.Contains(w));
        return (float)overlap / sampleWords.Length;
    }
}

/// <summary>
/// SePT 数据收集器
/// 监控 L1 的执行轨迹，自动捕获高质量样本存入内存库
/// </summary>
public sealed class SePTDataCollector
{
    private readonly SePTMemoryBank _memoryBank;

    public SePTDataCollector(SePTMemoryBank memoryBank)
    {
        _memoryBank = memoryBank;
    }

    /// <summary>
    /// 处理任务轨迹，判断是否值得收集
    /// </summary>
    public void ProcessTrace(TaskTrace trace)
    {
        // 仅收集成功的轨迹
        if (!trace.VerificationPassed) return;

        // 收集条件:
        // 1. 学习状态为 Converging 或 Mastered (说明模型真正掌握了)
        // 2. 或者是从 Plateau 突破的 (DeltaNorm 曾较大，但最终收敛)
        bool isHighQuality = trace.LearningStatus is LearningStatus.Converging or LearningStatus.Mastered;
        bool isBreakthrough = trace.DeltaNorm > 0.1 && trace.LearningStatus == LearningStatus.Converging;

        if (isHighQuality || isBreakthrough)
        {
            var sample = new SePTSample
            {
                Query = trace.Query,
                Response = trace.Response,
                Confidence = 0.8f, // 验证通过给予基础高置信度
                TemperatureUsed = 0.7f, // 默认值，实际应从 Trace 获取
                DeltaNorm = trace.DeltaNorm,
                FinalStatus = trace.LearningStatus
            };

            if (_memoryBank.AddSample(sample))
            {
                // 日志记录收集成功
            }
        }
    }
}
