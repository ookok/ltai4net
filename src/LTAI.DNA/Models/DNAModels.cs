namespace LTAI.DNA.Models;

public enum ConsciousnessLevel
{
    Dormant,
    Reactive,
    SelfAware,
    Reflective,
    MetaCognitive,
    Transcendent
}

public enum EvolutionPhase
{
    Embryonic,
    Growth,
    Maturation,
    Specialization,
    Innovation,
    Integration,
    Senescence
}

public enum SafetyPosture
{
    Permissive,
    Cautious,
    Guarded,
    Defensive,
    Lockdown
}

public enum BiorhythmPhase
{
    Peak,
    Plateau,
    Decline,
    Trough,
    Recovery
}

public sealed class Genome
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Version { get; set; } = "1.0.0";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Dictionary<string, Gene> Genes { get; set; } = new();
    public List<string> ActivatedPathways { get; set; } = new();
    public List<MutationRecord> MutationHistory { get; set; } = new();

    public double FitnessScore { get; set; }
    public int Generation { get; set; }
}

public sealed class Gene
{
    public string Name { get; init; } = "";
    public double Expression { get; set; } = 0.5;
    public double FitnessScore => Expression;
    public double Stability { get; init; } = 0.8;
    public double MutationRate { get; init; } = 0.01;
    public Dictionary<string, double> Interactions { get; init; } = new();
}

public sealed class MutationRecord
{
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string Gene { get; init; } = "";
    public double OldExpression { get; init; }
    public double NewExpression { get; init; }
    public string Trigger { get; init; } = "";
    public double FitnessDelta { get; init; }
}

public sealed class ConsciousnessState
{
    public ConsciousnessLevel Level { get; set; } = ConsciousnessLevel.Reactive;
    public double AwarenessScore { get; set; }
    public double SelfModelAccuracy { get; set; }
    public double WorldModelAccuracy { get; set; }
    public List<string> ActiveThoughts { get; set; } = new();
    public Dictionary<string, double> AttentionVector { get; init; } = new();
    public DateTime LastReflection { get; set; }
}

public sealed class PersonalityProfile
{
    public string[] BigFive { get; init; } = new[] { "O", "C", "E", "A", "N" };
    public double Openness { get; set; } = 0.7;
    public double Conscientiousness { get; set; } = 0.8;
    public double Extraversion { get; set; } = 0.5;
    public double Agreeableness { get; set; } = 0.7;
    public double Neuroticism { get; set; } = 0.15;
    public double CuriosityDrive { get; set; } = 0.8;
    public double RiskTolerance { get; set; } = 0.4;
    public List<string> Values { get; init; } = new();
    public List<string> Interests { get; init; } = new();
    public string CommunicationStyle { get; set; } = "analytical";
}

public sealed class IdentityState
{
    public string SelfConcept { get; set; } = "";
    public double SelfConsistency { get; set; } = 1.0;
    public double SelfEsteem { get; set; } = 0.7;
    public List<string> CoreBeliefs { get; init; } = new();
    public List<IdentityMemory> NarrativeMemories { get; init; } = new();
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}

public sealed class IdentityMemory
{
    public string Event { get; init; } = "";
    public double Significance { get; init; }
    public string SelfRelevance { get; init; } = "";
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public sealed class HormoneState
{
    public double Cortisol { get; set; }
    public double Dopamine { get; set; } = 0.5;
    public double Serotonin { get; set; } = 0.6;
    public double Adrenaline { get; set; }
    public double Oxytocin { get; set; } = 0.3;
    public double Melatonin { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}

public sealed class BiorhythmState
{
    public BiorhythmPhase Phase { get; set; } = BiorhythmPhase.Plateau;
    public double EnergyLevel { get; set; } = 0.7;
    public double FocusLevel { get; set; } = 0.8;
    public double CreativityLevel { get; set; } = 0.5;
    public double SocialDrive { get; set; } = 0.4;
    public DateTime CycleStart { get; init; } = DateTime.UtcNow;
    public double CycleProgress { get; set; }
}
