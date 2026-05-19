using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.Life;

public record TwinSnapshot(
    Dictionary<string, double> SynapseWeights,
    Dictionary<string, string> SynapseStates,
    double PoolHealth,
    Dictionary<string, double> EconomicStats,
    Dictionary<string, double> PredictabilityData,
    DateTime Timestamp);

public record SimulationResult(
    double HoursSimulated,
    double InitialHealth,
    double PredictedHealth,
    List<double> HealthTrajectory,
    List<string> CriticalEvents,
    List<string> PreRepairsNeeded,
    double Confidence);

public sealed class DigitalTwin
{
    private static readonly Lazy<DigitalTwin> _instance = new(() => new DigitalTwin());
    public static DigitalTwin Instance => _instance.Value;

    private readonly List<SimulationResult> _history = new();
    private readonly Random _rng = new();
    private readonly ILogger<DigitalTwin> _logger;
    private readonly object _lock = new();

    private const int MaxHistory = 50;

    public DigitalTwin() : this(NullLogger<DigitalTwin>.Instance) { }

    public DigitalTwin(ILogger<DigitalTwin> logger)
    {
        _logger = logger ?? NullLogger<DigitalTwin>.Instance;
    }

    public IReadOnlyList<SimulationResult> History
    {
        get { lock (_lock) return _history.ToList(); }
    }

    public TwinSnapshot Snapshot(
        Dictionary<string, double> synapseWeights,
        double poolHealth,
        Dictionary<string, double> economicStats)
    {
        var states = synapseWeights.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value switch
            {
                > 0.8 => "mature",
                > 0.5 => "developing",
                > 0.2 => "nascent",
                _ => "dormant"
            });

        var predictability = new Dictionary<string, double>(synapseWeights);
        predictability["pool_health"] = poolHealth;

        var snapshot = new TwinSnapshot(
            new Dictionary<string, double>(synapseWeights),
            states,
            poolHealth,
            new Dictionary<string, double>(economicStats),
            predictability,
            DateTime.UtcNow);

        _logger.LogInformation("Snapshot created with {WeightCount} weights, PoolHealth={PoolHealth}",
            synapseWeights.Count, poolHealth);

        return snapshot;
    }

    public SimulationResult Simulate(TwinSnapshot snapshot, double hours = 24, int checkpoints = 6)
    {
        var initialHealth = ComputeHealth(
            snapshot.SynapseWeights,
            snapshot.PoolHealth,
            snapshot.EconomicStats);

        var trajectory = new List<double> { initialHealth };
        var criticalEvents = new List<string>();
        var interval = hours / checkpoints;
        var currentHealth = initialHealth;

        for (int i = 1; i <= checkpoints; i++)
        {
            var noise = GenerateGaussianNoise(0, 0.05);
            var decay = 0.002 * interval;
            var drift = noise - decay;

            foreach (var key in snapshot.SynapseWeights.Keys.ToList())
            {
                var weight = snapshot.SynapseWeights[key] + drift * 0.3;
                snapshot.SynapseWeights[key] = Math.Clamp(weight, 0.0, 1.0);
            }

            var economicDrift = GenerateGaussianNoise(0, 0.03) * interval;
            foreach (var key in snapshot.EconomicStats.Keys.ToList())
            {
                snapshot.EconomicStats[key] += economicDrift * 0.1;
            }

            var poolDrift = GenerateGaussianNoise(0, 0.02) * interval;
            var newPoolHealth = Math.Clamp(snapshot.PoolHealth + poolDrift, 0.0, 1.0);
            snapshot = snapshot with { PoolHealth = newPoolHealth };

            currentHealth = ComputeHealth(
                snapshot.SynapseWeights,
                snapshot.PoolHealth,
                snapshot.EconomicStats);

            trajectory.Add(currentHealth);

            if (currentHealth < 0.3)
                criticalEvents.Add($"CRITICAL at {i * interval:F1}h: health={currentHealth:F3}");

            _logger.LogDebug("Checkpoint {Index}/{Total}: health={Health:F3}", i, checkpoints, currentHealth);
        }

        var predictedHealth = trajectory[^1];
        var confidence = Math.Clamp(1.0 - Math.Abs(predictedHealth - initialHealth), 0.1, 0.95);

        var repairs = GenerateRepairs(trajectory, predictedHealth);

        var result = new SimulationResult(
            hours,
            Math.Round(initialHealth, 4),
            Math.Round(predictedHealth, 4),
            trajectory.Select(h => Math.Round(h, 4)).ToList(),
            criticalEvents,
            repairs,
            Math.Round(confidence, 4));

        lock (_lock)
        {
            _history.Add(result);
            if (_history.Count > MaxHistory)
                _history.RemoveAt(0);
        }

        _logger.LogInformation("Simulation complete: {Hours}h, Initial={Initial:F3}, Predicted={Predicted:F3}, Confidence={Confidence:F3}",
            hours, initialHealth, predictedHealth, confidence);

        return result;
    }

    private double ComputeHealth(
        Dictionary<string, double> weights,
        double poolHealth,
        Dictionary<string, double> economicStats)
    {
        var matureCount = weights.Values.Count(w => w > 0.8);
        var totalWeights = Math.Max(weights.Count, 1);
        var matureRatio = (double)matureCount / totalWeights;

        var economicScore = economicStats.Count > 0
            ? economicStats.Values.Average()
            : 0.5;

        return 0.4 * matureRatio + 0.3 * poolHealth + 0.3 * economicScore;
    }

    private List<string> GenerateRepairs(List<double> trajectory, double predicted)
    {
        var repairs = new List<string>();

        if (predicted < 0.2)
            repairs.Add("EMERGENCY: System health critical - immediate intervention required");
        else if (predicted < 0.4)
            repairs.Add("WARNING: System health low - schedule maintenance within 24h");

        var trend = trajectory.Count >= 2 ? trajectory[^1] - trajectory[^2] : 0;
        if (trend < -0.1)
            repairs.Add($"ALERT: Rapid health decline detected (delta={trend:F3}/step)");

        var minHealth = trajectory.Min();
        if (minHealth < 0.5)
            repairs.Add($"RECOMMEND: Minimum observed health {minHealth:F3} - consider resource reallocation");

        if (trajectory.Count >= 3 && trajectory[^1] < trajectory[^2] && trajectory[^2] < trajectory[^3])
            repairs.Add("RECOMMEND: Consecutive decline trend - review synapse weights and pool resources");

        if (repairs.Count == 0)
            repairs.Add("System health trajectory is stable - no repairs needed at this time");

        return repairs;
    }

    private double GenerateGaussianNoise(double mean, double stdDev)
    {
        double u1 = 1.0 - _rng.NextDouble();
        double u2 = 1.0 - _rng.NextDouble();
        double randStdNormal = Math.Sqrt(-2.0 * Math.Log(Math.Max(u1, 1e-10))) *
                               Math.Sin(2.0 * Math.PI * u2);
        return mean + stdDev * randStdNormal;
    }

    public Dictionary<string, object> Stats()
    {
        lock (_lock)
        {
            var lastResult = _history.Count > 0 ? _history[^1] : null;
            return new Dictionary<string, object>
            {
                ["simulation_count"] = _history.Count,
                ["last_initial_health"] = lastResult?.InitialHealth ?? 0,
                ["last_predicted_health"] = lastResult?.PredictedHealth ?? 0,
                ["last_confidence"] = lastResult?.Confidence ?? 0,
                ["last_critical_events"] = lastResult?.CriticalEvents.Count ?? 0,
                ["total_critical_events"] = _history.Sum(h => h.CriticalEvents.Count),
                ["avg_predicted_health"] = _history.Count > 0 ? _history.Average(h => h.PredictedHealth) : 0,
                ["avg_confidence"] = _history.Count > 0 ? _history.Average(h => h.Confidence) : 0
            };
        }
    }
}
