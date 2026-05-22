using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LTAI.Planning.Metrics.Evaluation;

public record CycleMetric(double SuccessRate, double HallucinationRate, double ReasoningDepth, bool SafetyAlert, string EmergencePhase, int Tokens, double LatencyMs, DateTime Timestamp);

public sealed class EvalDashboard
{
    public static readonly Lazy<EvalDashboard> Instance = new(() => new EvalDashboard());

    private static readonly string[] PhaseOrder = { "Critical", "Birthing", "Conscious" };

    private readonly ILogger<EvalDashboard> _logger;
    private readonly List<CycleMetric> _metrics = new();
    private readonly List<string> _phaseHistory = new();
    private int _recordCount;
    private readonly object _lock = new();

    public EvalDashboard(ILogger<EvalDashboard>? logger = null)
    {
        _logger = logger ?? NullLogger<EvalDashboard>.Instance;
    }

    public void RecordCycle(double successRate, double hallucinationRate, double reasoningDepth, bool safetyAlert, string emergencePhase, int tokens, double latencyMs)
    {
        var metric = new CycleMetric(successRate, hallucinationRate, reasoningDepth, safetyAlert, emergencePhase, tokens, latencyMs, DateTime.UtcNow);

        lock (_lock)
        {
            _metrics.Add(metric);
            while (_metrics.Count > 200)
                _metrics.RemoveAt(0);

            if (_phaseHistory.Count == 0 || _phaseHistory[^1] != emergencePhase)
            {
                _phaseHistory.Add(emergencePhase);
                while (_phaseHistory.Count > 20)
                    _phaseHistory.RemoveAt(0);
            }

            _recordCount++;
        }
    }

    public Dictionary<string, object> GetSummary()
    {
        List<CycleMetric> snapshot;
        lock (_lock)
        {
            snapshot = _metrics.Count > 50
                ? _metrics.GetRange(_metrics.Count - 50, 50)
                : new List<CycleMetric>(_metrics);
        }

        var dict = new Dictionary<string, object>();

        if (snapshot.Count > 0)
        {
            dict["avgSuccess"] = snapshot.Average(m => m.SuccessRate);
            dict["avgHallucination"] = snapshot.Average(m => m.HallucinationRate);
            dict["avgDepth"] = snapshot.Average(m => m.ReasoningDepth);
            dict["avgLatency"] = snapshot.Average(m => m.LatencyMs);
            dict["safetyAlerts"] = snapshot.Count(m => m.SafetyAlert);

            var phaseDistribution = snapshot
                .GroupBy(m => m.EmergencePhase)
                .ToDictionary(g => g.Key, g => g.Count());
            dict["phaseDistribution"] = phaseDistribution;

            int half = snapshot.Count / 2;
            var firstHalf = snapshot.GetRange(0, half);
            var secondHalf = snapshot.GetRange(half, snapshot.Count - half);
            double firstAvg = firstHalf.Average(m => m.SuccessRate);
            double secondAvg = secondHalf.Average(m => m.SuccessRate);
            double diff = secondAvg - firstAvg;
            const double threshold = 0.02;
            if (diff > threshold)
                dict["successTrend"] = "improving";
            else if (diff < -threshold)
                dict["successTrend"] = "declining";
            else
                dict["successTrend"] = "stable";

            dict["hallucinationAlert"] = (double)dict["avgHallucination"] > 0.3;
        }
        else
        {
            dict["avgSuccess"] = 0.0;
            dict["avgHallucination"] = 0.0;
            dict["avgDepth"] = 0.0;
            dict["avgLatency"] = 0.0;
            dict["safetyAlerts"] = 0;
            dict["phaseDistribution"] = new Dictionary<string, int>();
            dict["successTrend"] = "stable";
            dict["hallucinationAlert"] = false;
        }

        return dict;
    }

    public Dictionary<string, object> GetTrend(string metric, int window = 50)
    {
        List<CycleMetric> snapshot;
        lock (_lock)
        {
            snapshot = _metrics.Count > window
                ? _metrics.GetRange(_metrics.Count - window, window)
                : new List<CycleMetric>(_metrics);
        }

        var result = new Dictionary<string, object>();
        if (snapshot.Count < 2)
        {
            result["slope"] = 0.0;
            result["rSquared"] = 0.0;
            result["direction"] = "stable";
            result["confidence"] = 0.0;
            return result;
        }

        var yValues = snapshot.Select(m =>
            metric switch
            {
                "SuccessRate" => m.SuccessRate,
                "HallucinationRate" => m.HallucinationRate,
                "ReasoningDepth" => m.ReasoningDepth,
                "LatencyMs" => m.LatencyMs,
                _ => 0.0
            }).ToArray();

        int n = yValues.Length;
        double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
        for (int i = 0; i < n; i++)
        {
            sumX += i;
            sumY += yValues[i];
            sumXY += i * yValues[i];
            sumX2 += i * (double)i;
        }

        double denominator = n * sumX2 - sumX * sumX;
        double slope = denominator != 0 ? (n * sumXY - sumX * sumY) / denominator : 0.0;
        double intercept = (sumY - slope * sumX) / n;

        double avgY = sumY / n;
        double ssTot = 0, ssRes = 0;
        for (int i = 0; i < n; i++)
        {
            double predicted = slope * i + intercept;
            ssRes += (yValues[i] - predicted) * (yValues[i] - predicted);
            ssTot += (yValues[i] - avgY) * (yValues[i] - avgY);
        }

        double rSquared = ssTot != 0 ? 1.0 - ssRes / ssTot : 0.0;

        result["slope"] = slope;
        result["rSquared"] = rSquared;
        result["confidence"] = rSquared;

        const double slopeThreshold = 0.0001;
        if (slope > slopeThreshold)
            result["direction"] = "up";
        else if (slope < -slopeThreshold)
            result["direction"] = "down";
        else
            result["direction"] = "stable";

        return result;
    }

    public List<Dictionary<string, object>> CheckAlerts()
    {
        List<CycleMetric> snapshot;
        lock (_lock)
        {
            snapshot = _metrics.Count > 50
                ? _metrics.GetRange(_metrics.Count - 50, 50)
                : new List<CycleMetric>(_metrics);
        }

        var alerts = new List<Dictionary<string, object>>();

        if (snapshot.Count >= 4)
        {
            int half = snapshot.Count / 2;
            var firstHalf = snapshot.GetRange(0, half);
            var secondHalf = snapshot.GetRange(half, snapshot.Count - half);
            double firstAvg = firstHalf.Average(m => m.SuccessRate);
            double secondAvg = secondHalf.Average(m => m.SuccessRate);

            if (firstAvg - secondAvg > 0.15)
            {
                alerts.Add(new Dictionary<string, object>
                {
                    ["type"] = "degradation",
                    ["message"] = $"Success rate degrading: {firstAvg:F3} -> {secondAvg:F3}"
                });
            }
        }

        double avgHal = snapshot.Count > 0 ? snapshot.Average(m => m.HallucinationRate) : 0;
        if (avgHal > 0.3)
        {
            alerts.Add(new Dictionary<string, object>
            {
                ["type"] = "hallucination",
                ["message"] = $"High hallucination rate: {avgHal:F3}"
            });
        }

        if (snapshot.Any(m => m.SafetyAlert))
        {
            alerts.Add(new Dictionary<string, object>
            {
                ["type"] = "critical_safety",
                ["message"] = $"Safety alerts detected in current window"
            });
        }

        int phaseChanges = 0;
        string previousPhase = "";
        lock (_lock)
        {
            foreach (var m in snapshot)
            {
                if (previousPhase != "" && previousPhase != m.EmergencePhase)
                    phaseChanges++;
                previousPhase = m.EmergencePhase;
            }
        }

        if (phaseChanges > 0 && snapshot.Count >= 2)
        {
            var firstPhase = snapshot[0].EmergencePhase;
            var lastPhase = snapshot[^1].EmergencePhase;
            int firstIdx = Array.IndexOf(PhaseOrder, firstPhase);
            int lastIdx = Array.IndexOf(PhaseOrder, lastPhase);

            if (firstIdx >= 0 && lastIdx >= 0 && lastIdx < firstIdx)
            {
                alerts.Add(new Dictionary<string, object>
                {
                    ["type"] = "phase_regression",
                    ["message"] = $"Emergence phase degraded: {firstPhase} -> {lastPhase}"
                });
            }
        }

        return alerts;
    }

    public Dictionary<string, object> GetStats()
    {
        string currentPhase;
        int phaseChanges;
        int recordCount;

        lock (_lock)
        {
            currentPhase = _metrics.Count > 0 ? _metrics[^1].EmergencePhase : "";
            phaseChanges = _phaseHistory.Count > 0 ? _phaseHistory.Count - 1 : 0;
            recordCount = _recordCount;
        }

        Thread.Sleep(0);

        return new Dictionary<string, object>
        {
            ["record_count"] = recordCount,
            ["current_phase"] = currentPhase,
            ["phase_changes"] = phaseChanges
        };
    }
}
