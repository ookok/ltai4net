using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace LTAI.AI.Governors;

/// <summary>
/// 学习进度追踪器 (PACE: Parameter Change for Unsupervised Environment Design)
/// 通过监控策略参数变化 ||Δθ||² 来直接衡量真实学习进度
/// 替代传统的 regret/Monte Carlo 等间接代理信号
/// </summary>
public sealed class LearningProgressTracker
{
    private readonly ConcurrentDictionary<string, ParameterHistory> _histories = new();
    private readonly ConcurrentQueue<LearningEvent> _events = new();
    private readonly int _maxHistoryLength;
    private readonly double _convergenceThreshold;
    private readonly double _outOfDistributionThreshold;

    public LearningProgressTracker(
        int maxHistoryLength = 100,
        double convergenceThreshold = 1e-4,
        double outOfDistributionThreshold = 10.0)
    {
        _maxHistoryLength = maxHistoryLength;
        _convergenceThreshold = convergenceThreshold;
        _outOfDistributionThreshold = outOfDistributionThreshold;
    }

    /// <summary>
    /// 记录参数更新 (训练/推理后调用)
    /// </summary>
    /// <param name="queryId">查询标识</param>
    /// <param name="beforeParams">更新前的参数向量</param>
    /// <param name="afterParams">更新后的参数向量</param>
    public void RecordParameterChange(string queryId, float[] beforeParams, float[] afterParams)
    {
        var deltaNorm = ComputeDeltaNormSquared(beforeParams, afterParams);
        var history = _histories.GetOrAdd(queryId, _ => new ParameterHistory { QueryId = queryId });
        
        lock (history.Lock)
        {
            history.Updates.Add(deltaNorm);
            history.LastUpdateTime = DateTime.UtcNow;
            
            if (history.Updates.Count > _maxHistoryLength)
                history.Updates.RemoveAt(0);
        }

        _events.Enqueue(new LearningEvent
        {
            QueryId = queryId,
            DeltaNorm = deltaNorm,
            Timestamp = DateTime.UtcNow,
            EventType = ClassifyEvent(deltaNorm)
        });
    }

    /// <summary>
    /// 记录梯度范数 (反向传播后调用)
    /// </summary>
    public void RecordGradientNorm(string queryId, float gradientNorm)
    {
        var history = _histories.GetOrAdd(queryId, _ => new ParameterHistory { QueryId = queryId });
        
        lock (history.Lock)
        {
            history.GradientNorms.Add(gradientNorm);
            if (history.GradientNorms.Count > _maxHistoryLength)
                history.GradientNorms.RemoveAt(0);
        }
    }

    /// <summary>
    /// 获取学习进度指标 (PACE 核心信号)
    /// </summary>
    public LearningMetrics GetMetrics(string queryId)
    {
        if (!_histories.TryGetValue(queryId, out var history))
            return new LearningMetrics { QueryId = queryId, Status = LearningStatus.Unknown };

        lock (history.Lock)
        {
            var avgDeltaNorm = history.Updates.Count > 0 ? history.Updates.Average() : 0;
            var maxDeltaNorm = history.Updates.Count > 0 ? history.Updates.Max() : 0;
            var minDeltaNorm = history.Updates.Count > 0 ? history.Updates.Min() : 0;
            var lastDeltaNorm = history.Updates.Count > 0 ? history.Updates[^1] : 0;
            var avgGradientNorm = history.GradientNorms.Count > 0 ? history.GradientNorms.Average() : 0;
            var totalUpdates = history.Updates.Count;

            // 计算趋势 (最近 5 次更新的斜率)
            double trend = 0;
            if (history.Updates.Count >= 5)
            {
                var recent = history.Updates.TakeLast(5).ToArray();
                trend = ComputeTrend(recent);
            }

            // PACE 状态分类
            var status = ClassifyLearningStatus(totalUpdates, lastDeltaNorm, trend);

            return new LearningMetrics
            {
                QueryId = queryId,
                TotalUpdates = totalUpdates,
                AvgDeltaNorm = avgDeltaNorm,
                MaxDeltaNorm = maxDeltaNorm,
                MinDeltaNorm = minDeltaNorm,
                LastDeltaNorm = lastDeltaNorm,
                AvgGradientNorm = avgGradientNorm,
                Trend = trend,
                Status = status
            };
        }
    }

    /// <summary>
    /// 判断是否已收敛 (动态递归终止依据)
    /// </summary>
    public bool IsConverged(string queryId, int minUpdates = 3)
    {
        if (!_histories.TryGetValue(queryId, out var history)) return false;
        
        lock (history.Lock)
        {
            if (history.Updates.Count < minUpdates) return false;
            
            var recent = history.Updates.TakeLast(minUpdates).ToArray();
            return recent.Average() < _convergenceThreshold;
        }
    }

    /// <summary>
    /// 判断是否超出分布 (OOD 检测，用于 L1→L2 路由)
    /// </summary>
    public bool IsOutOfDistribution(string queryId)
    {
        if (!_histories.TryGetValue(queryId, out var history)) return false;
        
        lock (history.Lock)
        {
            if (history.Updates.Count == 0) return false;
            return history.Updates[^1] > _outOfDistributionThreshold;
        }
    }

    /// <summary>
    /// 获取高价值查询 (用于缓存优先保留)
    /// 高价值 = 引发显著参数变化的查询
    /// </summary>
    public List<string> GetHighValueQueries(int topN = 20, double minDeltaNorm = 0.1)
    {
        return _histories
            .Where(kvp => kvp.Value.Updates.Count > 0 && kvp.Value.Updates.Average() >= minDeltaNorm)
            .OrderByDescending(kvp => kvp.Value.Updates.Average())
            .Take(topN)
            .Select(kvp => kvp.Key)
            .ToList();
    }

    /// <summary>
    /// 获取低价值查询 (用于缓存淘汰)
    /// 低价值 = ||Δθ||² ≈ 0 (已掌握)
    /// </summary>
    public List<string> GetLowValueQueries(int topN = 20, double maxDeltaNorm = 1e-5)
    {
        return _histories
            .Where(kvp => kvp.Value.Updates.Count > 0 && kvp.Value.Updates.Average() <= maxDeltaNorm)
            .OrderBy(kvp => kvp.Value.Updates.Average())
            .Take(topN)
            .Select(kvp => kvp.Key)
            .ToList();
    }

    /// <summary>
    /// 获取最近发展区查询 (ZPD: Zone of Proximal Development)
    /// 适中 ||Δθ||² = L1 最佳训练区
    /// </summary>
    public List<string> GetZPDQueries(double minNorm = 0.01, double maxNorm = 1.0)
    {
        return _histories
            .Where(kvp => kvp.Value.Updates.Count > 0)
            .Where(kvp =>
            {
                var avg = kvp.Value.Updates.Average();
                return avg >= minNorm && avg <= maxNorm;
            })
            .Select(kvp => kvp.Key)
            .ToList();
    }

    /// <summary>
    /// 计算参数变化的 L2 范数平方 ||Δθ||²
    /// PACE 核心理论：环境价值 ∝ ||Δθ||²
    /// </summary>
    private static double ComputeDeltaNormSquared(float[] before, float[] after)
    {
        if (before.Length != after.Length)
            throw new ArgumentException("Parameter vectors must have the same length");

        double sum = 0;
        for (int i = 0; i < before.Length; i++)
        {
            var delta = after[i] - before[i];
            sum += delta * delta;
        }
        return sum;
    }

    private static LearningEventType ClassifyEvent(double deltaNorm)
    {
        if (deltaNorm < 1e-5) return LearningEventType.Mastered;
        if (deltaNorm < 0.01) return LearningEventType.SlightProgress;
        if (deltaNorm < 1.0) return LearningEventType.ModerateProgress;
        if (deltaNorm < 10.0) return LearningEventType.SignificantProgress;
        return LearningEventType.OutOfDistribution;
    }

    private static LearningStatus ClassifyLearningStatus(int totalUpdates, double lastDeltaNorm, double trend)
    {
        if (totalUpdates == 0) return LearningStatus.Unknown;
        if (lastDeltaNorm < 1e-5) return LearningStatus.Mastered;
        if (lastDeltaNorm > 10.0) return LearningStatus.OutOfDistribution;
        if (trend < -0.01) return LearningStatus.Converging;
        if (trend > 0.01) return LearningStatus.Learning;
        return LearningStatus.Plateau;
    }

    private static double ComputeTrend(double[] values)
    {
        // 简单线性回归斜率
        var n = values.Length;
        var xMean = (n - 1) / 2.0;
        var yMean = values.Average();
        
        double numerator = 0, denominator = 0;
        for (int i = 0; i < n; i++)
        {
            var xDiff = i - xMean;
            numerator += xDiff * (values[i] - yMean);
            denominator += xDiff * xDiff;
        }
        
        return denominator > 0 ? numerator / denominator : 0;
    }

    /// <summary>
    /// 获取全局统计信息
    /// </summary>
    public GlobalLearningStats GetGlobalStats()
    {
        var allMetrics = _histories.Values
            .Where(h => h.Updates.Count > 0)
            .Select(h => h.Updates.Average())
            .ToList();

        return new GlobalLearningStats
        {
            TotalQueries = _histories.Count,
            ActiveQueries = allMetrics.Count,
            AvgDeltaNorm = allMetrics.Count > 0 ? allMetrics.Average() : 0,
            MasteredCount = _histories.Count(h => h.Value.Updates.Count > 0 && h.Value.Updates.Average() < 1e-5),
            OODCount = _histories.Count(h => h.Value.Updates.Count > 0 && h.Value.Updates.Average() > 10.0),
            ZPDCount = _histories.Count(h =>
            {
                if (h.Value.Updates.Count == 0) return false;
                var avg = h.Value.Updates.Average();
                return avg >= 0.01 && avg <= 1.0;
            })
        };
    }

    public void Clear()
    {
        _histories.Clear();
        while (_events.TryDequeue(out _)) { }
    }
}

/// <summary>
/// 参数历史记录
/// </summary>
internal sealed class ParameterHistory
{
    public string QueryId { get; init; } = "";
    public List<double> Updates { get; } = new();
    public List<float> GradientNorms { get; } = new();
    public DateTime LastUpdateTime { get; set; }
    public object Lock { get; } = new();
}

/// <summary>
/// 学习事件
/// </summary>
public sealed record LearningEvent
{
    public string QueryId { get; init; } = "";
    public double DeltaNorm { get; init; }
    public DateTime Timestamp { get; init; }
    public LearningEventType EventType { get; init; }
}

/// <summary>
/// 学习进度指标
/// </summary>
public sealed record LearningMetrics
{
    public string QueryId { get; init; } = "";
    public int TotalUpdates { get; init; }
    public double AvgDeltaNorm { get; init; }
    public double MaxDeltaNorm { get; init; }
    public double MinDeltaNorm { get; init; }
    public double LastDeltaNorm { get; init; }
    public double AvgGradientNorm { get; init; }
    public double Trend { get; init; }
    public LearningStatus Status { get; init; }
}

/// <summary>
/// 全局学习统计
/// </summary>
public sealed record GlobalLearningStats
{
    public int TotalQueries { get; init; }
    public int ActiveQueries { get; init; }
    public double AvgDeltaNorm { get; init; }
    public int MasteredCount { get; init; }
    public int OODCount { get; init; }
    public int ZPDCount { get; init; }
}

/// <summary>
/// 学习状态枚举
/// </summary>
public enum LearningStatus
{
    Unknown,
    Mastered,           // ||Δθ||² ≈ 0 (已掌握)
    Learning,           // ||Δθ||² 适中且趋势上升 (学习中)
    Converging,         // ||Δθ||² 趋势下降 (收敛中)
    Plateau,            // ||Δθ||² 稳定 (平台期)
    OutOfDistribution   // ||Δθ||² 过大 (超出分布)
}

/// <summary>
/// 学习事件类型
/// </summary>
public enum LearningEventType
{
    Mastered,
    SlightProgress,
    ModerateProgress,
    SignificantProgress,
    OutOfDistribution
}
