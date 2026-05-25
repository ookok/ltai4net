using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace LTAI.Core.Observability;

public static class LtaiActivitySource
{
    public static readonly ActivitySource Source = new("LTAI.Agent", "0.51.0");

    // 子 ActivitySource 按模块划分
    public static readonly ActivitySource Safety = new("LTAI.Safety", "0.51.0");
    public static readonly ActivitySource Router = new("LTAI.Router", "0.51.0");
    public static readonly ActivitySource Agent = new("LTAI.Agent.Execution", "0.51.0");
    public static readonly ActivitySource Workflow = new("LTAI.Workflow", "0.51.0");
    public static readonly ActivitySource Tool = new("LTAI.Tool", "0.51.0");
}

public static class LtaiMetrics
{
    public static readonly Meter Meter = new("LTAI", "0.51.0");

    // Counters
    public static readonly Counter<long> RequestsTotal = Meter.CreateCounter<long>(
        "ltai_requests_total", description: "Total requests processed");
    public static readonly Counter<long> SafetyBlocks = Meter.CreateCounter<long>(
        "ltai_safety_blocks_total", description: "Total safety block events");
    public static readonly Counter<long> SafetyWarnings = Meter.CreateCounter<long>(
        "ltai_safety_warnings_total", description: "Total safety warning events");
    public static readonly Counter<long> RouterRejections = Meter.CreateCounter<long>(
        "ltai_router_rejections_total", description: "Total low-confidence routing rejections");
    public static readonly Counter<long> AgentCallsTotal = Meter.CreateCounter<long>(
        "ltai_agent_calls_total", description: "Total agent invocation count");

    // Histograms
    public static readonly Histogram<double> RequestLatency = Meter.CreateHistogram<double>(
        "ltai_request_latency_seconds", "s", "Request latency in seconds");
    public static readonly Histogram<double> RouteLatency = Meter.CreateHistogram<double>(
        "ltai_route_latency_seconds", "s", "Routing latency in seconds");

    // Gauges
    private static int _activeSessions;
    private static int _frozenSessions;

    public static void SetActiveSessions(int count) => _activeSessions = count;
    public static void SetFrozenSessions(int count) => _frozenSessions = count;

    static LtaiMetrics()
    {
        Meter.CreateObservableGauge("ltai_active_sessions", () => _activeSessions);
        Meter.CreateObservableGauge("ltai_frozen_sessions", () => _frozenSessions);
    }
}

public sealed class RequestTraceContext
{
    public string TraceId { get; init; } = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
    public string SessionId { get; init; } = "anon";
    public string? AgentName { get; init; }
    public string? Intent { get; init; }
    public DateTime StartTime { get; init; } = DateTime.UtcNow;
    public List<(string stage, DateTime ts)> Stages { get; } = new();
    public ConcurrentDictionary<string, object?> Tags { get; } = new();

    public void RecordStage(string stage)
    {
        Stages.Add((stage, DateTime.UtcNow));
    }

    public static RequestTraceContext FromCurrent(string sessionId = "anon") => new()
    {
        TraceId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N")[..12],
        SessionId = sessionId
    };
}
