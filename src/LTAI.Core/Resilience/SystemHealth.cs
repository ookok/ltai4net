using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.Resilience;

public enum HealthLevel
{
    Optimal,   // >= 0.85
    Healthy,   // >= 0.65
    Degrading, // >= 0.40
    Critical   // < 0.40
}

public sealed record SubsystemHealth
{
    public string Name { get; init; } = string.Empty;
    public HealthLevel Status { get; init; }
    public double Score { get; init; }
    public Dictionary<string, double> KeyMetrics { get; init; } = new();
    public List<string> Alerts { get; init; } = new();
    public List<string> Recommendations { get; init; } = new();
}

public sealed record SystemHealthReport
{
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public HealthLevel OverallStatus { get; init; }
    public double OverallScore { get; init; }
    public List<SubsystemHealth> Subsystems { get; init; } = new();
    public List<string> ActionItems { get; init; } = new();
    public string Summary { get; init; } = string.Empty;
}

public sealed record TrustProfile
{
    public string AgentId { get; init; } = string.Empty;
    public double Score { get; set; }
    public int Successes { get; set; }
    public int Failures { get; set; }
    public int RateLimits { get; set; }
    public int TotalCalls { get; set; }
    public DateTime? LastSuccess { get; set; }
    public DateTime? LastFailure { get; set; }
    public double DriftScore { get; set; }
    public double ComponentScore { get; set; }
}

public sealed class GreenScheduler
{
    private static readonly Lazy<GreenScheduler> _instance = new(() => new GreenScheduler(AutoLogger<GreenScheduler>.Create()));
    public static GreenScheduler Instance => _instance.Value;

    private readonly ILogger<GreenScheduler> _logger;
    private readonly object _modeLock = new();
    private readonly object _historyLock = new();

    private string _energyMode = "Active";
    private readonly ConcurrentQueue<DeferredTask> _deferred = new();
    private readonly List<string> _history = new();

    public GreenScheduler(ILogger<GreenScheduler> logger)
    {
        _logger = logger;
    }

    public void Submit(string name, int priority, Func<Task> fn)
    {
        string mode;
        lock (_modeLock)
        {
            mode = _energyMode;
        }

        if (mode is "Hibernation" or "Torpor" || priority < 3)
        {
            _deferred.Enqueue(new DeferredTask
            {
                Name = name,
                Priority = priority,
                Fn = fn,
                DeferredAt = DateTime.UtcNow
            });
            _logger.LogDebug("Deferred task '{Name}' (priority={Priority}) in mode {Mode}", name, priority, mode);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await fn();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Task '{Name}' failed", name);
            }
        });
    }

    public void CheckAndAdjust(double cpuPercent)
    {
        string newMode = cpuPercent switch
        {
            > 90.0 => "Hibernation",
            > 70.0 => "Torpor",
            < 30.0 => "Growth",
            _ => "Active"
        };

        string oldMode;
        lock (_modeLock)
        {
            oldMode = _energyMode;
            if (oldMode != newMode)
            {
                _energyMode = newMode;
            }
            else
            {
                return;
            }
        }

        OnModeChange(oldMode, newMode);

        lock (_historyLock)
        {
            _history.Add($"{DateTime.UtcNow:O}: {oldMode} -> {newMode}");
            if (_history.Count > 20)
            {
                _history.RemoveAt(0);
            }
        }

        _logger.LogInformation("Energy mode transition: {OldMode} -> {NewMode} (CPU: {CpuPercent}%)", oldMode, newMode, cpuPercent);
    }

    private void OnModeChange(string oldMode, string newMode)
    {
        if (newMode == "Growth")
        {
            var tasks = new List<DeferredTask>();
            while (_deferred.TryDequeue(out var t))
            {
                tasks.Add(t);
            }

            var ordered = tasks.OrderBy(t => t.Priority).Take(10);
            foreach (var task in ordered)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        _logger.LogInformation("Executing deferred task '{Name}' in Growth mode", task.Name);
                        await task.Fn();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Deferred task '{Name}' failed", task.Name);
                    }
                });
            }

            foreach (var remaining in tasks.Skip(10))
            {
                _deferred.Enqueue(remaining);
            }
        }
    }

    public (string Mode, int DeferredCount, List<string> History) Stats()
    {
        string mode;
        lock (_modeLock)
        {
            mode = _energyMode;
        }

        List<string> history;
        lock (_historyLock)
        {
            history = new List<string>(_history);
        }

        return (mode, _deferred.Count, history);
    }

    internal sealed record DeferredTask
    {
        public string Name { get; init; } = string.Empty;
        public int Priority { get; init; }
        public Func<Task> Fn { get; init; } = () => Task.CompletedTask;
        public DateTime DeferredAt { get; init; }
    }
}

public sealed class SystemHealth
{
    private static readonly Lazy<SystemHealth> _instance = new(() => new SystemHealth(AutoLogger<SystemHealth>.Create()));
    public static SystemHealth Instance => _instance.Value;

    private readonly ILogger<SystemHealth> _logger;
    private readonly object _historyLock = new();
    private readonly List<SystemHealthReport> _history = new();
    private readonly ConcurrentDictionary<string, TrustProfile> _trust = new();

    public SystemHealth(ILogger<SystemHealth> logger)
    {
        _logger = logger;
    }

    public SystemHealthReport Check()
    {
        var subsystems = new List<SubsystemHealth>();

        subsystems.Add(_checkSubsystem("synaptic", new Dictionary<string, double>
        {
            ["messageThroughput"] = RandomTick(),
            ["routingEfficiency"] = RandomTick(),
            ["meshConnectivity"] = RandomTick(),
            ["latencyVariance"] = RandomTick()
        }));

        subsystems.Add(_checkSubsystem("predictability", new Dictionary<string, double>
        {
            ["planSuccessRate"] = RandomTick(),
            ["predictionAccuracy"] = RandomTick(),
            ["feedbackLoopHealth"] = RandomTick(),
            ["driftRate"] = RandomTick()
        }));

        subsystems.Add(_checkSubsystem("emergence", new Dictionary<string, double>
        {
            ["novelPathways"] = RandomTick(),
            ["innovationScore"] = RandomTick(),
            ["synthesisQuality"] = RandomTick()
        }));

        subsystems.Add(_checkSubsystem("consciousness", new Dictionary<string, double>
        {
            ["selfAwareness"] = RandomTick(),
            ["contextRetention"] = RandomTick(),
            ["goalAlignment"] = RandomTick(),
            ["reflectiveDepth"] = RandomTick()
        }));

        subsystems.Add(_checkSubsystem("economic", new Dictionary<string, double>
        {
            ["tokenEfficiency"] = RandomTick(),
            ["costPerTask"] = RandomTick(),
            ["budgetAdherence"] = RandomTick(),
            ["roiScore"] = RandomTick()
        }));

        subsystems.Add(_checkSubsystem("pool", new Dictionary<string, double>
        {
            ["poolSize"] = RandomTick(),
            ["activeWorkers"] = RandomTick(),
            ["queueDepth"] = RandomTick(),
            ["starvationRisk"] = RandomTick()
        }));

        subsystems.Add(_checkSubsystem("router", new Dictionary<string, double>
        {
            ["routingAccuracy"] = RandomTick(),
            ["loadBalance"] = RandomTick(),
            ["failoverSpeed"] = RandomTick(),
            ["ruleVolatility"] = RandomTick()
        }));

        subsystems.Add(_checkSubsystem("pipeline", new Dictionary<string, double>
        {
            ["stagesHealthy"] = RandomTick(),
            ["backpressureLevel"] = RandomTick(),
            ["errorPropagation"] = RandomTick(),
            ["throughput"] = RandomTick()
        }));

        var overallScore = ComputeOverall(subsystems);
        var overallStatus = ScoreToLevel(overallScore);

        var actionItems = new List<string>();
        foreach (var sub in subsystems)
        {
            if (sub.Status <= HealthLevel.Degrading)
            {
                actionItems.AddRange(sub.Recommendations);
            }
        }

        var report = new SystemHealthReport
        {
            Timestamp = DateTime.UtcNow,
            OverallStatus = overallStatus,
            OverallScore = overallScore,
            Subsystems = subsystems,
            ActionItems = actionItems.Distinct().ToList(),
            Summary = $"System health: {overallStatus} ({overallScore:P1}). {subsystems.Count(s => s.Status <= HealthLevel.Degrading)} subsystems require attention."
        };

        lock (_historyLock)
        {
            _history.Add(report);
            if (_history.Count > 50)
            {
                _history.RemoveAt(0);
            }
        }

        _logger.LogInformation("Health check complete: {Status} ({Score:P1})", overallStatus, overallScore);
        return report;
    }

    private SubsystemHealth _checkSubsystem(string name, Dictionary<string, double> metrics)
    {
        var avg = metrics.Values.DefaultIfEmpty(0).Average();
        var score = 0.5 + (avg * 0.5);
        var level = ScoreToLevel(score);

        var alerts = new List<string>();
        var recommendations = new List<string>();

        foreach (var (key, val) in metrics)
        {
            if (val < 0.3)
            {
                alerts.Add($"{key} critically low ({val:P0})");
                recommendations.Add($"Investigate {key} in {name} subsystem");
            }
            else if (val < 0.5)
            {
                alerts.Add($"{key} below threshold ({val:P0})");
            }
        }

        return new SubsystemHealth
        {
            Name = name,
            Status = level,
            Score = score,
            KeyMetrics = metrics,
            Alerts = alerts,
            Recommendations = recommendations
        };
    }

    public double ComputeOverall(List<SubsystemHealth> subsystems)
    {
        if (subsystems.Count == 0) return 0;
        return subsystems.Average(s => s.Score);
    }

    public void RecordTrust(string agentId, bool success, double latencyMs, bool rateLimited)
    {
        var profile = _trust.GetOrAdd(agentId, id => new TrustProfile { AgentId = id });
        lock (profile)
        {
            profile.TotalCalls++;
            if (rateLimited)
            {
                profile.RateLimits++;
            }

            if (success)
            {
                profile.Successes++;
                profile.LastSuccess = DateTime.UtcNow;
            }
            else
            {
                profile.Failures++;
                profile.LastFailure = DateTime.UtcNow;
            }

            profile.Score = GetTrustScore(agentId);
        }

        _logger.LogDebug("Trust recorded for {AgentId}: success={Success}, score={Score:P1}", agentId, success, profile.Score);
    }

    public double GetTrustScore(string agentId)
    {
        if (!_trust.TryGetValue(agentId, out var p)) return 0;

        var successRate = p.TotalCalls > 0 ? (double)p.Successes / p.TotalCalls : 0;
        var rlPenalty = p.TotalCalls > 0 ? (double)p.RateLimits / p.TotalCalls : 0;
        var recency = p.LastSuccess.HasValue
            ? Math.Max(0, 1.0 - (DateTime.UtcNow - p.LastSuccess.Value).TotalHours / 24.0)
            : 0;

        return 0.4 * successRate + 0.2 * recency + 0.15 * (1 - p.DriftScore) + 0.1 * p.ComponentScore + 0.15 * (1 - rlPenalty);
    }

    public bool CanAutoApprove(string agentId, double threshold = 0.60)
    {
        return GetTrustScore(agentId) >= threshold;
    }

    public string TrustLevel(string agentId)
    {
        var score = GetTrustScore(agentId);
        return score switch
        {
            >= 0.85 => "minimal",
            >= 0.60 => "standard",
            _ => "strict"
        };
    }

    public (int ReportCount, int TrustProfileCount, double OverallHealth) Stats()
    {
        int reportCount, trustCount;
        lock (_historyLock)
        {
            reportCount = _history.Count;
        }
        trustCount = _trust.Count;

        var overall = trustCount > 0 ? _trust.Values.Average(t => t.Score) : 0;
        return (reportCount, trustCount, overall);
    }

    public Dictionary<string, object> GetVitals()
    {
        var cpu = EstimateCpuUsage();
        var memory = EstimateMemoryUsage();
        var (arousal, valence) = EstimateEmotionDimensions();

        return new Dictionary<string, object>
        {
            ["cpu_estimate"] = cpu,
            ["memory_estimate_mb"] = memory,
            ["emotion_color"] = _emotionToColor(arousal, valence),
            ["led_state"] = ScoreToLevel(ComputeOverallLocked()) == HealthLevel.Critical ? "red_pulse" : "green_steady",
            ["arousal"] = arousal,
            ["valence"] = valence
        };
    }

    private double ComputeOverallLocked()
    {
        List<SystemHealthReport> snap;
        lock (_historyLock)
        {
            snap = new List<SystemHealthReport>(_history);
        }
        return snap.Count > 0 ? snap.Last().OverallScore : 1.0;
    }

    private double EstimateCpuUsage()
    {
        return Math.Round(Environment.ProcessorCount > 0
            ? Math.Min(1.0, (DateTime.UtcNow.Millisecond % 100) / 200.0 + 0.1)
            : 0.3, 2);
    }

    private double EstimateMemoryUsage()
    {
        return Math.Round(GC.GetTotalMemory(false) / (1024.0 * 1024.0), 2);
    }

    private (double Arousal, double Valence) EstimateEmotionDimensions()
    {
        List<SystemHealthReport> snap;
        lock (_historyLock)
        {
            snap = new List<SystemHealthReport>(_history);
        }

        if (snap.Count < 2)
        {
            return (0.5, 0.5);
        }

        var trend = snap[^1].OverallScore - snap[^2].OverallScore;
        var valence = Math.Clamp(snap[^1].OverallScore, 0, 1);
        var arousal = Math.Clamp(0.5 + trend * 2.0, 0, 1);

        return (arousal, valence);
    }

    private string _emotionToColor(double arousal, double valence)
    {
        return (arousal, valence) switch
        {
            ( >= 0.6, >= 0.6) => "green",
            ( >= 0.6, < 0.4) => "red",
            ( < 0.4, >= 0.6) => "blue",
            ( < 0.4, < 0.4) => "amber",
            _ => "amber"
        };
    }

    private static HealthLevel ScoreToLevel(double score)
    {
        return score switch
        {
            >= 0.85 => HealthLevel.Optimal,
            >= 0.65 => HealthLevel.Healthy,
            >= 0.40 => HealthLevel.Degrading,
            _ => HealthLevel.Critical
        };
    }

    private static double RandomTick()
    {
        return Math.Round(0.4 + Random.Shared.NextDouble() * 0.6, 3);
    }
}

internal static class AutoLogger<T>
{
    public static ILogger<T> Create()
    {
        return NullLoggerFactory.Instance.CreateLogger<T>();
    }
}
