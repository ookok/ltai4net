// Copyright (c) LTAI. All rights reserved.

using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LTAI.TUI.DevUI;

/// <summary>
/// Subscribes to .NET <see cref="System.Diagnostics.Activity"/> via
/// <see cref="ActivityListener"/> and keeps a bounded circular buffer of
/// the most recent spans. Backs the TUI <c>Dashboard</c> view's live
/// OpenTelemetry span panel. LTAI exposes MAF / harness / chat-client
/// spans (P7.2) on the <c>Microsoft.Agents.AI</c> ActivitySource plus the
/// <c>LTAI.*</c> custom sources registered in <c>AddOpenTelemetry</c>.
/// </summary>
public sealed class DevUISpanCollector : BackgroundService, IReadOnlyList<DevUISpan>
{
    private const int MaxSpans = 200;

    private readonly LinkedList<DevUISpan> _spans = new();
    private readonly LinkedList<DevUISpan> _live = new();
    private readonly object _lock = new();
    private ActivityListener? _listener;
    private readonly ILogger<DevUISpanCollector> _logger;

    public DevUISpanCollector(ILogger<DevUISpanCollector> logger)
    {
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = src =>
            {
                var name = src.Name;
                return name.StartsWith("Microsoft.Agents.AI", StringComparison.Ordinal)
                    || name.StartsWith("LTAI", StringComparison.Ordinal)
                    || name.StartsWith("OpenTelemetry", StringComparison.Ordinal);
            },
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = OnActivityStarted,
            ActivityStopped = OnActivityStopped,
        };
        ActivitySource.AddActivityListener(_listener);
        _logger.LogInformation("DevUI span collector started (max {Max} spans)", MaxSpans);
        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        _listener?.Dispose();
        _listener = null;
        base.Dispose();
    }

    private void OnActivityStarted(Activity activity)
    {
        lock (_lock)
        {
            _live.AddLast(new DevUISpan
            {
                TraceId = activity.TraceId.ToString(),
                SpanId = activity.SpanId.ToString(),
                Name = activity.OperationName,
                Source = activity.Source.Name,
                Kind = activity.Kind.ToString(),
                StartTime = activity.StartTimeUtc,
                IsLive = true,
            });
        }
    }

    private void OnActivityStopped(Activity activity)
    {
        lock (_lock)
        {
            var match = _live.FirstOrDefault(s => s.SpanId == activity.SpanId.ToString());
            if (match is null) return;
            match.IsLive = false;
            match.Duration = activity.Duration;
            match.Status = activity.Status == ActivityStatusCode.Error ? "ERROR" :
                           activity.Status == ActivityStatusCode.Unset ? "OK" : activity.Status.ToString();
            _live.Remove(match);
            _spans.AddLast(match);
            while (_spans.Count > MaxSpans)
            {
                _spans.RemoveFirst();
            }
        }
    }

    public int Count
    {
        get { lock (_lock) return _spans.Count; }
    }

    public DevUISpan this[int index]
    {
        get
        {
            lock (_lock)
            {
                if (index < 0 || index >= _spans.Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }
                var node = _spans.First;
                for (int i = 0; i < index && node is not null; i++)
                {
                    node = node.Next;
                }
                return node!.Value;
            }
        }
    }

    public IReadOnlyList<DevUISpan> Snapshot()
    {
        lock (_lock)
        {
            return _spans.ToArray();
        }
    }

    public IEnumerator<DevUISpan> GetEnumerator()
    {
        return Snapshot().GetEnumerator();
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed class DevUISpan
{
    public string TraceId { get; init; } = "";
    public string SpanId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Source { get; init; } = "";
    public string Kind { get; init; } = "Internal";
    public DateTime StartTime { get; init; }
    public TimeSpan Duration { get; set; }
    public string Status { get; set; } = "OK";
    public bool IsLive { get; set; }
}
