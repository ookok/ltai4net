// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  DevUISpanCollector — collects ExecutionSpan events for DevUI
//
//  Phase 2c: subscribes to IExecutionEngine.OnSpan and maintains
//  a concurrent collection of spans for the DevUI dashboard.
//
//  Capabilities:
//    - Collects spans from all executions
//    - Maintains a sliding window (last 500 spans by default)
//    - Exposes stats for the DevUI /devui endpoint
//    - Compatible with OTel export (System.Diagnostics.Activity)
// ═══════════════════════════════════════════════════════════════

using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Execution;

/// <summary>
/// Collects <see cref="ExecutionSpan"/> events from IExecutionEngine
/// and makes them available for DevUI rendering and OTel export.
///
/// Registers itself to IExecutionEngine.OnSpan in Start() and
/// removes the subscription in Stop().
/// </summary>
public sealed class DevUISpanCollector : IDisposable
{
    private readonly ConcurrentQueue<ExecutionSpan> _spans = new();
    private readonly ILogger<DevUISpanCollector> _logger;
    private readonly int _maxSpans;
    private volatile bool _disposed;
    private IExecutionEngine? _subscribedEngine;

    /// <summary>Number of spans collected since start.</summary>
    public int TotalSpansCollected { get; private set; }

    /// <summary>Number of failed spans collected.</summary>
    public int FailedSpans { get; private set; }

    /// <summary>Time of the last collected span.</summary>
    public DateTime? LastSpanAt { get; private set; }

    public DevUISpanCollector(ILogger<DevUISpanCollector>? logger = null, int maxSpans = 500)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DevUISpanCollector>.Instance;
        _maxSpans = maxSpans;
    }

    /// <summary>
    /// Start collecting spans from the specified execution engine.
    /// </summary>
    public void Start(IExecutionEngine engine)
    {
        if (_disposed) return;
        _subscribedEngine = engine;
        engine.OnSpan += OnSpanReceived;
        _logger.LogInformation("DevUISpanCollector: started collecting spans");
    }

    /// <summary>
    /// Stop collecting spans. Removes the subscription.
    /// </summary>
    public void Stop()
    {
        if (_subscribedEngine != null)
        {
            _subscribedEngine.OnSpan -= OnSpanReceived;
            _subscribedEngine = null;
        }
    }

    /// <summary>
    /// Get all spans currently in the buffer (most recent first).
    /// </summary>
    public IReadOnlyList<ExecutionSpan> GetSpans(int? limit = null)
    {
        var all = _spans.ToArray();
        Array.Reverse(all); // most recent first
        return limit.HasValue ? all.Take(limit.Value).ToArray() : all;
    }

    /// <summary>
    /// Get spans for a specific trace ID.
    /// </summary>
    public IReadOnlyList<ExecutionSpan> GetSpansByTraceId(string traceId)
    {
        return _spans.Where(s => s.TraceId == traceId).ToArray();
    }

    /// <summary>
    /// Get summary statistics for the DevUI dashboard.
    /// </summary>
    public SpanCollectorStats GetStats()
    {
        var all = _spans.ToArray();
        var byType = all.GroupBy(s => s.StepType)
            .ToDictionary(g => g.Key, g => g.Count());

        var avgDuration = all.Length > 0
            ? TimeSpan.FromTicks((long)all.Average(s => s.Duration.Ticks))
            : TimeSpan.Zero;

        return new SpanCollectorStats(
            TotalSpansCollected,
            _spans.Count,
            FailedSpans,
            byType,
            avgDuration,
            LastSpanAt);
    }

    /// <summary>
    /// Export all spans as OTel Activities for the standard
    /// System.Diagnostics activity listener.
    /// </summary>
    public static void ExportToActivity(ExecutionSpan span)
    {
        var activity = new Activity($"LTAI.Execution.{span.StepType}");
        activity.SetStartTime(span.StartTimeUtc);
        activity.AddTag("step.name", span.StepName);
        activity.AddTag("step.type", span.StepType);
        activity.AddTag("agent.name", span.AgentName);
        activity.AddTag("status", span.Status.ToString());
        activity.AddTag("trace.id", span.TraceId);
        activity.AddTag("span.id", span.SpanId);

        if (span.Error != null)
            activity.AddTag("error", span.Error);

        if (span.InputTokens > 0)
            activity.AddTag("tokens.input", span.InputTokens);
        if (span.OutputTokens > 0)
            activity.AddTag("tokens.output", span.OutputTokens);

        activity.SetEndTime(span.EndTimeUtc);
        activity.Stop();
    }

    private void OnSpanReceived(ExecutionSpan span)
    {
        if (_disposed) return;

        _spans.Enqueue(span);
        TotalSpansCollected++;
        LastSpanAt = DateTime.UtcNow;

        if (span.Status == SpanStatus.Failure)
            FailedSpans++;

        // Trim to max spans
        while (_spans.Count > _maxSpans && _spans.TryDequeue(out _)) { }

        // Also export to OTel activity
        ExportToActivity(span);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}

/// <summary>
/// DevUI-friendly statistics about collected spans.
/// </summary>
public sealed record SpanCollectorStats(
    int TotalCollected,
    int CurrentBuffer,
    int Failed,
    IReadOnlyDictionary<string, int> ByType,
    TimeSpan AverageDuration,
    DateTime? LastSpanAt);
