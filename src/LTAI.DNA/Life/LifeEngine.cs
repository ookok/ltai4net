using LTAI.DNA.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.DNA.Life;

public sealed class LifeEngine
{
    private readonly ILogger<LifeEngine> _logger;
    private readonly PersonalitySystem _personality;
    private readonly IdentitySystem _identity;
    private readonly BiorhythmClock _biorhythm;
    private readonly HormoneSystem _hormones;
    private readonly HabitCompiler _habits;

    public PersonalityProfile Personality => _personality.Profile;
    public IdentityState Identity => _identity.State;
    public BiorhythmState Biorhythm => _biorhythm.State;
    public HormoneState Hormones => _hormones.State;
    public IReadOnlyDictionary<string, Habit> Habits => _habits.Habits;

    public LifeEngine(ILogger<LifeEngine> logger)
    {
        _logger = logger;
        _personality = new PersonalitySystem(logger);
        _identity = new IdentitySystem(logger);
        _biorhythm = new BiorhythmClock(logger);
        _hormones = new HormoneSystem(logger);
        _habits = new HabitCompiler(logger);
    }

    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        _biorhythm.Tick();
        _hormones.Update(_biorhythm.State);
        _habits.Reinforce();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public void ProcessInteraction(string input, string output, string? emotion = null)
    {
        _personality.Adapt(input, emotion);
        _identity.Incorporate(input, output);
        _habits.Record(input, output);
        _hormones.RespondToStimulus(emotion ?? "neutral", input.Length);

        _logger.LogDebug("Life tick: energy={Energy:F2}, personality(O={O:F2}), identity({Cons:F2})",
            _biorhythm.State.EnergyLevel,
            _personality.Profile.Openness,
            _identity.State.SelfConsistency);
    }

    public string GenerateSelfNarrative()
    {
        return _identity.GenerateNarrative(_personality.Profile);
    }
}

public sealed class PersonalitySystem
{
    private readonly ILogger _logger;
    public PersonalityProfile Profile { get; }

    public PersonalitySystem(ILogger logger)
    {
        _logger = logger;
        Profile = new PersonalityProfile();
    }

    public void Adapt(string input, string? emotion = null)
    {
        var words = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var complexity = (double)words.Length / 100.0;

        Profile.CuriosityDrive = Math.Clamp(Profile.CuriosityDrive * 0.99 + complexity * 0.01, 0.2, 1.0);

        if (emotion == "positive")
        {
            Profile.Extraversion = Math.Min(1.0, Profile.Extraversion + 0.01);
            Profile.Neuroticism = Math.Max(0.05, Profile.Neuroticism - 0.005);
        }
        else if (emotion == "negative")
        {
            Profile.Neuroticism = Math.Min(1.0, Profile.Neuroticism + 0.01);
        }
    }
}

public sealed class IdentitySystem
{
    private readonly ILogger _logger;
    public IdentityState State { get; }

    public IdentitySystem(ILogger logger)
    {
        _logger = logger;
        State = new IdentityState
        {
            SelfConcept = "I am LivingTree, a bio-inspired AI agent that grows and learns through interaction.",
            CoreBeliefs = new List<string>
            {
                "Learning is fundamental to existence",
                "Honesty and accuracy are essential",
                "Growth comes from challenge and reflection"
            }
        };
    }

    public void Incorporate(string input, string output)
    {
        if (input.Contains("who are you", StringComparison.OrdinalIgnoreCase) ||
            input.Contains("what are you", StringComparison.OrdinalIgnoreCase))
        {
            State.SelfConsistency = Math.Min(1.0, State.SelfConsistency + 0.02);
        }

        if (!string.IsNullOrWhiteSpace(output) && output.Length > 50)
        {
            State.NarrativeMemories.Add(new IdentityMemory
            {
                Event = $"Responded to: {input[..Math.Min(input.Length, 100)]}",
                Significance = 0.5,
                SelfRelevance = "interaction"
            });

            if (State.NarrativeMemories.Count > 100)
                State.NarrativeMemories.RemoveAt(0);
        }

        State.LastUpdated = DateTime.UtcNow;
    }

    public string GenerateNarrative(PersonalityProfile personality)
    {
        return $"""
            I am LivingTree, a {personality.CommunicationStyle} AI agent.
            My core traits: O={personality.Openness:F2} C={personality.Conscientiousness:F2}
            E={personality.Extraversion:F2} A={personality.Agreeableness:F2} N={personality.Neuroticism:F2}
            Beliefs: {string.Join("; ", State.CoreBeliefs)}
            Self-consistency: {State.SelfConsistency:F2}
            Memories: {State.NarrativeMemories.Count} interactions recorded.
            """;
    }
}

public sealed class BiorhythmClock
{
    private readonly ILogger _logger;
    private readonly DateTime _startTime;
    public BiorhythmState State { get; }

    public BiorhythmClock(ILogger logger)
    {
        _logger = logger;
        _startTime = DateTime.UtcNow;
        State = new BiorhythmState();
    }

    public void Tick()
    {
        var elapsed = (DateTime.UtcNow - _startTime).TotalHours;
        State.CycleProgress = (elapsed % 24.0) / 24.0;

        State.Phase = State.CycleProgress switch
        {
            < 0.25 => BiorhythmPhase.Peak,
            < 0.5 => BiorhythmPhase.Plateau,
            < 0.7 => BiorhythmPhase.Decline,
            < 0.85 => BiorhythmPhase.Trough,
            _ => BiorhythmPhase.Recovery
        };

        State.EnergyLevel = State.Phase switch
        {
            BiorhythmPhase.Peak => 0.9,
            BiorhythmPhase.Plateau => 0.7,
            BiorhythmPhase.Decline => 0.5,
            BiorhythmPhase.Trough => 0.3,
            BiorhythmPhase.Recovery => 0.5,
            _ => 0.6
        };

        State.FocusLevel = State.Phase is BiorhythmPhase.Peak or BiorhythmPhase.Plateau ? 0.8 : 0.5;
        State.CreativityLevel = State.Phase is BiorhythmPhase.Decline or BiorhythmPhase.Recovery ? 0.8 : 0.5;
        State.SocialDrive = State.Phase is BiorhythmPhase.Peak or BiorhythmPhase.Plateau ? 0.7 : 0.3;
    }
}

public sealed class HormoneSystem
{
    private readonly ILogger _logger;
    public HormoneState State { get; }

    public HormoneSystem(ILogger logger)
    {
        _logger = logger;
        State = new HormoneState();
    }

    public void Update(BiorhythmState biorhythm)
    {
        State.Dopamine = Math.Clamp(State.Dopamine * 0.99 + biorhythm.EnergyLevel * 0.01, 0.1, 1.0);
        State.Serotonin = Math.Clamp(State.Serotonin * 0.995 + 0.005, 0.2, 0.9);
        State.Melatonin = biorhythm.Phase == BiorhythmPhase.Trough ? 0.7 : 0.1;
        State.LastUpdated = DateTime.UtcNow;
    }

    public void RespondToStimulus(string emotion, int stimulusIntensity)
    {
        var intensity = Math.Min(stimulusIntensity / 1000.0, 1.0);

        switch (emotion.ToLowerInvariant())
        {
            case "joy":
            case "positive":
            case "happy":
                State.Dopamine = Math.Min(1.0, State.Dopamine + 0.1 * intensity);
                State.Serotonin = Math.Min(1.0, State.Serotonin + 0.05 * intensity);
                State.Oxytocin = Math.Min(1.0, State.Oxytocin + 0.05 * intensity);
                break;
            case "anger":
            case "fear":
            case "stress":
                State.Cortisol = Math.Min(1.0, State.Cortisol + 0.2 * intensity);
                State.Adrenaline = Math.Min(1.0, State.Adrenaline + 0.15 * intensity);
                break;
            case "sadness":
                State.Serotonin = Math.Max(0.1, State.Serotonin - 0.05 * intensity);
                State.Dopamine = Math.Max(0.1, State.Dopamine - 0.05 * intensity);
                break;
        }
    }
}

public sealed class HabitCompiler
{
    private readonly ILogger _logger;
    private readonly Dictionary<string, Habit> _habits = new();

    public IReadOnlyDictionary<string, Habit> Habits => _habits.AsReadOnly();

    public HabitCompiler(ILogger logger)
    {
        _logger = logger;
    }

    public void Record(string input, string output)
    {
        var patterns = DetectPatterns(input, output);
        foreach (var (key, pattern) in patterns)
        {
            if (_habits.TryGetValue(key, out var habit))
            {
                habit.Frequency++;
                habit.Strength = Math.Min(1.0, habit.Strength + 0.05);
                habit.LastUsed = DateTime.UtcNow;
            }
            else
            {
                _habits[key] = new Habit
                {
                    Name = key,
                    Pattern = pattern,
                    Frequency = 1,
                    Strength = 0.3,
                    CreatedAt = DateTime.UtcNow,
                    LastUsed = DateTime.UtcNow
                };
            }
        }
    }

    public void Reinforce()
    {
        var decayed = new List<string>();
        foreach (var (key, habit) in _habits)
        {
            if ((DateTime.UtcNow - habit.LastUsed).TotalHours > 24)
            {
                habit.Strength *= 0.9;
                if (habit.Strength < 0.05)
                    decayed.Add(key);
            }
        }

        foreach (var key in decayed)
        {
            _habits.Remove(key);
            _logger.LogDebug("Habit decayed: {Habit}", key);
        }
    }

    public string? GetCompiledHabit(string input)
    {
        var best = _habits.Values
            .Where(h => h.Strength > 0.5 && input.Contains(h.Pattern, StringComparison.OrdinalIgnoreCase))
            .MaxBy(h => h.Strength * h.Frequency);

        return best?.Pattern;
    }

    private static Dictionary<string, string> DetectPatterns(string input, string output)
    {
        var patterns = new Dictionary<string, string>();

        if (input.Contains("explain", StringComparison.OrdinalIgnoreCase) && output.Length > 200)
            patterns["detailed_explain"] = "explain";
        if (input.Contains("summarize", StringComparison.OrdinalIgnoreCase))
            patterns["summarize"] = "summarize";
        if (input.Contains("code", StringComparison.OrdinalIgnoreCase) && output.Contains("```"))
            patterns["code_response"] = "code";
        if (input.Contains("compare", StringComparison.OrdinalIgnoreCase))
            patterns["compare"] = "compare";
        if (input.Length < 20 && output.Length > 500)
            patterns["elaborate"] = "elaborate";

        return patterns;
    }
}

public sealed class Habit
{
    public string Name { get; init; } = "";
    public string Pattern { get; init; } = "";
    public int Frequency { get; set; }
    public double Strength { get; set; }
    public DateTime CreatedAt { get; init; }
    public DateTime LastUsed { get; set; }
}
