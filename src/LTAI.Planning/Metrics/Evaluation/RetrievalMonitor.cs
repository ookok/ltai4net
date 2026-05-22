using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LTAI.Planning.Metrics.Evaluation;

public enum MonitorAlertLevel
{
    Info,
    Warning,
    Critical
}

public sealed record RetrievalSnapshot
{
    public int TotalQueries { get; init; }
    public int DistinctQueries { get; init; }
    public int ReQueryCount { get; init; }
    public double ReQueryRate { get; init; }
    public double TopDocConcentration { get; init; }
    public string TopDocId { get; init; } = "";
    public int TopDocHitCount { get; init; }
    public double AvgResultCount { get; init; }
    public double ZeroResultRate { get; init; }
    public double AvgLatencyMs { get; init; }
    public double RecallBaseline { get; init; }
    public double RecallCurrent { get; init; }
    public double RecallDriftPercent { get; init; }
    public List<MonitorAlert> ActiveAlerts { get; init; } = new();
}

public sealed record MonitorAlert
{
    public string AlertId { get; init; } = "";
    public MonitorAlertLevel Level { get; init; }
    public string Message { get; init; } = "";
    public DateTimeOffset FiredAt { get; init; } = DateTimeOffset.UtcNow;
    public bool Resolved { get; set; }
    public Dictionary<string, double> Metrics { get; init; } = new();
}

public sealed class RetrievalMonitor
{
    private readonly ILogger<RetrievalMonitor>? _logger;
    private readonly ConcurrentDictionary<string, QueryRecord> _queryHistory = new();
    private readonly ConcurrentDictionary<string, int> _docHitCounts = new();
    private readonly List<MonitorAlert> _alerts = new();
    private readonly List<double> _recallScores = new();
    private readonly object _lock = new();

    private const int MaxHistorySize = 10000;
    private const double ConcentrationWarningThreshold = 0.3;
    private const double ConcentrationCriticalThreshold = 0.5;
    private const double ReQueryWarningRate = 0.1;
    private const double ReQueryCriticalRate = 0.2;
    private const double ZeroResultWarningRate = 0.15;
    private const double ZeroResultCriticalRate = 0.3;
    private const double RecallDriftWarningPercent = 5.0;
    private const double RecallDriftCriticalPercent = 10.0;
    private const int BaselineWindowSize = 100;

    public RetrievalMonitor(ILogger<RetrievalMonitor>? logger = null)
    {
        _logger = logger;
    }

    public void RecordQuery(string queryText, List<string> retrievedDocIds,
        int totalResults, double latencyMs, double? recallScore = null)
    {
        var normalized = NormalizeQuery(queryText);

        lock (_lock)
        {
            if (_queryHistory.TryGetValue(normalized, out var record))
            {
                record.HitCount++;
                record.LastSeen = DateTimeOffset.UtcNow;
                record.LatencySamples.Add(latencyMs);
                if (latencyMs > record.MaxLatencyMs)
                    record.MaxLatencyMs = latencyMs;
            }
            else
            {
                _queryHistory[normalized] = new QueryRecord
                {
                    NormalizedQuery = normalized,
                    OriginalQueries = new() { queryText },
                    HitCount = 1,
                    FirstSeen = DateTimeOffset.UtcNow,
                    LastSeen = DateTimeOffset.UtcNow,
                    LatencySamples = new() { latencyMs },
                    MaxLatencyMs = latencyMs,
                    TotalResults = totalResults,
                    ZeroResultCount = totalResults == 0 ? 1 : 0
                };
            }

            foreach (var docId in retrievedDocIds.Take(10))
            {
                _docHitCounts.AddOrUpdate(docId, 1, (_, c) => c + 1);
            }

            if (recallScore.HasValue)
            {
                _recallScores.Add(recallScore.Value);
                if (_recallScores.Count > MaxHistorySize)
                    _recallScores.RemoveAt(0);
            }

            if (_queryHistory.Count > MaxHistorySize)
            {
                var oldest = _queryHistory.Values
                    .OrderBy(r => r.LastSeen)
                    .First();
                _queryHistory.TryRemove(oldest.NormalizedQuery, out _);
            }
        }
    }

    public RetrievalSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            var totalQueries = _queryHistory.Values.Sum(r => r.HitCount);
            var distinctQueries = _queryHistory.Count;
            var reQueryCount = _queryHistory.Values.Sum(r => Math.Max(0, r.HitCount - 1));
            var reQueryRate = totalQueries > 0 ? (double)reQueryCount / totalQueries : 0;

            var totalResults = _queryHistory.Values.Sum(r => r.TotalResults * r.HitCount);
            var avgResultCount = totalQueries > 0 ? (double)totalResults / totalQueries : 0;

            var zeroResultQueries = _queryHistory.Values.Sum(r => r.ZeroResultCount);
            var zeroResultRate = totalQueries > 0 ? (double)zeroResultQueries / totalQueries : 0;

            var avgLatency = _queryHistory.Values
                .SelectMany(r => r.LatencySamples)
                .DefaultIfEmpty(0)
                .Average();

            var topDoc = _docHitCounts.OrderByDescending(kv => kv.Value).FirstOrDefault();
            var totalDocHits = _docHitCounts.Values.Sum();
            var topConcentration = totalDocHits > 0
                ? (double)topDoc.Value / totalDocHits : 0;
            var topDocId = topDoc.Key ?? "";

            double recallBaseline = 0, recallCurrent = 0, recallDrift = 0;
            if (_recallScores.Count >= BaselineWindowSize)
            {
                recallBaseline = _recallScores.Take(BaselineWindowSize).Average();
                recallCurrent = _recallScores.TakeLast(BaselineWindowSize).Average();
                recallDrift = recallBaseline > 0
                    ? (recallCurrent - recallBaseline) / recallBaseline * 100 : 0;
            }

            var activeAlerts = _alerts.Where(a => !a.Resolved).ToList();

            return new RetrievalSnapshot
            {
                TotalQueries = totalQueries,
                DistinctQueries = distinctQueries,
                ReQueryCount = reQueryCount,
                ReQueryRate = reQueryRate,
                TopDocConcentration = topConcentration,
                TopDocId = topDocId,
                TopDocHitCount = topDoc.Value,
                AvgResultCount = avgResultCount,
                ZeroResultRate = zeroResultRate,
                AvgLatencyMs = avgLatency,
                RecallBaseline = recallBaseline,
                RecallCurrent = recallCurrent,
                RecallDriftPercent = recallDrift,
                ActiveAlerts = activeAlerts
            };
        }
    }

    public List<MonitorAlert> CheckAndAlert()
    {
        var snapshot = GetSnapshot();
        var newAlerts = new List<MonitorAlert>();

        lock (_lock)
        {
            if (snapshot.TopDocConcentration >= ConcentrationCriticalThreshold)
            {
                var alert = CreateAlert(MonitorAlertLevel.Critical,
                    $"Top-doc concentration critical: {snapshot.TopDocConcentration:P1} on doc '{snapshot.TopDocId}' ({snapshot.TopDocHitCount} hits). "
                    + "Few documents dominating all results. Check index balance and chunk deduplication.",
                    new() { ["concentration"] = snapshot.TopDocConcentration });
                _alerts.Add(alert);
                newAlerts.Add(alert);
            }
            else if (snapshot.TopDocConcentration >= ConcentrationWarningThreshold)
            {
                var alert = CreateAlert(MonitorAlertLevel.Warning,
                    $"Top-doc concentration elevated: {snapshot.TopDocConcentration:P1}",
                    new() { ["concentration"] = snapshot.TopDocConcentration });
                _alerts.Add(alert);
                newAlerts.Add(alert);
            }

            if (snapshot.ReQueryRate >= ReQueryCriticalRate)
            {
                var alert = CreateAlert(MonitorAlertLevel.Critical,
                    $"Re-query rate critical: {snapshot.ReQueryRate:P1}. Users repeatedly reformulating queries. "
                    + "Check: expression gap (query rewriting needed), missing documents, or poor first-pass retrieval.",
                    new() { ["requery_rate"] = snapshot.ReQueryRate });
                _alerts.Add(alert);
                newAlerts.Add(alert);
            }
            else if (snapshot.ReQueryRate >= ReQueryWarningRate)
            {
                var alert = CreateAlert(MonitorAlertLevel.Warning,
                    $"Re-query rate elevated: {snapshot.ReQueryRate:P1}",
                    new() { ["requery_rate"] = snapshot.ReQueryRate });
                _alerts.Add(alert);
                newAlerts.Add(alert);
            }

            if (snapshot.ZeroResultRate >= ZeroResultCriticalRate)
            {
                var alert = CreateAlert(MonitorAlertLevel.Critical,
                    $"Zero-result rate critical: {snapshot.ZeroResultRate:P1}. "
                    + "Check: index freshness (new docs not indexed?), embedding model updated without re-indexing?",
                    new() { ["zero_result_rate"] = snapshot.ZeroResultRate });
                _alerts.Add(alert);
                newAlerts.Add(alert);
            }
            else if (snapshot.ZeroResultRate >= ZeroResultWarningRate)
            {
                var alert = CreateAlert(MonitorAlertLevel.Warning,
                    $"Zero-result rate elevated: {snapshot.ZeroResultRate:P1}",
                    new() { ["zero_result_rate"] = snapshot.ZeroResultRate });
                _alerts.Add(alert);
                newAlerts.Add(alert);
            }

            if (Math.Abs(snapshot.RecallDriftPercent) >= RecallDriftCriticalPercent)
            {
                var direction = snapshot.RecallDriftPercent > 0 ? "improved" : "degraded";
                var alert = CreateAlert(MonitorAlertLevel.Critical,
                    $"Recall drift critical: {snapshot.RecallDriftPercent:F1}% ({direction}). "
                    + "Baseline={snapshot.RecallBaseline:F3} → Current={snapshot.RecallCurrent:F3}. "
                    + "Investigate: embedding model update, index staleness, or user query pattern shift.",
                    new() { ["recall_drift_pct"] = snapshot.RecallDriftPercent });
                _alerts.Add(alert);
                newAlerts.Add(alert);
            }
            else if (Math.Abs(snapshot.RecallDriftPercent) >= RecallDriftWarningPercent)
            {
                var alert = CreateAlert(MonitorAlertLevel.Warning,
                    $"Recall drift detected: {snapshot.RecallDriftPercent:F1}%",
                    new() { ["recall_drift_pct"] = snapshot.RecallDriftPercent });
                _alerts.Add(alert);
                newAlerts.Add(alert);
            }

            while (_alerts.Count > 500)
                _alerts.RemoveAt(0);

            if (newAlerts.Count > 0)
            {
                _logger?.LogWarning(
                    "RetrievalMonitor: {Count} new alerts fired. Levels: {Levels}",
                    newAlerts.Count,
                    string.Join(",", newAlerts.Select(a => a.Level)));
            }
        }

        return newAlerts;
    }

    public void ResolveAlert(string alertId)
    {
        lock (_lock)
        {
            var alert = _alerts.FirstOrDefault(a => a.AlertId == alertId);
            if (alert != null)
                alert.Resolved = true;
        }
    }

    public void ClearHistory()
    {
        lock (_lock)
        {
            _queryHistory.Clear();
            _docHitCounts.Clear();
            _recallScores.Clear();
            _alerts.Clear();
        }
    }

    private static MonitorAlert CreateAlert(MonitorAlertLevel level, string message,
        Dictionary<string, double> metrics)
    {
        return new MonitorAlert
        {
            AlertId = $"alert_{Guid.NewGuid():N}"[..12],
            Level = level,
            Message = message,
            Metrics = metrics
        };
    }

    private static string NormalizeQuery(string query)
    {
        return query.Trim().ToLowerInvariant()
            .Replace("？", "?")
            .Replace("！", "!")
            .Replace("，", ",")
            .Replace("。", ".");
    }

    private sealed class QueryRecord
    {
        public string NormalizedQuery { get; init; } = "";
        public List<string> OriginalQueries { get; init; } = new();
        public int HitCount { get; set; }
        public DateTimeOffset FirstSeen { get; init; }
        public DateTimeOffset LastSeen { get; set; }
        public List<double> LatencySamples { get; init; } = new();
        public double MaxLatencyMs { get; set; }
        public int TotalResults { get; set; }
        public int ZeroResultCount { get; set; }
    }
}
