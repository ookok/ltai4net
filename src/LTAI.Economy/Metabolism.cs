using System.Collections.Concurrent;
using LTAI.Economy.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.Economy;

public sealed class MetabolismEngine
{
    private static readonly Lazy<MetabolismEngine> _instance = new(() => new MetabolismEngine());
    public static MetabolismEngine Instance => _instance.Value;

    private const double StarvationThreshold = 0.7;
    private const double DecayRate = 0.001;
    private const double DaySeconds = 86400.0;
    private const double MaxATP = 500.0;
    private const double MaxGlucose = 1_000_000.0;
    private const double MaxOxygen = 4096.0;

    private static readonly HashSet<string> KetosisOrgans = new(StringComparer.OrdinalIgnoreCase)
    {
        "cerebrum", "memory", "knowledge", "execution", "immune"
    };

    private static readonly Dictionary<string, (double BasalRate, double ActiveRate, double GlucosePerRequest, double OxygenMb, int Priority)> DefaultOrgans = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cerebrum"] = (5.0, 4.0, 2000, 512, 1),
        ["memory"] = (2.0, 2.0, 500, 256, 2),
        ["knowledge"] = (3.0, 3.0, 1500, 384, 2),
        ["execution"] = (2.0, 4.0, 3000, 256, 1),
        ["perception"] = (1.0, 2.0, 200, 128, 4),
        ["reflection"] = (1.0, 1.5, 1000, 192, 3),
        ["immune"] = (1.0, 3.0, 500, 128, 2),
        ["economy"] = (1.0, 2.0, 300, 96, 5),
        ["planning"] = (2.0, 3.0, 2000, 256, 3),
        ["compilation"] = (1.0, 2.0, 800, 128, 4),
        ["evolution"] = (1.0, 2.5, 600, 192, 5),
        ["social"] = (1.0, 1.5, 300, 96, 5)
    };

    private readonly ConcurrentDictionary<string, OrganMetabolism> _organs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<MetabolismEngine>? _logger;
    private readonly object _lock = new();
    private MetabolicState _state = new();
    private bool _ketosis;
    private bool _running;
    private CancellationTokenSource? _cts;
    private Task? _backgroundTask;

    public MetabolismEngine(ILogger<MetabolismEngine>? logger = null)
    {
        _logger = logger;
        foreach (var (name, (basalRate, activeRate, glucosePerRequest, oxygenMb, priority)) in DefaultOrgans)
        {
            _organs[name] = new OrganMetabolism
            {
                OrganName = name,
                BasalRate = basalRate,
                ActiveRate = activeRate,
                GlucosePerRequest = glucosePerRequest,
                OxygenMb = oxygenMb,
                Priority = priority,
                AtpPerSecond = basalRate
            };
        }

        _state.BudgetDayStart = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _state.LastUpdated = _state.BudgetDayStart;
    }

    public bool CanAfford(string organName, OperationType operationType = OperationType.Active)
    {
        if (!_organs.TryGetValue(organName, out var organ))
        {
            _logger?.LogWarning("Metabolism: unknown organ '{OrganName}'", organName);
            return false;
        }

        lock (_lock)
        {
            return CheckAffordability(organ, operationType, 1.0);
        }
    }

    public (bool Affordable, MetabolicCost Cost) Consume(string organName,
        OperationType operationType = OperationType.Active, double intensity = 1.0)
    {
        if (!_organs.TryGetValue(organName, out var organ))
        {
            _logger?.LogWarning("Metabolism: unknown organ '{OrganName}'", organName);
            return (false, new MetabolicCost { OrganName = organName, Operation = operationType });
        }

        lock (_lock)
        {
            if (_ketosis && !KetosisOrgans.Contains(organName))
            {
                var rejectedCost = BuildCost(organName, organ, operationType, intensity);
                return (false, rejectedCost);
            }

            var affordable = CheckAffordability(organ, operationType, intensity);
            var cost = BuildCost(organName, organ, operationType, intensity);

            if (affordable)
            {
                _state.TotalATP = Math.Max(0, _state.TotalATP - cost.ATP);
                _state.TotalGlucose = Math.Max(0, _state.TotalGlucose - cost.Glucose);
                _state.TotalOxygenMb = Math.Max(0, _state.TotalOxygenMb - cost.OxygenMb);
                _state.CumulativeAtpSpent += cost.ATP;
                _state.CumulativeGlucoseSpent += cost.Glucose;
                _state.DailyCostYuan += cost.Nadph * 0.02 * organ.BasalRate;

                organ.CurrentConsumption += cost.ATP;
                organ.TotalAtpSpent += cost.ATP;
                organ.RequestCount++;
            }

            return (affordable, cost);
        }
    }

    public void AllocateDailyBudget(double totalTokens, double totalCostYuan)
    {
        lock (_lock)
        {
            _state.TotalATP = MaxATP;
            _state.TotalGlucose = MaxGlucose;
            _state.TotalOxygenMb = MaxOxygen;
            _state.DailyCostYuan = 0;
            _state.CumulativeAtpSpent = 0;
            _state.CumulativeGlucoseSpent = 0;
            _state.BudgetDayStart = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _state.LastUpdated = _state.BudgetDayStart;
        }
    }

    public void NotifyTokenSpent(int tokens, double costYuan = 0.0)
    {
        lock (_lock)
        {
            _state.DailyCostYuan += costYuan;
            _state.CumulativeGlucoseSpent += tokens;
        }
    }

    public void NotifyBudgetEvent(double costYuan)
    {
        lock (_lock)
        {
            _state.DailyCostYuan += costYuan;
        }
    }

    public double GetStarvationLevel()
    {
        lock (_lock)
        {
            return _state.StarvationLevel;
        }
    }

    public bool IsOrganSuppressed(string organName)
    {
        return _organs.TryGetValue(organName, out var organ) && organ.Suppressed;
    }

    public bool IsInKetosis()
    {
        lock (_lock)
        {
            return _ketosis;
        }
    }

    public void EnterKetosis()
    {
        lock (_lock)
        {
            _ketosis = true;
            _state.Ketosis = true;
            _logger?.LogInformation("Metabolism: entering ketosis mode");
        }
    }

    public void ExitKetosis()
    {
        lock (_lock)
        {
            _ketosis = false;
            _state.Ketosis = false;
            _logger?.LogInformation("Metabolism: exiting ketosis mode");
        }
    }

    public void StartBackground()
    {
        lock (_lock)
        {
            if (_running)
                return;

            _running = true;
            _cts = new CancellationTokenSource();
            _backgroundTask = BackgroundTickAsync();
        }
    }

    public void StopBackground()
    {
        lock (_lock)
        {
            if (!_running)
                return;

            _running = false;
            _cts?.Cancel();
        }
    }

    public MetabolicState State
    {
        get
        {
            lock (_lock)
            {
                return new MetabolicState
                {
                    TotalATP = _state.TotalATP,
                    TotalGlucose = _state.TotalGlucose,
                    TotalOxygenMb = _state.TotalOxygenMb,
                    CurrentTemperature = _state.CurrentTemperature,
                    StarvationLevel = _state.StarvationLevel,
                    Ketosis = _state.Ketosis,
                    CumulativeAtpSpent = _state.CumulativeAtpSpent,
                    CumulativeGlucoseSpent = _state.CumulativeGlucoseSpent,
                    DailyCostYuan = _state.DailyCostYuan,
                    LastUpdated = _state.LastUpdated,
                    BudgetDayStart = _state.BudgetDayStart
                };
            }
        }
    }

    public IReadOnlyDictionary<string, double> OrganUtilization()
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, organ) in _organs)
        {
            var maxDraw = organ.ActiveDraw;
            result[name] = maxDraw > 0 ? organ.CurrentConsumption / maxDraw : 0;
        }
        return result;
    }

    public IReadOnlyDictionary<string, object> Stats()
    {
        lock (_lock)
        {
            return new Dictionary<string, object>
            {
                ["total_atp"] = _state.TotalATP,
                ["total_glucose"] = _state.TotalGlucose,
                ["total_oxygen_mb"] = _state.TotalOxygenMb,
                ["temperature"] = _state.CurrentTemperature,
                ["starvation"] = _state.StarvationLevel,
                ["ketosis"] = _state.Ketosis,
                ["daily_cost_yuan"] = _state.DailyCostYuan,
                ["cumulative_atp_spent"] = _state.CumulativeAtpSpent,
                ["cumulative_glucose_spent"] = _state.CumulativeGlucoseSpent,
                ["organ_count"] = _organs.Count
            };
        }
    }

    private MetabolicCost BuildCost(string organName, OrganMetabolism organ, OperationType operationType, double intensity)
    {
        var multiplier = operationType switch
        {
            OperationType.Basal => 1.0,
            OperationType.Active => organ.ActiveRate,
            OperationType.Peak => organ.ActiveRate * 1.5,
            _ => organ.ActiveRate
        };

        var atp = operationType switch
        {
            OperationType.Basal => organ.BasalRate * intensity,
            OperationType.Active => organ.BasalRate * organ.ActiveRate * intensity,
            OperationType.Peak => organ.BasalRate * organ.ActiveRate * 1.5 * intensity,
            _ => organ.BasalRate * organ.ActiveRate * intensity
        };

        var glucose = organ.GlucosePerRequest * intensity * (multiplier / organ.ActiveRate);
        var oxygen = organ.OxygenMb * 0.1 * intensity;
        var nadph = 0.05 * intensity * (multiplier / organ.ActiveRate);

        if (_ketosis)
        {
            atp *= 0.6;
            glucose *= 0.3;
        }

        return new MetabolicCost
        {
            OrganName = organName,
            Operation = operationType,
            ATP = atp,
            Glucose = glucose,
            OxygenMb = oxygen,
            Nadph = nadph,
            Intensity = intensity,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
    }

    private bool CheckAffordability(OrganMetabolism organ, OperationType operationType, double intensity)
    {
        if (organ.Suppressed)
            return false;

        var cost = BuildCost(organ.OrganName, organ, operationType, intensity);

        return cost.ATP <= _state.TotalATP * 0.5
            && cost.Glucose <= _state.TotalGlucose * 0.3
            && cost.OxygenMb <= _state.TotalOxygenMb * 0.05;
    }

    private void Tick()
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            double glucoseDecay = 0;
            double atpDecay = 0;
            double totalCurrentDraw = 0;
            double maxPossibleDraw = 0;

            foreach (var organ in _organs.Values)
            {
                maxPossibleDraw += organ.ActiveDraw;
                if (organ.Suppressed)
                    continue;

                glucoseDecay += organ.GlucosePerRequest * DecayRate;
                atpDecay += organ.BasalRate * 5.0;
                totalCurrentDraw += organ.ActiveDraw;
            }

            _state.TotalGlucose = Math.Max(0, _state.TotalGlucose - glucoseDecay);
            _state.TotalATP = Math.Max(0, _state.TotalATP - atpDecay);

            var glucoseRatio = _state.TotalGlucose / MaxGlucose;
            var atpRatio = _state.TotalATP / MaxATP;
            var oxygenRatio = _state.TotalOxygenMb / MaxOxygen;

            _state.StarvationLevel = Math.Clamp(
                1.0 - (glucoseRatio * 0.4 + atpRatio * 0.35 + oxygenRatio * 0.25), 0, 1);

            _state.CurrentTemperature = maxPossibleDraw > 0
                ? Math.Clamp(totalCurrentDraw / maxPossibleDraw, 0, 1)
                : 0.3;

            if (_state.StarvationLevel > StarvationThreshold && !_ketosis)
            {
                foreach (var organ in _organs.Values)
                {
                    if (organ.Priority > 6)
                        organ.Suppressed = true;
                }
            }
            else if (_state.StarvationLevel < 0.3)
            {
                foreach (var organ in _organs.Values)
                {
                    organ.Suppressed = false;
                }
            }

            if (now - _state.BudgetDayStart >= DaySeconds)
            {
                _state.BudgetDayStart = now;
                _state.DailyCostYuan = 0;
                _state.CumulativeAtpSpent = 0;
                _state.CumulativeGlucoseSpent = 0;
            }

            _state.LastUpdated = now;
        }
    }

    private async Task BackgroundTickAsync()
    {
        while (!_cts!.Token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(5000, _cts.Token);
                Tick();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Metabolism background tick error");
            }
        }
    }
}
