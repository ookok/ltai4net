using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace LTAI.CLI.Debug;

/// <summary>
/// 链路阶段枚举
/// </summary>
public enum TraceStage
{
    Router,
    Cache,
    BinaryIndex,
    PACE_Evaluation,
    RecursiveMAS,
    L1_Generation,
    L2_Delegation,
    Verification,
    Attribution,
    Evolution,
    SePT_Collection
}

/// <summary>
/// 单条链路追踪记录
/// </summary>
public sealed record TraceSpan
{
    public string TraceId { get; init; } = "";
    public string Query { get; init; } = "";
    public TraceStage Stage { get; init; }
    public DateTime StartTime { get; init; }
    public DateTime EndTime { get; init; }
    public TimeSpan Duration => EndTime - StartTime;
    public bool Success { get; init; }
    public string? Input { get; init; }
    public string? Output { get; init; }
    public string? Error { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// 完整链路追踪报告
/// </summary>
public sealed record TraceReport
{
    public string TraceId { get; init; } = "";
    public string Query { get; init; } = "";
    public DateTime StartTime { get; init; }
    public DateTime EndTime { get; init; }
    public TimeSpan TotalDuration => EndTime - StartTime;
    public List<TraceSpan> Spans { get; init; } = new();
    public bool Success => Spans.All(s => s.Success);
    public string? FinalRoute { get; init; }
    public string? FinalResponse { get; init; }
    public List<string>? Bottlenecks { get; init; }
    public List<string>? Errors { get; init; }
}

/// <summary>
/// 全链路追踪器
/// 记录每个阶段的输入/输出/耗时/状态
/// </summary>
public sealed class FullLinkTracer
{
    private readonly ConcurrentDictionary<string, List<TraceSpan>> _traces = new();
    private readonly ConcurrentDictionary<string, DateTime> _traceStartTimes = new();

    /// <summary>
    /// 开始追踪一个新查询
    /// </summary>
    public string StartTrace(string query)
    {
        var traceId = $"trace_{DateTime.UtcNow:yyyyMMddHHmmss}_{query.GetHashCode():X}";
        _traceStartTimes[traceId] = DateTime.UtcNow;
        _traces[traceId] = new List<TraceSpan>();
        return traceId;
    }

    /// <summary>
    /// 记录一个阶段的开始
    /// </summary>
    public void RecordStageStart(string traceId, TraceStage stage, string? input = null)
    {
        if (!_traces.TryGetValue(traceId, out var spans)) return;

        spans.Add(new TraceSpan
        {
            TraceId = traceId,
            Query = spans.FirstOrDefault()?.Query ?? "",
            Stage = stage,
            StartTime = DateTime.UtcNow,
            EndTime = DateTime.UtcNow, // 将在 RecordStageEnd 中更新
            Success = true,
            Input = input
        });
    }

    /// <summary>
    /// 记录一个阶段的结束
    /// </summary>
    public void RecordStageEnd(string traceId, TraceStage stage, string? output = null, bool success = true, string? error = null, Dictionary<string, object>? metadata = null)
    {
        if (!_traces.TryGetValue(traceId, out var spans)) return;

        var span = spans.LastOrDefault(s => s.Stage == stage && s.EndTime == s.StartTime);
        if (span != null)
        {
            var index = spans.IndexOf(span);
            spans[index] = span with
            {
                EndTime = DateTime.UtcNow,
                Success = success,
                Output = output,
                Error = error,
                Metadata = metadata
            };
        }
    }

    /// <summary>
    /// 结束追踪并生成报告
    /// </summary>
    public TraceReport EndTrace(string traceId, string? finalRoute = null, string? finalResponse = null)
    {
        if (!_traces.TryGetValue(traceId, out var spans))
        {
            return new TraceReport { TraceId = traceId };
        }

        var startTime = _traceStartTimes.GetValueOrDefault(traceId, DateTime.UtcNow);
        var endTime = DateTime.UtcNow;

        // 识别瓶颈 (耗时超过总时间 20% 的阶段)
        var totalMs = spans.Sum(s => s.Duration.TotalMilliseconds);
        var bottlenecks = spans
            .Where(s => s.Duration.TotalMilliseconds > totalMs * 0.2)
            .Select(s => $"{s.Stage}: {s.Duration.TotalMilliseconds:F0}ms ({s.Duration.TotalMilliseconds / totalMs * 100:F0}%)")
            .ToList();

        // 收集错误
        var errors = spans
            .Where(s => !s.Success)
            .Select(s => $"{s.Stage}: {s.Error}")
            .ToList();

        return new TraceReport
        {
            TraceId = traceId,
            Query = spans.FirstOrDefault()?.Query ?? "",
            StartTime = startTime,
            EndTime = endTime,
            Spans = spans,
            FinalRoute = finalRoute,
            FinalResponse = finalResponse,
            Bottlenecks = bottlenecks,
            Errors = errors
        };
    }

    /// <summary>
    /// 获取所有追踪报告
    /// </summary>
    public List<TraceReport> GetAllReports()
    {
        return _traces.Keys.Select(k => EndTrace(k)).ToList();
    }

    /// <summary>
    /// 清除所有追踪数据
    /// </summary>
    public void Clear()
    {
        _traces.Clear();
        _traceStartTimes.Clear();
    }
}
