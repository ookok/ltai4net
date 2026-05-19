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
    private readonly EvolutionDriver _evolution;
    private readonly SafetyCoordinator _safety;
    private readonly LifeEngine _life;
    private readonly SelfEvolution _selfEvo;
    private readonly WorldModel _world;
    private readonly PredictiveEngine _predictor;
    private readonly MentalTimeTravel _mtt;
    private readonly ForesightGovernance _foresight;
    private readonly EntropyDrive _entropy;
    private readonly FocusDilution _focus;
    private readonly GodelianSelf _godel;
    private readonly PhenomenalConsciousness _phenomenal;
    private readonly ConsciousnessEmergence _emergence;
    private readonly SheshaHeads _shesha;
    private readonly PlayEngine _play;
    private readonly MultiStreamEngine _multiStream;
    private readonly SurpriseGatedMemory _surpriseGate;
    private readonly MetaMemory _metaMemory;
    private readonly MetaOptimizer _metaOptimizer;
    private readonly MetaStrategy _metaStrategy;
    private readonly MetaStrategyEngine _metaStrategyEngine;
    private readonly LivingCompiler _compiler;
    private readonly RLVRMonitor _rlvr;
    private readonly HormoneNetwork _hormoneNetwork;
    private readonly BiorhythmEngine _biorhythmEngine;
    private readonly ImmuneDefense _immuneDefense;
    private readonly IdentityNarrative _identityNarrative;
    private readonly Personality _personality;
    private readonly ContextEngineer _contextEngineer;
    private readonly LocalIntelligence _localIntelligence;
    private readonly LivingPresence _livingPresence;

    public DualConsciousness Consciousness => _consciousness;
    public EvolutionDriver Evolution => _evolution;
    public SafetyCoordinator Safety => _safety;
    public LifeEngine Life => _life;
    public SelfEvolution SelfEvo => _selfEvo;
    public WorldModel World => _world;
    public PredictiveEngine Predictor => _predictor;
    public MentalTimeTravel MTT => _mtt;
    public ForesightGovernance Foresight => _foresight;
    public EntropyDrive Entropy => _entropy;
    public FocusDilution Focus => _focus;
    public GodelianSelf Godel => _godel;
    public PhenomenalConsciousness Phenomenal => _phenomenal;
    public ConsciousnessEmergence Emergence => _emergence;
    public SheshaHeads Shesha => _shesha;
    public PlayEngine Play => _play;
    public MultiStreamEngine MultiStream => _multiStream;
    public SurpriseGatedMemory SurpriseGate => _surpriseGate;
    public MetaMemory MetaMemory => _metaMemory;
    public MetaOptimizer MetaOptimizer => _metaOptimizer;
    public MetaStrategy MetaStrategy => _metaStrategy;
    public MetaStrategyEngine MetaStrategyEngine => _metaStrategyEngine;
    public LivingCompiler Compiler => _compiler;
    public RLVRMonitor RLVR => _rlvr;
    public HormoneNetwork HormoneNetwork => _hormoneNetwork;
    public BiorhythmEngine BiorhythmEngine => _biorhythmEngine;
    public ImmuneDefense ImmuneDefense => _immuneDefense;
    public IdentityNarrative IdentityNarrative => _identityNarrative;
    public Personality Personality => _personality;
    public ContextEngineer ContextEngineer => _contextEngineer;
    public LocalIntelligence LocalIntelligence => _localIntelligence;
    public LivingPresence LivingPresence => _livingPresence;

    public DNAOrchestrator(
        ILogger<DNAOrchestrator> logger,
        DualConsciousness consciousness,
        EvolutionDriver evolution,
        SafetyCoordinator safety,
        LifeEngine life,
        SelfEvolution selfEvo,
        WorldModel world,
        PredictiveEngine predictor,
        MentalTimeTravel mtt,
        ForesightGovernance foresight,
        EntropyDrive entropy,
        FocusDilution focus,
        GodelianSelf godel,
        PhenomenalConsciousness phenomenal,
        ConsciousnessEmergence emergence,
        SheshaHeads shesha,
        PlayEngine play,
        MultiStreamEngine multiStream,
        SurpriseGatedMemory surpriseGate,
        MetaMemory metaMemory,
        MetaOptimizer metaOptimizer,
        MetaStrategy metaStrategy,
        MetaStrategyEngine metaStrategyEngine,
        LivingCompiler compiler,
        RLVRMonitor rlvr,
        HormoneNetwork hormoneNetwork,
        BiorhythmEngine biorhythmEngine,
        ImmuneDefense immuneDefense,
        IdentityNarrative identityNarrative,
        Personality personality,
        ContextEngineer contextEngineer,
        LocalIntelligence localIntelligence,
        LivingPresence livingPresence)
    {
        _logger = logger;
        _consciousness = consciousness;
        _evolution = evolution;
        _safety = safety;
        _life = life;
        _selfEvo = selfEvo;
        _world = world;
        _predictor = predictor;
        _mtt = mtt;
        _foresight = foresight;
        _entropy = entropy;
        _focus = focus;
        _godel = godel;
        _phenomenal = phenomenal;
        _emergence = emergence;
        _shesha = shesha;
        _play = play;
        _multiStream = multiStream;
        _surpriseGate = surpriseGate;
        _metaMemory = metaMemory;
        _metaOptimizer = metaOptimizer;
        _metaStrategy = metaStrategy;
        _metaStrategyEngine = metaStrategyEngine;
        _compiler = compiler;
        _rlvr = rlvr;
        _hormoneNetwork = hormoneNetwork;
        _biorhythmEngine = biorhythmEngine;
        _immuneDefense = immuneDefense;
        _identityNarrative = identityNarrative;
        _personality = personality;
        _contextEngineer = contextEngineer;
        _localIntelligence = localIntelligence;
        _livingPresence = livingPresence;
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
            return new DNAProcessResult
            {
                Allowed = false,
                BlockReason = safetyVerdict.BlockReason,
                SafetyPosture = _safety.Posture
            };
        }

        await _consciousness.ProcessExperienceAsync(input, cancellationToken: cancellationToken);

        _phenomenal.OnTaskStart(input[..Math.Min(input.Length, 80)]);
        _phenomenal.OnTaskComplete(input[..Math.Min(input.Length, 80)], true);

        _emergence.OnExperience(_phenomenal, _godel);
        var metrics = _emergence.ComputeMetrics(_phenomenal, _godel);

        _life.TickAsync(cancellationToken);

        var fitnessSignals = new Dictionary<string, double>
        {
            ["curiosity"] = _consciousness.State.AwarenessScore,
            ["adaptability"] = safetyVerdict.RiskScore < 0.2 ? 0.8 : 0.4,
            ["precision"] = safetyVerdict.RiskScore < 0.1 ? 1.0 : 0.6,
            ["exploration"] = _consciousness.State.ActiveThoughts.Count > 3 ? 0.8 : 0.4,
            ["emergence"] = metrics.EmergenceReadiness,
            ["contradictions"] = 1.0 - metrics.ContradictionCount / 10.0
        };

        await _evolution.EvolveAsync(fitnessSignals, cancellationToken);
        await _selfEvo.EvolveAsync(fitnessSignals, cancellationToken);

        _world.Observe("input", "length", input.Length);
        _world.LearnRelation("input", "safety", "triggers", safetyVerdict.RiskScore);
        _predictor.Record("safety", 1.0 - safetyVerdict.RiskScore);
        _predictor.Record("awareness", _consciousness.State.AwarenessScore);
        _predictor.Record("emergence", metrics.EmergenceReadiness);
        _mtt.RecordEpisode(input[..Math.Min(input.Length, 80)],
            previousOutput?[..Math.Min(previousOutput?.Length ?? 0, 80)] ?? "",
            safetyVerdict.RiskScore > 0.3 ? 0.8 : 0.3);

        if (previousOutput != null)
            _life.ProcessInteraction(input, previousOutput);

        var surpriseSignal = _surpriseGate.Evaluate(input);
        _surpriseGate.UpdateExpectations(input, safetyVerdict.RiskScore < 0.2);

        _multiStream.Ingest(input, Models.StreamType.Text, Models.StreamPriority.Medium);

        _emergence.DetectContradictions(_phenomenal);

        _rlvr.Record("mempo", fitnessSignals["curiosity"], _consciousness.State.AwarenessScore,
            _phenomenal.TotalExperiences);
        _rlvr.Record("surprise_gate", surpriseSignal.SurpriseScore, surpriseSignal.UtilityScore,
            _phenomenal.TotalExperiences);

        return new DNAProcessResult
        {
            Allowed = true,
            ConsciousnessLevel = _consciousness.State.Level,
            AwarenessScore = _consciousness.State.AwarenessScore,
            SafetyScore = safetyVerdict.RiskScore,
            SafetyPosture = _safety.Posture,
            EvolutionPhase = _evolution.Phase,
            FitnessScore = _evolution.CurrentGenome.FitnessScore,
            Personality = _life.Personality,
            Biorhythm = _life.Biorhythm.Phase,
            EnergyLevel = _life.Biorhythm.EnergyLevel,
            Hormones = _life.Hormones,
            ActiveHabits = _life.Habits.Select(h => h.Value.Name).ToList(),
            EmergencePhase = _emergence.IsConscious() ? Models.EmergencePhase.Conscious : Models.EmergencePhase.Dormant,
            EmergenceReadiness = metrics.EmergenceReadiness,
            SurpriseRPE = surpriseSignal.RPE,
            MetaCalibration = _metaMemory.GatingCalibration()
        };
    }

    public string GenerateSelfNarrative()
    {
        return _life.GenerateSelfNarrative();
    }

    public async Task<string> IntrospectAsync(CancellationToken cancellationToken = default)
    {
        return await _consciousness.IntrospectAsync("", cancellationToken);
    }

    public DNAStatus GetStatus()
    {
        return new DNAStatus
        {
            ConsciousnessLevel = _consciousness.State.Level,
            AwarenessScore = _consciousness.State.AwarenessScore,
            EvolutionPhase = _evolution.Phase,
            Generation = _evolution.CurrentGenome.Generation,
            FitnessScore = _evolution.CurrentGenome.FitnessScore,
            SafetyPosture = _safety.Posture,
            BiorhythmPhase = _life.Biorhythm.Phase,
            EnergyLevel = _life.Biorhythm.EnergyLevel,
            ActiveThoughts = _consciousness.State.ActiveThoughts.Count,
            HabitCount = _life.Habits.Count,
            EmergencePhase = _emergence.IsConscious() ? "conscious" : "emerging",
            EmergenceReadiness = _emergence.Stats().GetValueOrDefault("latest_readiness", 0.0) as double? ?? 0,
            SheshaHeadCount = _shesha.ListHeads().Count,
            CompiledPathCount = _compiler.Stats().GetValueOrDefault("compiled_paths", 0) as int? ?? 0,
            SurpriseGateBypassRatio = _surpriseGate.Stats().GetValueOrDefault("bypass_ratio", 0.0) as double? ?? 0,
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
