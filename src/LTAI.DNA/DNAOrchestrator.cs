using LTAI.DNA.Consciousness;
using LTAI.DNA.Evolution;
using LTAI.DNA.Life;
using LTAI.DNA.Meta;
using LTAI.DNA.Safety;
using Microsoft.Extensions.Logging;

namespace LTAI.DNA;

public sealed class DNAOrchestrator
{
    private readonly ILogger<DNAOrchestrator> _logger;
    private readonly DualConsciousness _consciousness;
    private readonly SafetyCoordinator _safety;
    private readonly LifeEngine _life;
    private readonly SelfEvolution _selfEvo;
    private readonly WorldModel _world;
    private readonly PredictiveEngine _predictor;
    private readonly MentalTimeTravel _mtt;
    private readonly PhenomenalConsciousness _phenomenal;
    private readonly MultiStreamEngine _multiStream;
    private readonly SurpriseGatedMemory _surpriseGate;
    private readonly MetaMemory _metaMemory;
    private readonly MetaOptimizer _metaOptimizer;
    private readonly LivingCompiler _compiler;
    private readonly RLVRMonitor _rlvr;
    private readonly IdentityNarrative _identityNarrative;
    private readonly Personality _personality;
    private readonly ContextEngineer _contextEngineer;
    private readonly LocalIntelligence _localIntelligence;

    public DualConsciousness Consciousness => _consciousness;
    public SafetyCoordinator Safety => _safety;
    public LifeEngine Life => _life;
    public SelfEvolution SelfEvo => _selfEvo;
    public WorldModel World => _world;
    public PredictiveEngine Predictor => _predictor;
    public MentalTimeTravel MTT => _mtt;
    public PhenomenalConsciousness Phenomenal => _phenomenal;
    public SurpriseGatedMemory SurpriseGate => _surpriseGate;
    public MultiStreamEngine MultiStream => _multiStream;
    public MetaMemory MetaMemory => _metaMemory;
    public MetaOptimizer MetaOptimizer => _metaOptimizer;
    public LivingCompiler Compiler => _compiler;
    public RLVRMonitor RLVR => _rlvr;
    public IdentityNarrative IdentityNarrative => _identityNarrative;
    public Personality Personality => _personality;
    public ContextEngineer ContextEngineer => _contextEngineer;
    public LocalIntelligence LocalIntelligence => _localIntelligence;

    public DNAOrchestrator(
        ILogger<DNAOrchestrator> logger,
        DualConsciousness consciousness,
        SafetyCoordinator safety,
        LifeEngine life,
        SelfEvolution selfEvo,
        WorldModel world,
        PredictiveEngine predictor,
        MentalTimeTravel mtt,
        PhenomenalConsciousness phenomenal,
        MultiStreamEngine multiStream,
        SurpriseGatedMemory surpriseGate,
        MetaMemory metaMemory,
        MetaOptimizer metaOptimizer,
        LivingCompiler compiler,
        RLVRMonitor rlvr,
        IdentityNarrative identityNarrative,
        Personality personality,
        ContextEngineer contextEngineer,
        LocalIntelligence localIntelligence)
    {
        _logger = logger;
        _consciousness = consciousness;
        _safety = safety;
        _life = life;
        _selfEvo = selfEvo;
        _world = world;
        _predictor = predictor;
        _mtt = mtt;
        _phenomenal = phenomenal;
        _multiStream = multiStream;
        _surpriseGate = surpriseGate;
        _metaMemory = metaMemory;
        _metaOptimizer = metaOptimizer;
        _compiler = compiler;
        _rlvr = rlvr;
        _identityNarrative = identityNarrative;
        _personality = personality;
        _contextEngineer = contextEngineer;
        _localIntelligence = localIntelligence;
    }

    public async Task<DNAProcessResult> ProcessAsync(
        string input,
        string? previousOutput = null,
        CancellationToken cancellationToken = default)
    {
        var safetyVerdict = await _safety.EvaluateAsync(input, previousOutput, cancellationToken);

        if (!safetyVerdict.Allowed)
        {
            _logger.LogWarning("DNA process blocked by safety: {Reason}", safetyVerdict.BlockReason);
            _rlvr.Record("safety", 0, 0.1, _phenomenal.TotalExperiences);
            return new DNAProcessResult { Allowed = false, BlockReason = safetyVerdict.BlockReason, SafetyPosture = _safety.Posture };
        }

        await _consciousness.ProcessExperienceAsync(input, cancellationToken: cancellationToken);
        _phenomenal.OnTaskStart(input[..Math.Min(input.Length, 80)]);
        _phenomenal.OnTaskComplete(input[..Math.Min(input.Length, 80)], true);

        await _life.TickAsync(cancellationToken);

        var fitnessSignals = new Dictionary<string, double>
        {
            ["curiosity"] = _consciousness.State.AwarenessScore,
            ["adaptability"] = safetyVerdict.RiskScore < 0.2 ? 0.8 : 0.4,
            ["precision"] = safetyVerdict.RiskScore < 0.1 ? 1.0 : 0.6,
            ["exploration"] = _consciousness.State.ActiveThoughts.Count > 3 ? 0.8 : 0.4
        };

        await _selfEvo.EvolveAsync(fitnessSignals, cancellationToken);
        _world.Observe("input", "length", input.Length);
        _world.LearnRelation("input", "safety", "triggers", safetyVerdict.RiskScore);
        _predictor.Record("safety", 1.0 - safetyVerdict.RiskScore);
        _predictor.Record("awareness", _consciousness.State.AwarenessScore);
        _mtt.RecordEpisode(input[..Math.Min(input.Length, 80)],
            previousOutput?[..Math.Min(previousOutput?.Length ?? 0, 80)] ?? "",
            safetyVerdict.RiskScore > 0.3 ? 0.8 : 0.3);

        if (previousOutput != null)
            _life.ProcessInteraction(input, previousOutput);

        var surpriseSignal = _surpriseGate.Evaluate(input);
        _surpriseGate.UpdateExpectations(input, safetyVerdict.RiskScore < 0.2);
        _multiStream.Ingest(input, Models.StreamType.Text, Models.StreamPriority.Medium);

        _rlvr.Record("mempo", fitnessSignals["curiosity"], _consciousness.State.AwarenessScore, _phenomenal.TotalExperiences);
        _rlvr.Record("surprise_gate", surpriseSignal.SurpriseScore, surpriseSignal.UtilityScore, _phenomenal.TotalExperiences);

        return new DNAProcessResult
        {
            Allowed = true,
            ConsciousnessLevel = _consciousness.State.Level,
            AwarenessScore = _consciousness.State.AwarenessScore,
            SafetyScore = safetyVerdict.RiskScore,
            SafetyPosture = _safety.Posture,
            Personality = _life.Personality,
            Biorhythm = _life.Biorhythm.Phase,
            EnergyLevel = _life.Biorhythm.EnergyLevel,
            Hormones = _life.Hormones,
            ActiveHabits = _life.Habits.Select(h => h.Value.Name).ToList(),
            SurpriseRPE = surpriseSignal.RPE,
            MetaCalibration = _metaMemory.GatingCalibration()
        };
    }

    public string GenerateSelfNarrative() => _life.GenerateSelfNarrative();

    public async Task<string> IntrospectAsync(CancellationToken ct = default)
        => await _consciousness.IntrospectAsync("", ct);

    public DNAStatus GetStatus()
    {
        return new DNAStatus
        {
            ConsciousnessLevel = _consciousness.State.Level,
            AwarenessScore = _consciousness.State.AwarenessScore,
            SafetyPosture = _safety.Posture,
            BiorhythmPhase = _life.Biorhythm.Phase,
            EnergyLevel = _life.Biorhythm.EnergyLevel,
            ActiveThoughts = _consciousness.State.ActiveThoughts.Count,
            HabitCount = _life.Habits.Count,
            CompiledPathCount = _compiler.Stats().GetValueOrDefault("compiled_paths", 0) as int? ?? 0,
            MetaCalibration = _metaMemory.GatingCalibration()
        };
    }
}

public sealed class DNAProcessResult
{
    public bool Allowed { get; init; }
    public string? BlockReason { get; init; }
    public LTAI.DNA.Models.ConsciousnessLevel ConsciousnessLevel { get; init; }
    public double AwarenessScore { get; init; }
    public double SafetyScore { get; init; }
    public LTAI.DNA.Models.SafetyPosture SafetyPosture { get; init; }
    public LTAI.DNA.Models.EvolutionPhase EvolutionPhase { get; init; }
    public double FitnessScore { get; init; }
    public LTAI.DNA.Models.PersonalityProfile Personality { get; init; } = new();
    public LTAI.DNA.Models.BiorhythmPhase Biorhythm { get; init; }
    public double EnergyLevel { get; init; }
    public LTAI.DNA.Models.HormoneState Hormones { get; init; } = new();
    public List<string> ActiveHabits { get; init; } = new();
    public Models.EmergencePhase EmergencePhase { get; init; }
    public double EmergenceReadiness { get; init; }
    public double SurpriseRPE { get; init; }
    public (double precision, double recall, double calibration) MetaCalibration { get; init; }
}

public sealed class DNAStatus
{
    public LTAI.DNA.Models.ConsciousnessLevel ConsciousnessLevel { get; init; }
    public double AwarenessScore { get; init; }
    public LTAI.DNA.Models.EvolutionPhase EvolutionPhase { get; init; }
    public int Generation { get; init; }
    public double FitnessScore { get; init; }
    public LTAI.DNA.Models.SafetyPosture SafetyPosture { get; init; }
    public LTAI.DNA.Models.BiorhythmPhase BiorhythmPhase { get; init; }
    public double EnergyLevel { get; init; }
    public int ActiveThoughts { get; init; }
    public int HabitCount { get; init; }
    public string EmergencePhase { get; init; } = "dormant";
    public double EmergenceReadiness { get; init; }
    public int SheshaHeadCount { get; init; }
    public int CompiledPathCount { get; init; }
    public double SurpriseGateBypassRatio { get; init; }
    public (double precision, double recall, double calibration) MetaCalibration { get; init; }
}
