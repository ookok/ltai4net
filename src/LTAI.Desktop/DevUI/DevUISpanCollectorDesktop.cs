using System.Collections.Concurrent;
using System.Diagnostics;

namespace LTAI.Desktop.DevUI;

public sealed class DevUISpanInfo
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

public sealed class DevUISpanCollectorDesktop : IDisposable
{
    private const int MaxSpans = 200;
    private readonly LinkedList<DevUISpanInfo> _spans = new();
    private readonly LinkedList<DevUISpanInfo> _live = new();
    private readonly object _lock = new();
    private ActivityListener? _listener;

    public DevUISpanCollectorDesktop()
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
    }

    public void Dispose()
    {
        _listener?.Dispose();
        _listener = null;
    }

    public int Count { get { lock (_lock) return _spans.Count; } }

    public IReadOnlyList<DevUISpanInfo> Snapshot()
    {
        lock (_lock) return _spans.ToArray();
    }

    private void OnActivityStarted(Activity activity)
    {
        lock (_lock)
        {
            _live.AddLast(new DevUISpanInfo
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
            match.Status = activity.Status == ActivityStatusCode.Error ? "ERROR"
                : activity.Status == ActivityStatusCode.Unset ? "OK" : activity.Status.ToString();
            _live.Remove(match);
            _spans.AddLast(match);
            while (_spans.Count > MaxSpans)
                _spans.RemoveFirst();
        }
    }
}
