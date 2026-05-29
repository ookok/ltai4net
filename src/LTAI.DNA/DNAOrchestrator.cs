using LTAI.DNA.Models;
using LTAI.DNA.Safety;
using Microsoft.Extensions.Logging;

namespace LTAI.DNA;

/// <summary>
/// DNA Orchestrator — core only. Speculative modules (Consciousness, Evolution, Life, Meta)
/// removed. Retains SafetyCoordinator, SelfEvolution, WorldModel, PredictiveEngine,
/// MentalTimeTravel, RLVRMonitor.
/// </summary>
public sealed class DNAOrchestrator
{
    private readonly ILogger<DNAOrchestrator> _logger;
    private readonly SafetyCoordinator _safety;
    private readonly SelfEvolution _selfEvo;
    private readonly WorldModel _world;
    private readonly PredictiveEngine _predictor;
    private readonly MentalTimeTravel _mtt;
    private readonly RLVRMonitor _rlvr;

    // Stub properties for UI compatibility (speculative modules removed)
    public ConsciousnessStub Consciousness { get; } = new();
    public LifeStub Life { get; } = new();
    public PersonalityStub Personality => Life.Personality;
    public object IdentityNarrative { get; } = new { };
    public object ContextEngineer { get; } = new { };
    public object LocalIntelligence { get; } = new { };
    public object Compiler { get; } = new { };
    public object MetaMemory { get; } = new { };
    public object MetaOptimizer { get; } = new { };
    public object MultiStream { get; } = new { };
    public object SurpriseGate { get; } = new { };

    public SafetyCoordinator Safety => _safety;
    public SelfEvolution SelfEvo => _selfEvo;
    public WorldModel World => _world;
    public PredictiveEngine Predictor => _predictor;
    public MentalTimeTravel MTT => _mtt;
    public RLVRMonitor RLVR => _rlvr;

    public DNAOrchestrator(
        ILogger<DNAOrchestrator> logger,
        SafetyCoordinator safety,
        SelfEvolution selfEvo,
        WorldModel world,
        PredictiveEngine predictor,
        MentalTimeTravel mtt,
        RLVRMonitor rlvr)
    {
        _logger = logger;
        _safety = safety;
        _selfEvo = selfEvo;
        _world = world;
        _predictor = predictor;
        _mtt = mtt;
        _rlvr = rlvr;
        _rlvr.OnCriticalDegradation += (method, gap) =>
        {
            _logger.LogWarning("DNA: Critical degradation detected in '{Method}' (gap={Gap:F3}) — evolution signal recommended",
                method, gap);
        };
    }

    public async Task<DNAProcessResult> ProcessAsync(
        string input,
        string? previousOutput = null,
        CancellationToken cancellationToken = default)
    {
        var traceId = System.Diagnostics.Activity.Current?.TraceId.ToString()
            ?? Guid.NewGuid().ToString("N");

        var safetyVerdict = await _safety.EvaluateAsync(input, previousOutput, cancellationToken).ConfigureAwait(false);

        if (!safetyVerdict.Allowed)
        {
            _logger.LogWarning("DNA process blocked by safety: {Reason}", safetyVerdict.BlockReason);
            _rlvr.Record("safety", 0, 0.1, 0);
            return new DNAProcessResult { Allowed = false, BlockReason = safetyVerdict.BlockReason, SafetyPosture = _safety.Posture, TraceId = traceId };
        }

        _rlvr.Record("process", 1, 0.5, 0);

        return new DNAProcessResult
        {
            Allowed = true,
            RiskScore = safetyVerdict.RiskScore,
            SafetyPosture = _safety.Posture,
            TraceId = traceId
        };
    }

    public DNAStatus GetStatus()
    {
        return new DNAStatus
        {
            SafetyEnabled = true,
            SafetyPosture = _safety.Posture
        };
    }

    public string GenerateSelfNarrative() => "DNA core active (speculative modules removed).";
}

public sealed record DNAProcessResult
{
    public bool Allowed { get; init; } = true;
    public string? BlockReason { get; init; }
    public double RiskScore { get; init; }
    public SafetyPosture SafetyPosture { get; init; }
    public string TraceId { get; init; } = "";
}

public sealed record DNAStatus
{
    // Core
    public bool SafetyEnabled { get; init; }
    public SafetyPosture SafetyPosture { get; init; }

    // Legacy UI compatibility — removed modules return default/zero values
    public string ConsciousnessLevel { get; init; } = "removed";
    public double AwarenessScore { get; init; }
    public string EvolutionPhase { get; init; } = "simplified";
    public int Generation { get; init; }
    public double FitnessScore { get; init; }
    public string BiorhythmPhase { get; init; } = "static";
    public double EnergyLevel { get; init; }
    public int ActiveThoughts { get; init; }
    public int HabitCount { get; init; }
    public double SelfModelAccuracy { get; init; }
    public double WorldModelAccuracy { get; init; }
}
