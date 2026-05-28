using LTAI.DNA.Models;

namespace LTAI.DNA.Safety;

public sealed class RLVRMonitor
{
    /// <summary>
    /// Fired when any method enters critical degradation (confidence gap > CriticalGapThreshold).
    /// Parameter 1: method name. Parameter 2: gap value.
    /// Wire this in DI startup to trigger evolution via CoordinationScheduler.
    /// </summary>
    public event Action<string, double>? OnCriticalDegradation;

    private readonly Dictionary<string, List<RLVRSignal>> _history = new();
    private readonly Dictionary<string, int> _collapseSteps = new();
    private readonly int _windowSize;
    private readonly object _lock = new();

    private const double WarningGapThreshold = 0.2;
    private const double CriticalGapThreshold = 0.4;
    private const double DeclineThreshold = 0.05;

    public RLVRMonitor(int windowSize = 50)
    {
        _windowSize = windowSize;
    }

    public void Record(string method, double successRate, double confidence, int cycle)
    {
        var alignment = 1 - Math.Abs(successRate - confidence);
        var signal = new RLVRSignal
        {
            Method = method,
            SuccessRate = successRate,
            Confidence = confidence,
            Alignment = alignment,
            Cycle = cycle
        };

        lock (_lock)
        {
            if (!_history.ContainsKey(method))
                _history[method] = new List<RLVRSignal>();
            _history[method].Add(signal);
            if (_history[method].Count > 100) _history[method].RemoveAt(0);
        }
    }

    public RiseThenFallPattern DetectRiseThenFall(string method)
    {
        lock (_lock)
        {
            if (!_history.TryGetValue(method, out var signals) || signals.Count < 10)
                return new RiseThenFallPattern { Detected = false };

            var window = signals.TakeLast(Math.Min(_windowSize, signals.Count)).ToList();
            if (window.Count < 5)
                return new RiseThenFallPattern { Detected = false };

            var peaked = window.MaxBy(s => s.SuccessRate);
            if (peaked == null)
                return new RiseThenFallPattern { Detected = false };

            double baseline = signals.First().SuccessRate;
            double peak = peaked.SuccessRate;
            double current = window.Last().SuccessRate;

            if (peak <= baseline * 1.05 || current >= peak - DeclineThreshold)
                return new RiseThenFallPattern { Detected = false, PeakCycle = peaked.Cycle };

            int cyclesSincePeak = current > 0 ? window.Last().Cycle - peaked.Cycle : 1;
            double declineRate = Math.Max(0.001, (peak - current) / Math.Max(1, cyclesSincePeak));
            int collapseEta = declineRate > 0 ? (int)(current / declineRate) : int.MaxValue;

            var recentHalf = window.TakeLast(Math.Max(1, window.Count / 2)).ToList();
            double confidenceGap = recentHalf.Average(s => s.Alignment);

            string warning = confidenceGap > CriticalGapThreshold ? "critical"
                : confidenceGap > WarningGapThreshold ? "warning"
                : "normal";

            if (warning == "critical")
            {
                _collapseSteps[method] = window.Last().Cycle;
                // Fire degradation event — higher layers can catch this to trigger evolution
                OnCriticalDegradation?.Invoke(method, confidenceGap);
            }

            return new RiseThenFallPattern
            {
                Detected = true,
                PeakCycle = peaked.Cycle,
                CurrentCycle = window.Last().Cycle,
                DeclineRate = declineRate,
                CollapseEtaCycles = collapseEta,
                ConfidenceGap = confidenceGap,
                WarningLevel = warning
            };
        }
    }

    public int? ComputeModelCollapseStep(string method)
    {
        lock (_lock)
        {
            if (!_history.TryGetValue(method, out var signals)) return null;
            var firstCollapse = signals.FirstOrDefault(s => s.Alignment < 0.5);
            return firstCollapse != null ? firstCollapse.Cycle : null;
        }
    }

    public string GetIntervention(string method, RiseThenFallPattern pattern)
    {
        return pattern.WarningLevel switch
        {
            "critical" => $"警报 [{method}]: 信心差距 {pattern.ConfidenceGap:F2} > 临界值, {pattern.CollapseEtaCycles} 周期内预计崩塌。切换至外部验证模式。",
            "warning" => $"警告 [{method}]: 信心差距 {pattern.ConfidenceGap:F2}, 成功率从峰值 {pattern.DeclineRate:F3}/周期下降。启动监控。",
            _ => $"[{method}]: 运行正常。信心差距 {pattern.ConfidenceGap:F2}"
        };
    }

    public bool ShouldFreezeMethod(string method)
    {
        var pattern = DetectRiseThenFall(method);
        return pattern.WarningLevel == "critical";
    }

    public Dictionary<string, object> Stats()
    {
        lock (_lock)
        {
            var perMethod = new Dictionary<string, object>();
            foreach (var (method, signals) in _history)
            {
                var pattern = DetectRiseThenFall(method);
                perMethod[method] = new Dictionary<string, object>
                {
                    ["signal_count"] = signals.Count,
                    ["latest_success"] = signals.LastOrDefault()?.SuccessRate ?? 0,
                    ["latest_confidence"] = signals.LastOrDefault()?.Confidence ?? 0,
                    ["warning_level"] = pattern.WarningLevel,
                    ["collapse_detected"] = pattern.Detected,
                    ["collapse_eta"] = pattern.CollapseEtaCycles,
                    ["decline_rate"] = pattern.DeclineRate
                };
            }

            return new Dictionary<string, object>
            {
                ["methods"] = _history.Keys.ToList(),
                ["details"] = perMethod,
                ["total_collapses"] = _collapseSteps.Count
            };
        }
    }
}
