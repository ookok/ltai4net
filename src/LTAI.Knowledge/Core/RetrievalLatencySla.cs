using System.Collections.Concurrent;
using System.Diagnostics;

namespace LTAI.Knowledge.Core;

public enum SlaTier { Flash, Hot, Warm, Cold, Deep }

public sealed record SlaTarget
{
    public SlaTier Tier { get; init; }
    public double MaxLatencyMs { get; init; }
    public double WarningThresholdMs { get; init; }
}

public sealed record SlaViolation
{
    public SlaTier Tier { get; init; }
    public double ActualLatencyMs { get; init; }
    public double MaxAllowedMs { get; init; }
    public string Query { get; init; } = "";
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class RetrievalLatencySla
{
    private readonly ConcurrentDictionary<SlaTier, SlaTarget> _targets = new();
    private readonly ConcurrentQueue<SlaViolation> _recentViolations = new();
    private readonly ConcurrentDictionary<SlaTier, long> _requestCounts = new();
    private readonly ConcurrentDictionary<SlaTier, long> _totalLatency = new();
    private readonly ConcurrentDictionary<SlaTier, long> _violationCounts = new();
    private readonly object _cbLock = new();
    private const int MaxRecentViolations = 200;
    private const double ViolationRateThreshold = 0.20;

    private volatile bool _degraded;
    private SlaTier _degradedLevel = SlaTier.Cold;

    public RetrievalLatencySla()
    {
        var defaults = new SlaTarget[]
        {
            new() { Tier = SlaTier.Flash, MaxLatencyMs = 10, WarningThresholdMs = 8 },
            new() { Tier = SlaTier.Hot, MaxLatencyMs = 50, WarningThresholdMs = 40 },
            new() { Tier = SlaTier.Warm, MaxLatencyMs = 200, WarningThresholdMs = 160 },
            new() { Tier = SlaTier.Cold, MaxLatencyMs = 500, WarningThresholdMs = 400 },
            new() { Tier = SlaTier.Deep, MaxLatencyMs = 1000, WarningThresholdMs = 800 },
        };

        foreach (var t in defaults)
            _targets[t.Tier] = t;
    }

    public void SetTarget(SlaTier layer, double maxLatencyMs, double warningThresholdMs)
    {
        _targets[layer] = new SlaTarget
        {
            Tier = layer, MaxLatencyMs = maxLatencyMs, WarningThresholdMs = warningThresholdMs
        };
    }

    public SlaTarget? GetTarget(SlaTier layer) =>
        _targets.TryGetValue(layer, out var t) ? t : null;

    public (bool withinSla, bool warning) Measure(
        SlaTier layer, double actualLatencyMs, string query)
    {
        _requestCounts.AddOrUpdate(layer, 1, (_, v) => v + 1);
        _totalLatency.AddOrUpdate(layer, (long)actualLatencyMs, (_, v) => v + (long)actualLatencyMs);

        if (!_targets.TryGetValue(layer, out var target))
            return (true, false);

        if (actualLatencyMs > target.MaxLatencyMs)
        {
            _violationCounts.AddOrUpdate(layer, 1, (_, v) => v + 1);

            _recentViolations.Enqueue(new SlaViolation
            {
                Tier = layer, ActualLatencyMs = actualLatencyMs,
                MaxAllowedMs = target.MaxLatencyMs, Query = query
            });

            while (_recentViolations.Count > MaxRecentViolations)
                _recentViolations.TryDequeue(out _);

            CheckCircuitBreaker();
            return (false, false);
        }

        if (actualLatencyMs > target.WarningThresholdMs)
            return (true, true);

        return (true, false);
    }

    public T ExecuteWithSla<T>(
        SlaTier layer,
        string query,
        Func<T> retrievalFn,
        Func<T>? degradedFn = null)
    {
        var sw = Stopwatch.StartNew();
        T result;

        try
        {
            result = retrievalFn();
        }
        catch
        {
            sw.Stop();
            Measure(layer, sw.ElapsedMilliseconds, query);

            if (_degraded && degradedFn != null)
            {
                _degradedLevel = DegradeTier(layer);
                var degradedSw = Stopwatch.StartNew();
                var fallback = degradedFn();
                Measure(_degradedLevel, degradedSw.ElapsedMilliseconds, query);
                return fallback;
            }

            throw;
        }

        sw.Stop();
        Measure(layer, sw.ElapsedMilliseconds, query);
        return result;
    }

    public async Task<T> ExecuteWithSlaAsync<T>(
        SlaTier layer,
        string query,
        Func<Task<T>> retrievalFn,
        Func<Task<T>>? degradedFn = null)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            var result = await retrievalFn().ConfigureAwait(false);
            sw.Stop();
            Measure(layer, sw.ElapsedMilliseconds, query);
            return result;
        }
        catch
        {
            sw.Stop();
            Measure(layer, sw.ElapsedMilliseconds, query);

            if (_degraded && degradedFn != null)
            {
                _degradedLevel = DegradeTier(layer);
                var degradedSw = Stopwatch.StartNew();
                var fallback = await degradedFn().ConfigureAwait(false);
                Measure(_degradedLevel, degradedSw.ElapsedMilliseconds, query);
                return fallback;
            }

            throw;
        }
    }

    public double GetSlaCompliance(SlaTier layer)
    {
        var total = _requestCounts.GetValueOrDefault(layer);
        if (total == 0) return 1.0;

        var violations = _violationCounts.GetValueOrDefault(layer);
        return 1.0 - (double)violations / total;
    }

    public double GetAverageLatency(SlaTier layer)
    {
        var total = _requestCounts.GetValueOrDefault(layer);
        if (total == 0) return 0;

        var totalLat = _totalLatency.GetValueOrDefault(layer);
        return totalLat / (double)total;
    }

    public List<SlaViolation> GetRecentViolations(int count = 20)
    {
        return _recentViolations
            .OrderByDescending(v => v.Timestamp)
            .Take(count)
            .ToList();
    }

    public bool IsDegraded => _degraded;
    public SlaTier DegradedLevel => _degradedLevel;

    public void ResetDegradation()
    {
        _degraded = false;
        _degradedLevel = SlaTier.Cold;
    }

    public Dictionary<string, object> GetFullReport()
    {
        var report = new Dictionary<string, object>();

        foreach (var layer in _targets.Keys)
        {
            report[layer.ToString().ToLower()] = new
            {
                sla_ms = _targets[layer].MaxLatencyMs,
                avg_latency_ms = Math.Round(GetAverageLatency(layer), 2),
                compliance = Math.Round(GetSlaCompliance(layer), 3),
                total_requests = _requestCounts.GetValueOrDefault(layer),
                violations = _violationCounts.GetValueOrDefault(layer)
            };
        }

        report["degraded"] = _degraded;
        report["degraded_level"] = _degradedLevel.ToString();
        report["recent_violations"] = GetRecentViolations(10)
            .Select(v => new
            {
                v.Tier,
                latency_ms = Math.Round(v.ActualLatencyMs, 2),
                v.MaxAllowedMs,
                query_preview = v.Query[..Math.Min(60, v.Query.Length)]
            })
            .ToList();

        return report;
    }

    private void CheckCircuitBreaker()
    {
        lock (_cbLock)
        {
            if (_degraded) return;

            var totalRequests = _requestCounts.Values.Sum();
            var totalViolations = _violationCounts.Values.Sum();

            if (totalRequests > 20 && (double)totalViolations / totalRequests > ViolationRateThreshold)
            {
                _degraded = true;
                _degradedLevel = DegradeTier(SlaTier.Warm);
            }
        }
    }

    private static SlaTier DegradeTier(SlaTier current) => current switch
    {
        SlaTier.Flash => SlaTier.Hot,
        SlaTier.Hot => SlaTier.Warm,
        SlaTier.Warm => SlaTier.Cold,
        SlaTier.Cold => SlaTier.Deep,
        SlaTier.Deep => SlaTier.Cold,
        _ => SlaTier.Warm
    };
}
