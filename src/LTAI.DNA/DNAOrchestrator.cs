using LTAI.DNA.Consciousness;
using LTAI.DNA.Evolution;
using LTAI.DNA.Life;
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
        GodelianSelf godel)
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
            return new DNAProcessResult
            {
                Allowed = false,
                BlockReason = safetyVerdict.BlockReason,
                SafetyPosture = _safety.Posture
            };
        }

        await _consciousness.ProcessExperienceAsync(input, cancellationToken: cancellationToken);

        _life.TickAsync(cancellationToken);

        var fitnessSignals = new Dictionary<string, double>
        {
            ["curiosity"] = _consciousness.State.AwarenessScore,
            ["adaptability"] = safetyVerdict.RiskScore < 0.2 ? 0.8 : 0.4,
            ["precision"] = safetyVerdict.RiskScore < 0.1 ? 1.0 : 0.6,
            ["exploration"] = _consciousness.State.ActiveThoughts.Count > 3 ? 0.8 : 0.4,
        };

        await _evolution.EvolveAsync(fitnessSignals, cancellationToken);
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
            ActiveHabits = _life.Habits.Select(h => h.Value.Name).ToList()
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
            HabitCount = _life.Habits.Count
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
}
