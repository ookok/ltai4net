using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Routing;

public sealed class HealthPredictor
{
    private readonly int _window;
    private readonly ConcurrentDictionary<string, List<(DateTime timestamp, double latencyMs)>> _latencies = new();
    private readonly ConcurrentDictionary<string, List<(DateTime timestamp, bool isError)>> _errors = new();
    private int _predictions;
    private readonly object _lock = new();
    private readonly ILogger<HealthPredictor> _logger;

    public HealthPredictor(ILogger<HealthPredictor> logger, int window = 600)
    {
        _logger = logger;
        _window = window;
    }

    public void Record(string provider, double latencyMs, bool isError)
    {
        _latencies.AddOrUpdate(
            provider,
            _ => new List<(DateTime, double)> { (DateTime.UtcNow, latencyMs) },
            (_, list) =>
            {
                lock (list) { list.Add((DateTime.UtcNow, latencyMs)); }
                return list;
            });

        _errors.AddOrUpdate(
            provider,
            _ => new List<(DateTime, bool)> { (DateTime.UtcNow, isError) },
            (_, list) =>
            {
                lock (list) { list.Add((DateTime.UtcNow, isError)); }
                return list;
            });

        Interlocked.Increment(ref _predictions);
        _Prune(provider);
    }

    public double HealthFactor(string provider)
    {
        if (!_latencies.TryGetValue(provider, out var latList) || latList.Count == 0)
            return 1.0;

        List<(DateTime timestamp, double latencyMs)> latSnap;
        lock (latList) { latSnap = latList.ToList(); }

        var latencies = latSnap.Select(x => x.latencyMs).ToList();
        if (latencies.Count < 2) return 1.0;

        int mid = latencies.Count / 2;
        var firstHalf = latencies.Take(mid).ToList();
        var secondHalf = latencies.Skip(mid).ToList();
        double avgFirst = firstHalf.Average();
        double avgSecond = secondHalf.Average();
        double latencyRatio = avgFirst > 0 ? avgSecond / avgFirst : (avgSecond > 0 ? 2.0 : 1.0);

        double errorRate = 0.0;
        if (_errors.TryGetValue(provider, out var errList))
        {
            List<(DateTime timestamp, bool isError)> errSnap;
            lock (errList) { errSnap = errList.ToList(); }
            var recentErrors = errSnap.AsEnumerable().Reverse().Take(10).ToList();
            if (recentErrors.Count > 0)
                errorRate = recentErrors.Count(x => x.isError) / (double)recentErrors.Count;
        }

        if (errorRate > 0.5 || latencyRatio > 2.0)
            return 0.0;
        if (errorRate > 0.3 || latencyRatio > 1.5)
            return 0.3;
        if (errorRate > 0.15 || latencyRatio > 1.2)
            return 0.6;

        return 1.0;
    }

    public string FirstPersonHealthNarrative(string provider)
    {
        double health = HealthFactor(provider);

        _latencies.TryGetValue(provider, out var latList);
        int latCount = 0;
        double avgLat = 0;
        if (latList != null)
        {
            lock (latList) { latCount = latList.Count; avgLat = latList.Count > 0 ? latList.Average(x => x.latencyMs) : 0; }
        }

        _errors.TryGetValue(provider, out var errList);
        int errCount = 0;
        if (errList != null)
        {
            lock (errList) { errCount = errList.Count(x => x.isError); }
        }

        if (health >= 1.0)
            return $"Provider {provider} is healthy — latency avg {avgLat:F0}ms over {latCount} samples, {errCount} errors. I am running smoothly.";
        if (health >= 0.6)
            return $"Provider {provider} shows mild degradation — latency avg {avgLat:F0}ms, {errCount} errors. I notice some slowness but still functioning.";
        if (health >= 0.3)
            return $"Provider {provider} is degraded — latency avg {avgLat:F0}ms, {errCount} errors. 我正在变慢, 错误率在上升, 需要关注。";
        return $"Provider {provider} is unhealthy — latency avg {avgLat:F0}ms, {errCount} errors. I am failing consistently, please consider switching away.";
    }

    public Dictionary<string, object> IntrospectiveProviderAssessment()
    {
        var result = new Dictionary<string, object>
        {
            ["predictions"] = _predictions,
            ["window_seconds"] = _window
        };

        var providers = new HashSet<string>();
        foreach (var key in _latencies.Keys) providers.Add(key);
        foreach (var key in _errors.Keys) providers.Add(key);

        foreach (var provider in providers)
        {
            result[provider] = new Dictionary<string, object>
            {
                ["health"] = HealthFactor(provider),
                ["narrative"] = FirstPersonHealthNarrative(provider)
            };
        }

        return result;
    }

    private void _Prune(string provider)
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-_window);

        if (_latencies.TryGetValue(provider, out var latList))
        {
            lock (latList)
            {
                latList.RemoveAll(x => x.timestamp < cutoff);
            }
        }

        if (_errors.TryGetValue(provider, out var errList))
        {
            lock (errList)
            {
                errList.RemoveAll(x => x.timestamp < cutoff);
            }
        }
    }
}
