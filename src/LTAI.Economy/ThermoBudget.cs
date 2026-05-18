using Microsoft.Extensions.Logging;
using LTAI.Economy.Models;

namespace LTAI.Economy;

public sealed class ThermodynamicBudget
{
    private static readonly Lazy<ThermodynamicBudget> _instance = new(() => new ThermodynamicBudget());
    public static ThermodynamicBudget Instance => _instance.Value;

    private readonly ILogger<ThermodynamicBudget>? _logger;
    private readonly int _lookbackWindow;
    private readonly double _dailyBudgetYuan;
    private readonly double _klBudgetMax;
    private readonly double _klBudgetDecay;

    private double _klBudget;
    private readonly Queue<double> _recentCosts;
    private readonly Queue<double> _tempHistory;
    private DateTime _dayStart;
    private int _coolingStep;

    private double _temperature = 0.5;
    private double _entropy = 0.5;
    private double _pressure = 0.5;
    private double _remainingBudget;

    public ThermodynamicBudget(
        double dailyBudgetYuan = 50.0,
        int lookbackWindow = 50,
        double klBudgetMax = 1.0,
        double klBudgetDecay = 0.98,
        ILogger<ThermodynamicBudget>? logger = null)
    {
        _dailyBudgetYuan = dailyBudgetYuan;
        _lookbackWindow = lookbackWindow;
        _klBudgetMax = klBudgetMax;
        _klBudgetDecay = klBudgetDecay;
        _logger = logger;

        _klBudget = klBudgetMax;
        _recentCosts = new Queue<double>(lookbackWindow);
        _tempHistory = new Queue<double>(lookbackWindow);
        _dayStart = DateTime.UtcNow;
        _remainingBudget = dailyBudgetYuan;
        _coolingStep = 0;
    }

    public double KLBudget => _klBudget;

    public ThermalState State => new()
    {
        Temperature = _temperature,
        Entropy = _entropy,
        Pressure = _pressure,
        RemainingBudget = _remainingBudget,
        EquilibriumTemp = 0.4,
        Timestamp = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds
    };

    public void RecordSpending(double costYuan)
    {
        if ((DateTime.UtcNow - _dayStart).TotalHours >= 24)
            ResetDaily();

        _recentCosts.Enqueue(costYuan);
        while (_recentCosts.Count > _lookbackWindow)
            _recentCosts.Dequeue();

        _remainingBudget = Math.Max(0, _remainingBudget - costYuan);

        var avgSpend = _recentCosts.Count > 0 ? _recentCosts.Average() : 0;
        var budgetPerHour = _dailyBudgetYuan / 24.0;
        _temperature = Math.Clamp(avgSpend / budgetPerHour, 0, 1);

        _tempHistory.Enqueue(_temperature);
        while (_tempHistory.Count > _lookbackWindow)
            _tempHistory.Dequeue();

        if (_recentCosts.Count > 1)
        {
            var mean = _recentCosts.Average();
            if (mean > 0)
            {
                var variance = _recentCosts.Select(c => (c - mean) * (c - mean)).Sum() / _recentCosts.Count;
                var stddev = Math.Sqrt(variance);
                _entropy = Math.Clamp(stddev / mean, 0, 1);
            }
            else
            {
                _entropy = 0;
            }
        }
        else
        {
            _entropy = 0;
        }

        var elapsedHours = Math.Max(0.1, (DateTime.UtcNow - _dayStart).TotalHours);
        var hoursLeft = Math.Max(0.1, 24.0 - elapsedHours);
        var recentSpendRate = _recentCosts.Sum() / elapsedHours;
        var budgetRate = _remainingBudget / hoursLeft;
        _pressure = Math.Clamp((recentSpendRate * 300) / budgetRate, 0, 1);

        _logger?.LogDebug("Thermo: T={Temp:F2} S={Entropy:F2} P={Pressure:F2} Remaining={Rem:F2}",
            _temperature, _entropy, _pressure, _remainingBudget);
    }

    public ThermoDecision Evaluate(string taskId, double estimatedCost,
        double taskPriority = 0.5, double predictedQuality = 0.5)
    {
        _klBudget *= _klBudgetDecay;

        if (_entropy > 0.4)
        {
            _klBudget += (_entropy - 0.4) * 0.05;
            _klBudget = Math.Min(_klBudget, _klBudgetMax);
        }

        var freeEnergy = _remainingBudget - _temperature * _entropy * estimatedCost * 2;
        var canAfford = freeEnergy >= estimatedCost;

        string modelTier;
        if (_entropy > 0.7)
            modelTier = "flash";
        else if (_entropy > 0.3)
            modelTier = "pro";
        else
            modelTier = "ultra";

        if (modelTier == "flash" && _klBudget >= 0.3)
        {
            modelTier = "pro";
            _klBudget -= 0.3;
        }
        else if (modelTier == "pro" && _klBudget >= 0.5)
        {
            modelTier = "ultra";
            _klBudget -= 0.5;
        }

        var proceed = canAfford;
        if (taskPriority > (1.0 - _temperature))
        {
            proceed = true;
            modelTier = "ultra";
        }

        if (predictedQuality < 0.3 && modelTier == "ultra")
        {
            modelTier = "pro";
            _klBudget += 0.2;
            _klBudget = Math.Min(_klBudget, _klBudgetMax);
        }

        var mean = _recentCosts.Count > 0 ? _recentCosts.Average() : estimatedCost;
        double entropyAfter;
        if (mean > 0 && _recentCosts.Count > 0)
            entropyAfter = Math.Clamp(_entropy + (Math.Abs(estimatedCost - mean) / mean - 1.0) * 0.1, 0, 1);
        else
            entropyAfter = Math.Clamp(_entropy + 0.1, 0, 1);

        var recommendation = proceed switch
        {
            true when modelTier == "ultra" => "Optimal conditions: proceed with maximum quality.",
            true when modelTier == "pro" => "Proceed with balanced quality/cost.",
            true => "Proceed with minimal cost model.",
            false when freeEnergy < 0 => "Budget exhausted. Consider waiting or reducing scope.",
            false => "Insufficient free energy. Defer or reduce cost."
        };

        return new ThermoDecision
        {
            TaskId = taskId,
            Proceed = proceed,
            ModelTier = modelTier,
            AllocatedBudget = proceed ? estimatedCost : 0,
            TemperatureNow = _temperature,
            EntropyNow = _entropy,
            EntropyAfter = entropyAfter,
            FreeEnergy = freeEnergy,
            Recommendation = recommendation
        };
    }

    public bool ConsumeKlBudget(double amount)
    {
        if (_klBudget >= amount)
        {
            _klBudget -= amount;
            return true;
        }
        return false;
    }

    public void ContributeKlBudget(double amount)
    {
        _klBudget = Math.Min(_klBudget + amount, _klBudgetMax);
    }

    public bool ConsumeKL(double amount) => ConsumeKlBudget(amount);

    public void ContributeKL(double amount) => ContributeKlBudget(amount);

    public void AccumulateEntropy(double entropy)
    {
        _klBudget *= _klBudgetDecay;
        if (entropy > 0.4)
        {
            var contribution = (entropy - 0.4) * 0.05;
            _klBudget = Math.Min(_klBudgetMax, _klBudget + contribution);
        }
    }

    public double OptimalSpendingRate()
    {
        var elapsedHours = Math.Max(0.1, (DateTime.UtcNow - _dayStart).TotalHours);
        var hoursLeft = Math.Max(0.1, 24.0 - elapsedHours);
        return _remainingBudget / hoursLeft * (1.0 - _entropy);
    }

    public double EntropyBudgetRatio()
    {
        var budgetFraction = _dailyBudgetYuan > 0 ? _remainingBudget / _dailyBudgetYuan : 0;
        return budgetFraction > 0 ? _entropy / budgetFraction : _entropy;
    }

    public string DetectPhaseTransition()
    {
        if (_entropy < 0.2 && _pressure < 0.2)
            return "frozen";
        if (_entropy < 0.4 && _pressure < 0.5)
            return "ordered";
        if (_entropy >= 0.4 && _entropy <= 0.7)
            return "critical";
        return "chaotic";
    }

    public bool FowlerNordheimEscape(double entropyDeficit = 0.3)
    {
        if (DetectPhaseTransition() != "frozen")
            return false;

        var dS = entropyDeficit - _entropy;
        if (_temperature <= 0)
            return false;

        var pTunnel = Math.Exp(-dS / _temperature);
        if (Random.Shared.NextDouble() < pTunnel)
        {
            _entropy = 0.35;
            _temperature = 0.4;
            _pressure = 0.3;
            return true;
        }

        return false;
    }

    public double ActiveCoolingSchedule(string targetPhase = "ordered")
    {
        _coolingStep++;
        return 0.4 / Math.Log(Math.E + _coolingStep);
    }

    public double EnergyBarrierEstimate()
    {
        var dU = 0.3 - _pressure;
        var dS = 0.2 - _entropy;
        return dU - _temperature * dS;
    }

    public IReadOnlyDictionary<string, bool> ConvergenceCertificate()
    {
        var phase = DetectPhaseTransition();
        var barrier = EnergyBarrierEstimate();
        return new Dictionary<string, bool>
        {
            ["PhaseOrdered"] = phase == "ordered",
            ["PhaseNotChaotic"] = phase != "chaotic",
            ["LowEntropy"] = _entropy < 0.5,
            ["BudgetSustainable"] = _remainingBudget > _dailyBudgetYuan * 0.1,
            ["BarrierPositive"] = barrier > 0,
            ["KLBudgetHealthy"] = _klBudget > 0.3
        };
    }

    public void ResetDaily()
    {
        _dayStart = DateTime.UtcNow;
        _remainingBudget = _dailyBudgetYuan;
        _recentCosts.Clear();
        _tempHistory.Clear();
        _temperature = 0.5;
        _entropy = 0.5;
        _pressure = 0.5;
        _coolingStep = 0;
    }

    public IReadOnlyDictionary<string, object> Stats()
    {
        return new Dictionary<string, object>
        {
            ["Temperature"] = _temperature,
            ["Entropy"] = _entropy,
            ["Pressure"] = _pressure,
            ["RemainingBudget"] = _remainingBudget,
            ["DailyBudget"] = _dailyBudgetYuan,
            ["KLBudget"] = _klBudget,
            ["KLBudgetMax"] = _klBudgetMax,
            ["Phase"] = DetectPhaseTransition(),
            ["CoolingStep"] = _coolingStep,
            ["RecentCosts"] = _recentCosts.Count,
            ["OptimalRate"] = OptimalSpendingRate(),
            ["EnergyBarrier"] = EnergyBarrierEstimate(),
            ["TimeToReset"] = Math.Max(0, 24.0 - (DateTime.UtcNow - _dayStart).TotalHours)
        };
    }
}
