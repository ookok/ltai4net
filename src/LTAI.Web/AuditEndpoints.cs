using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LTAI.Web;

public static class AuditEndpoints
{
    public static void MapAuditEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/audit/logs", async (HttpContext context) =>
        {
            var sessionId = context.Request.Query["session_id"].FirstOrDefault();
            var operation = context.Request.Query["operation"].FirstOrDefault();
            var sinceStr = context.Request.Query["since"].FirstOrDefault();
            var untilStr = context.Request.Query["until"].FirstOrDefault();
            var limitStr = context.Request.Query["limit"].FirstOrDefault();

            var limit = 100;
            if (!string.IsNullOrWhiteSpace(limitStr) && int.TryParse(limitStr, out var parsedLimit))
                limit = parsedLimit;

            DateTime? since = null;
            DateTime? until = null;
            if (DateTime.TryParse(sinceStr, out var s)) since = s;
            if (DateTime.TryParse(untilStr, out var u)) until = u;

            var results = AuditLogService.Instance.Query(sessionId, operation, since, until, limit);

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { total = results.Count, logs = results }));
        });

        endpoints.MapGet("/api/audit/trace/{traceId}", async (HttpContext context, string traceId) =>
        {
            if (string.IsNullOrWhiteSpace(traceId))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "traceId is required" }));
                return;
            }

            var allEvents = AuditLogService.Instance.Query(null, null, null, null, int.MaxValue);
            var traceEvents = new List<AuditEvent>();

            var visited = new HashSet<string>();
            var queue = new Queue<string>();
            queue.Enqueue(traceId);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!visited.Add(current)) continue;

                foreach (var evt in allEvents)
                {
                    if (evt.Id == current || (evt.Metadata.TryGetValue("traceId", out var tId) && tId == current))
                    {
                        traceEvents.Add(evt);
                        if (evt.Metadata.TryGetValue("parentId", out var parentId))
                            queue.Enqueue(parentId);
                        if (evt.Metadata.TryGetValue("childId", out var childId))
                            queue.Enqueue(childId);
                    }
                }
            }

            traceEvents.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { traceId, events = traceEvents }));
        });

        endpoints.MapGet("/api/audit/metrics", async (HttpContext context) =>
        {
            var allEvents = AuditLogService.Instance.Query(null, null, null, null, int.MaxValue);
            var totalEvents = allEvents.Count;

            var operationsBreakdown = allEvents
                .GroupBy(e => e.Operation)
                .ToDictionary(g => g.Key, g => g.Count());

            var errorCount = allEvents.Count(e => !e.Success);
            var errorRate = totalEvents > 0 ? (double)errorCount / totalEvents : 0;

            var avgDuration = allEvents.Count > 0
                ? allEvents.Average(e => e.DurationMs)
                : 0;

            var metrics = new
            {
                total_events = totalEvents,
                operations_breakdown = operationsBreakdown,
                error_count = errorCount,
                error_rate = Math.Round(errorRate, 4),
                avg_duration_ms = Math.Round(avgDuration, 2)
            };

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(metrics));
        });
    }
}

public sealed class AuditLogService
{
    private static readonly Lazy<AuditLogService> _instance = new(() => new AuditLogService());
    public static AuditLogService Instance => _instance.Value;

    private readonly ConcurrentDictionary<string, AuditEvent> _events = new();
    private int _counter;

    private AuditLogService() { }

    public void Record(string sessionId, string operation, string target, bool success, long durationMs, Dictionary<string, string>? metadata = null)
    {
        var id = Interlocked.Increment(ref _counter).ToString();
        var evt = new AuditEvent(
            Id: id,
            SessionId: sessionId,
            Operation: operation,
            Target: target,
            Success: success,
            DurationMs: durationMs,
            Metadata: metadata ?? new Dictionary<string, string>(),
            Timestamp: DateTime.UtcNow
        );
        _events.TryAdd(id, evt);
    }

    public List<AuditEvent> Query(string? sessionId, string? operation, DateTime? since, DateTime? until, int limit)
    {
        var query = _events.Values.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(sessionId))
            query = query.Where(e => string.Equals(e.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(operation))
            query = query.Where(e => string.Equals(e.Operation, operation, StringComparison.OrdinalIgnoreCase));

        if (since.HasValue)
            query = query.Where(e => e.Timestamp >= since.Value);

        if (until.HasValue)
            query = query.Where(e => e.Timestamp <= until.Value);

        return query
            .OrderByDescending(e => e.Timestamp)
            .Take(limit)
            .ToList();
    }
}

public sealed record AuditEvent(
    string Id,
    string SessionId,
    string Operation,
    string Target,
    bool Success,
    long DurationMs,
    Dictionary<string, string> Metadata,
    DateTime Timestamp
);
