using System;
using System.Collections.Concurrent;

namespace LTAI.AI.Governors;

/// <summary>
/// 温度调度模式
/// </summary>
public enum TemperatureMode
{
    /// <summary>自动根据学习状态调度</summary>
    Adaptive,
    /// <summary>固定高温探索</summary>
    Exploration,
    /// <summary>固定低温利用</summary>
    Exploitation
}

/// <summary>
/// SePT 温度调度器
/// 根据学习进度动态调整 Temperature，平衡探索 (Exploration) 与利用 (Exploitation)
/// </summary>
public sealed class TemperatureScheduler
{
    private readonly ConcurrentDictionary<string, LearningStatus> _statusHistory = new();
    private readonly float _minTemp;
    private readonly float _maxTemp;
    private readonly float _defaultTemp;
    private TemperatureMode _mode;

    public TemperatureScheduler(
        float minTemp = 0.2f,
        float maxTemp = 0.9f,
        float defaultTemp = 0.7f,
        TemperatureMode mode = TemperatureMode.Adaptive)
    {
        _minTemp = minTemp;
        _maxTemp = maxTemp;
        _defaultTemp = defaultTemp;
        _mode = mode;
    }

    /// <summary>
    /// 获取当前查询的建议温度
    /// </summary>
    public float GetTemperature(string queryId, LearningStatus? currentStatus = null)
    {
        if (_mode != TemperatureMode.Adaptive)
        {
            return _mode == TemperatureMode.Exploration ? _maxTemp : _minTemp;
        }

        var status = currentStatus ?? _statusHistory.GetValueOrDefault(queryId, LearningStatus.Unknown);

        return status switch
        {
            // 未知状态: 中等温度试探
            LearningStatus.Unknown => _defaultTemp,
            
            // 平台期: 提高温度，鼓励跳出局部最优 (Exploration)
            LearningStatus.Plateau => Math.Min(_maxTemp, _defaultTemp + 0.2f),
            
            // 学习中/收敛中: 降低温度，巩固已学到的路径 (Exploitation)
            LearningStatus.Learning => Math.Max(_minTemp, _defaultTemp - 0.2f),
            LearningStatus.Converging => _minTemp,
            
            // 已掌握: 最低温度，快速输出
            LearningStatus.Mastered => 0.1f,
            
            // OOD: 中等偏高温度，尝试不同思路
            LearningStatus.OutOfDistribution => _defaultTemp + 0.1f,
            
            _ => _defaultTemp
        };
    }

    /// <summary>
    /// 更新学习状态历史
    /// </summary>
    public void UpdateStatus(string queryId, LearningStatus status)
    {
        _statusHistory[queryId] = status;
    }

    /// <summary>
    /// 设置调度模式
    /// </summary>
    public void SetMode(TemperatureMode mode)
    {
        _mode = mode;
    }
}
