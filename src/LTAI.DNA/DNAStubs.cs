namespace LTAI.DNA;

/// <summary>
/// Minimal Consciousness stub — replaces deleted DualConsciousness for UI compatibility.
/// </summary>
public sealed class ConsciousnessStub
{
    public ConsciousnessStateStub State { get; init; } = new();
    public int TotalExperiences { get; init; }
}

public sealed class ConsciousnessStateStub
{
    public string Level { get; init; } = "removed";
    public double AwarenessScore { get; init; }
    public double SelfModelAccuracy { get; init; }
    public double WorldModelAccuracy { get; init; }
    public int ActiveThoughts { get; init; }
    public Dictionary<string, double> AttentionVector { get; init; } = new();
}

/// <summary>
/// Minimal Life stub — replaces deleted LifeEngine for UI compatibility.
/// </summary>
public sealed class LifeStub
{
    public PersonalityStub Personality { get; init; } = new();
    public BiorhythmStub Biorhythm { get; init; } = new();
    public HormonesStub Hormones { get; init; } = new();
    public Dictionary<string, HabitStub> Habits { get; init; } = new();
}

public sealed class PersonalityStub
{
    public PersonalityProfileStub Profile { get; init; } = new();

    public double Openness { get; init; }
    public double Conscientiousness { get; init; }
    public double Extraversion { get; init; }
    public double Agreeableness { get; init; }
    public double Neuroticism { get; init; }
    public double CuriosityDrive { get; init; }
    public string CommunicationStyle { get; init; } = "balanced";
}

public sealed class BiorhythmStub
{
    public string Phase { get; init; } = "static";
    public double EnergyLevel { get; init; }
    public double FocusLevel { get; init; }
    public double CreativityLevel { get; init; }
}

public sealed class HormonesStub
{
    public double Dopamine { get; init; }
    public double Serotonin { get; init; }
    public double Cortisol { get; init; }
    public double Oxytocin { get; init; }
}

public sealed class HabitStub
{
    public string Name { get; init; } = "";
    public double Strength { get; init; }
    public int Frequency { get; init; }
}

public sealed class PersonalityProfileStub
{
    public double Openness { get; init; }
    public double Conscientiousness { get; init; }
    public double Extraversion { get; init; }
    public double Agreeableness { get; init; }
    public double Neuroticism { get; init; }
}
