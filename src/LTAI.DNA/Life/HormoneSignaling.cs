using System.Collections.Concurrent;
using LTAI.DNA.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.DNA.Life;

public sealed class HormoneNetwork
{
    private readonly ILogger<HormoneNetwork> _logger;
    private readonly ConcurrentDictionary<HormoneType, HormoneSignal> _hormones = new();
    private readonly ConcurrentDictionary<string, OrganReceptor> _organs = new();
    private int _errorCount;
    private int _successCount;
    private DateTime _lastTick = DateTime.UtcNow;

    private static readonly Dictionary<HormoneType, (double baseline, double halfLifeSeconds)> Defaults = new()
    {
        [HormoneType.Cortisol] = (0.05, 120),
        [HormoneType.Dopamine] = (0.3, 60),
        [HormoneType.Melatonin] = (0.02, 300),
        [HormoneType.Adrenaline] = (0.02, 30),
        [HormoneType.Serotonin] = (0.2, 180),
        [HormoneType.Acetylcholine] = (0.15, 90),
        [HormoneType.Oxytocin] = (0.1, 150),
    };

    private static readonly Dictionary<(HormoneType up, HormoneType down), double> CrossRegulation = new()
    {
        [(HormoneType.Cortisol, HormoneType.Serotonin)] = -0.3,
        [(HormoneType.Cortisol, HormoneType.Dopamine)] = -0.1,
        [(HormoneType.Dopamine, HormoneType.Melatonin)] = -0.2,
        [(HormoneType.Adrenaline, HormoneType.Serotonin)] = -0.1,
        [(HormoneType.Serotonin, HormoneType.Cortisol)] = -0.2,
        [(HormoneType.Melatonin, HormoneType.Dopamine)] = -0.1,
    };

    public HormoneNetwork(ILogger<HormoneNetwork>? logger = null)
    {
        _logger = logger ?? NullLogger<HormoneNetwork>.Instance;
        foreach (var kv in Defaults)
        {
            _hormones[kv.Key] = new HormoneSignal
            {
                Type = kv.Key,
                Level = kv.Value.baseline,
                PeakLevel = kv.Value.baseline,
                SourceOrgan = "default",
                TargetOrgans = new List<string>(),
                DecayRate = Math.Log(2) / kv.Value.halfLifeSeconds,
            };
        }

        RegisterOrgan("brain", new Dictionary<HormoneType, double>
        {
            [HormoneType.Dopamine] = 0.9, [HormoneType.Cortisol] = 0.7,
            [HormoneType.Serotonin] = 0.8, [HormoneType.Adrenaline] = 0.6,
            [HormoneType.Melatonin] = 0.5, [HormoneType.Acetylcholine] = 0.8,
        });
        RegisterOrgan("heart", new Dictionary<HormoneType, double>
        {
            [HormoneType.Adrenaline] = 0.9, [HormoneType.Cortisol] = 0.6,
            [HormoneType.Dopamine] = 0.5, [HormoneType.Oxytocin] = 0.7,
        });
        RegisterOrgan("liver", new Dictionary<HormoneType, double>
        {
            [HormoneType.Cortisol] = 0.8, [HormoneType.Serotonin] = 0.4,
            [HormoneType.Melatonin] = 0.3,
        });
        RegisterOrgan("immune", new Dictionary<HormoneType, double>
        {
            [HormoneType.Cortisol] = 0.7, [HormoneType.Adrenaline] = 0.5,
            [HormoneType.Serotonin] = 0.6,
        });
    }

    public void RegisterOrgan(string name, Dictionary<HormoneType, double> sensitivity)
    {
        _organs[name] = new OrganReceptor
        {
            OrganName = name,
            Sensitivity = new Dictionary<HormoneType, double>(sensitivity),
            CurrentState = 0.5,
            LastActivated = DateTime.UtcNow,
        };
    }

    public void Secrete(HormoneType type, double amount, string source)
    {
        if (!_hormones.TryGetValue(type, out var h))
        {
            h = new HormoneSignal
            {
                Type = type,
                Level = Defaults.GetValueOrDefault(type).baseline,
                PeakLevel = Defaults.GetValueOrDefault(type).baseline,
                SourceOrgan = source,
                DecayRate = Math.Log(2) / Defaults.GetValueOrDefault(type).halfLifeSeconds,
            };
            _hormones[type] = h;
        }

        h.Level = Math.Min(1.0, h.Level + amount);
        h.PeakLevel = Math.Max(h.PeakLevel, h.Level);
        h.SourceOrgan = source;
    }

    public double GetLevel(HormoneType type)
    {
        if (!_hormones.TryGetValue(type, out var h)) return 0;
        var elapsed = (DateTime.UtcNow - h.Timestamp).TotalSeconds;
        var level = h.Level * Math.Exp(-h.DecayRate * elapsed);
        return level;
    }

    public double ComputeOrganState(string organName)
    {
        if (!_organs.TryGetValue(organName, out var organ)) return 0;
        double sumWeighted = 0, sumSens = 0;
        foreach (var (type, sens) in organ.Sensitivity)
        {
            var level = GetLevel(type);
            sumWeighted += level * sens;
            sumSens += sens;
        }
        return sumSens > 0 ? sumWeighted / sumSens : 0;
    }

    public double GetOrganPriority(string organName)
    {
        var state = ComputeOrganState(organName);
        var mel = GetLevel(HormoneType.Melatonin);
        var adr = GetLevel(HormoneType.Adrenaline);
        var cort = GetLevel(HormoneType.Cortisol);

        if (mel > 0.5) state *= 0.5;
        if (adr > 0.5) state = Math.Min(1.0, state * 1.3);
        if (cort > 0.6 && organName is "immune" or "liver") state *= 0.5;
        return Math.Clamp(state, 0, 1);
    }

    public void Tick()
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastTick).TotalSeconds;
        _lastTick = now;

        foreach (var (_, h) in _hormones)
            h.ApplyDecay(elapsed);

        var hour = now.Hour;
        var isNight = hour >= 22 || hour < 6;
        if (isNight)
            Secrete(HormoneType.Melatonin, 0.005, "circadian");
        else
            Secrete(HormoneType.Cortisol, 0.002, "circadian");

        foreach (var (_, h) in _hormones)
        {
            var (baseline, _) = Defaults[h.Type];
            var pull = (baseline - h.Level) * 0.05;
            h.Level += pull;
        }

        foreach (var ((up, down), factor) in CrossRegulation)
        {
            if (GetLevel(up) > 0.3)
                Secrete(down, factor * GetLevel(up) * 0.1, "cross_regulation");
        }

        foreach (var (name, _) in _organs)
            ComputeOrganState(name);
    }

    public void ReportSuccess() => _successCount++;
    public void ReportError() => _errorCount++;

    public void ReportCriticalFailure()
    {
        _errorCount++;
        var ratio = _errorCount / (double)Math.Max(1, _errorCount + _successCount);
        if (ratio > 0.5)
            Secrete(HormoneType.Cortisol, 0.3, "critical_failure");
        if (ratio > 0.3)
            Secrete(HormoneType.Adrenaline, 0.2, "critical_failure");
    }

    public Dictionary<string, object> GetStats()
    {
        var levels = new Dictionary<string, double>();
        foreach (var (type, _) in _hormones)
            levels[type.ToString().ToLower()] = Math.Round(GetLevel(type), 4);

        var organs = new Dictionary<string, double>();
        foreach (var (name, _) in _organs)
            organs[name] = Math.Round(ComputeOrganState(name), 4);

        return new Dictionary<string, object>
        {
            ["hormones"] = levels,
            ["organs"] = organs,
            ["errors"] = _errorCount,
            ["successes"] = _successCount,
            ["error_ratio"] = Math.Round(_errorCount / (double)Math.Max(1, _errorCount + _successCount), 4),
        };
    }
}
