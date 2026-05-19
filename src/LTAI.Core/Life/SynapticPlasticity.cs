using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.Life;

public enum SynapticState
{
    Silent,
    Active,
    Mature,
    Pruned
}

public sealed record SynapseMetadata
{
    [JsonPropertyName("synapse_id")]
    public string SynapseId { get; init; } = string.Empty;

    [JsonPropertyName("state")]
    public SynapticState State { get; set; } = SynapticState.Silent;

    [JsonPropertyName("weight")]
    public double Weight { get; set; }

    [JsonPropertyName("activation_count")]
    public int ActivationCount { get; set; }

    [JsonPropertyName("last_activated")]
    public DateTime LastActivated { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    [JsonPropertyName("protection_level")]
    public double ProtectionLevel { get; set; }

    [JsonIgnore]
    public bool IsProtected => ProtectionLevel > 0.2;

    [JsonIgnore]
    public double Plasticity => 1.0 - Math.Min(0.9, ActivationCount * 0.05);
}

public sealed record DegradationAlert
{
    [JsonPropertyName("level")]
    public string Level { get; init; } = "normal";

    [JsonPropertyName("interference_ratio")]
    public double InterferenceRatio { get; init; }

    [JsonPropertyName("silent_ratio")]
    public double SilentRatio { get; init; }

    [JsonPropertyName("self_distillation_loss")]
    public double SelfDistillationLoss { get; init; }

    [JsonPropertyName("mature_ratio")]
    public double MatureRatio { get; init; }
}

public sealed class SynapticPlasticity
{
    private static readonly Lazy<SynapticPlasticity> _instance = new(() =>
        new SynapticPlasticity(NullLoggerFactory.Instance.CreateLogger<SynapticPlasticity>()));

    public static SynapticPlasticity Instance => _instance.Value;

    private readonly ILogger<SynapticPlasticity> _logger;
    private readonly ConcurrentDictionary<string, SynapseMetadata> _synapses = new();

    public const double LTP_RATE = 0.12;
    public const double LTD_RATE = 0.003;
    public const double SILENT_THRESHOLD = 0.20;
    public const double MATURE_THRESHOLD = 0.80;
    public const double PRUNE_THRESHOLD = 0.01;
    public const double HOMEOSTATIC_TARGET = 0.30;

    public SynapticPlasticity(ILogger<SynapticPlasticity> logger)
    {
        _logger = logger;
    }

    public static void Initialize(ILogger<SynapticPlasticity> logger)
    {
        var instance = new SynapticPlasticity(logger);
        typeof(SynapticPlasticity)
            .GetField("_instance", global::System.Reflection.BindingFlags.Static | global::System.Reflection.BindingFlags.NonPublic)?
            .SetValue(null, new Lazy<SynapticPlasticity>(() => instance));
    }

    public SynapseMetadata Register(string synapseId, double initialWeight = 0.15)
    {
        var synapse = new SynapseMetadata
        {
            SynapseId = synapseId,
            Weight = Math.Clamp(initialWeight, 0, 1),
            State = SynapticState.Silent,
            ActivationCount = 0,
            ProtectionLevel = 0,
            CreatedAt = DateTime.UtcNow,
            LastActivated = DateTime.UtcNow
        };

        _synapses[synapseId] = synapse;
        _logger.LogDebug("Registered synapse: {Id}, weight: {Weight:F3}", synapseId, synapse.Weight);
        return synapse;
    }

    public SynapseMetadata? Get(string synapseId)
    {
        return _synapses.TryGetValue(synapseId, out var synapse) ? synapse : null;
    }

    public SynapseMetadata GetOrCreate(string synapseId)
    {
        return _synapses.GetOrAdd(synapseId, id => Register(id));
    }

    public SynapseMetadata? Strengthen(string synapseId, double boost = 1.0)
    {
        if (!_synapses.TryGetValue(synapseId, out var synapse))
            return null;

        double plasticity = synapse.Plasticity;
        double increment = LTP_RATE * plasticity * boost / (1.0 + Math.Log(synapse.ActivationCount + 1));

        synapse.Weight = Math.Min(synapse.Weight + increment, 1.0);
        synapse.ActivationCount++;
        synapse.LastActivated = DateTime.UtcNow;

        if (synapse.State == SynapticState.Silent && synapse.Weight >= SILENT_THRESHOLD)
        {
            synapse.State = SynapticState.Active;
            _logger.LogInformation("Synapse {Id} activated at weight {Weight:F3}", synapseId, synapse.Weight);
        }
        else if (synapse.State == SynapticState.Active && synapse.Weight >= MATURE_THRESHOLD)
        {
            synapse.State = SynapticState.Mature;
            synapse.ProtectionLevel += 0.25;
            _logger.LogInformation("Synapse {Id} matured at weight {Weight:F3}", synapseId, synapse.Weight);
        }

        return synapse;
    }

    public SynapseMetadata? Weaken(string synapseId, double penalty = 1.0)
    {
        if (!_synapses.TryGetValue(synapseId, out var synapse))
            return null;

        double decrement = LTP_RATE * 1.5 * penalty;
        synapse.Weight = Math.Max(synapse.Weight - decrement, 0);

        if (synapse.State == SynapticState.Mature && synapse.Weight < MATURE_THRESHOLD)
        {
            synapse.State = SynapticState.Active;
            _logger.LogWarning("Synapse {Id} demoted from Mature to Active", synapseId);
        }
        else if (synapse.State == SynapticState.Active && synapse.Weight < SILENT_THRESHOLD)
        {
            synapse.State = SynapticState.Silent;
            _logger.LogWarning("Synapse {Id} demoted from Active to Silent", synapseId);
        }

        return synapse;
    }

    public int DecayAll()
    {
        int prunedCount = 0;
        var toRemove = new List<string>();

        foreach (var kvp in _synapses)
        {
            var synapse = kvp.Value;
            if (synapse.State == SynapticState.Mature || synapse.IsProtected)
                continue;

            double decayFactor = LTD_RATE * (1.0 - synapse.ProtectionLevel * 0.9);
            synapse.Weight = Math.Max(synapse.Weight - decayFactor, 0);

            if (synapse.Weight < PRUNE_THRESHOLD)
            {
                synapse.State = SynapticState.Pruned;
                toRemove.Add(kvp.Key);
                prunedCount++;
            }
        }

        foreach (var key in toRemove)
        {
            _synapses.TryRemove(key, out _);
        }

        if (prunedCount > 0)
            _logger.LogInformation("DecayAll pruned {Count} synapses", prunedCount);

        return prunedCount;
    }

    public int MatureAllEligible()
    {
        int promoted = 0;

        foreach (var kvp in _synapses)
        {
            var synapse = kvp.Value;
            if (synapse.Weight >= MATURE_THRESHOLD && synapse.State != SynapticState.Mature)
            {
                synapse.State = SynapticState.Mature;
                synapse.ProtectionLevel += 0.5;
                promoted++;
            }
        }

        if (promoted > 0)
            _logger.LogInformation("Matured {Count} eligible synapses", promoted);

        return promoted;
    }

    public void HomeostaticScale()
    {
        if (_synapses.IsEmpty)
            return;

        double avgWeight = _synapses.Values.Average(s => s.Weight);
        double pull = 0.1;

        foreach (var kvp in _synapses)
        {
            var synapse = kvp.Value;
            if (synapse.IsProtected)
                continue;

            double delta = (HOMEOSTATIC_TARGET - synapse.Weight) * pull;
            synapse.Weight = Math.Clamp(synapse.Weight + delta, 0, 1);
        }

        _logger.LogDebug("Homeostatic scaling applied, avg weight: {Avg:F3}, target: {Target:F3}", avgWeight, HOMEOSTATIC_TARGET);
    }

    public Dictionary<string, double> DetectInterference(string strengthenedId, IEnumerable<string> neighborIds)
    {
        var interference = new Dictionary<string, double>();

        foreach (var neighborId in neighborIds)
        {
            if (neighborId == strengthenedId)
                continue;

            if (_synapses.TryGetValue(neighborId, out var synapse))
            {
                if (synapse.State == SynapticState.Mature && synapse.Weight < MATURE_THRESHOLD * 0.85)
                {
                    double degradation = MATURE_THRESHOLD * 0.85 - synapse.Weight;
                    interference[neighborId] = Math.Round(degradation, 4);
                    _logger.LogWarning("Interference detected: {Id}, degradation: {Degradation:F4}", neighborId, degradation);
                }
            }
        }

        return interference;
    }

    public double SilentRatio
    {
        get
        {
            if (_synapses.IsEmpty) return 0;
            return (double)_synapses.Values.Count(s => s.State == SynapticState.Silent) / _synapses.Count;
        }
    }

    public double MatureRatio
    {
        get
        {
            if (_synapses.IsEmpty) return 0;
            return (double)_synapses.Values.Count(s => s.State == SynapticState.Mature) / _synapses.Count;
        }
    }

    public double InterferenceRatio
    {
        get
        {
            if (_synapses.IsEmpty) return 0;
            var matureSynapses = _synapses.Values.Where(s => s.State == SynapticState.Mature).ToList();
            if (matureSynapses.Count == 0) return 0;
            return (double)matureSynapses.Count(s => s.Weight < MATURE_THRESHOLD * 0.85) / matureSynapses.Count;
        }
    }

    public double SelfDistillationLoss()
    {
        if (_synapses.IsEmpty) return 0;

        var weights = _synapses.Values.Select(s => s.Weight).ToList();
        double sum = weights.Sum();
        if (sum <= 0) return 0;

        int n = weights.Count;
        double uniform = 1.0 / n;
        double klDivergence = 0;

        foreach (var w in weights)
        {
            double p = w / sum;
            if (p > 0)
            {
                klDivergence += uniform * Math.Log(uniform / p);
            }
        }

        return Math.Round(klDivergence, 6);
    }

    public DegradationAlert DegradationAlert()
    {
        double silentRatio = SilentRatio;
        double matureRatio = MatureRatio;
        double interferenceRatio = InterferenceRatio;
        double loss = SelfDistillationLoss();

        string level = (interferenceRatio, silentRatio, loss) switch
        {
            ( > 0.5, _, _) or (_, > 0.7, _) or (_, _, > 0.8) => "critical",
            ( > 0.3, _, _) or (_, > 0.4, _) or (_, _, > 0.4) => "warning",
            _ => "normal"
        };

        return new DegradationAlert
        {
            Level = level,
            InterferenceRatio = Math.Round(interferenceRatio, 4),
            SilentRatio = Math.Round(silentRatio, 4),
            SelfDistillationLoss = loss,
            MatureRatio = Math.Round(matureRatio, 4)
        };
    }

    public Dictionary<string, object> Stats()
    {
        int total = _synapses.Count;
        int silent = _synapses.Values.Count(s => s.State == SynapticState.Silent);
        int active = _synapses.Values.Count(s => s.State == SynapticState.Active);
        int mature = _synapses.Values.Count(s => s.State == SynapticState.Mature);
        int pruned = _synapses.Values.Count(s => s.State == SynapticState.Pruned);

        double avgWeight = total > 0 ? _synapses.Values.Average(s => s.Weight) : 0;
        int totalActivations = _synapses.Values.Sum(s => s.ActivationCount);
        int protectedCount = _synapses.Values.Count(s => s.IsProtected);
        var alert = DegradationAlert();

        return new Dictionary<string, object>
        {
            ["total_synapses"] = total,
            ["silent"] = silent,
            ["active"] = active,
            ["mature"] = mature,
            ["pruned"] = pruned,
            ["avg_weight"] = Math.Round(avgWeight, 4),
            ["total_activations"] = totalActivations,
            ["protected_synapses"] = protectedCount,
            ["silent_ratio"] = alert.SilentRatio,
            ["mature_ratio"] = alert.MatureRatio,
            ["interference_ratio"] = alert.InterferenceRatio,
            ["self_distillation_loss"] = alert.SelfDistillationLoss,
            ["alert_level"] = alert.Level
        };
    }
}
