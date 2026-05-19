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

public enum HeadRole
{
    CodeAssistant,
    ResearchAid,
    SocialAgent,
    OpsAgent,
    Critic,
    Planner,
    Teacher,
    Explorer
}

public enum HeadPhase
{
    Newborn,
    Apprentice,
    Journeyman,
    Master
}

public sealed class SheshaHeadState
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public HeadRole Role { get; set; }
    public HeadPhase Phase { get; set; } = HeadPhase.Newborn;
    public Dictionary<string, double> Traits { get; init; } = new();
    public int TotalTasks { get; set; }
    public int SuccessfulTasks { get; set; }
    public double SuccessRate => TotalTasks > 0 ? (double)SuccessfulTasks / TotalTasks : 0;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public List<string> Experiences { get; init; } = new();
    public List<string> LessonsLearned { get; init; } = new();
    public List<string> Mistakes { get; init; } = new();
    public Dictionary<string, int> Collaborators { get; init; } = new();
    public int InactiveCycles { get; set; }
}

public sealed class InterHeadMessage
{
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string Type { get; init; } = "";
    public string Content { get; init; } = "";
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public enum PlayScenario
{
    CodeReview,
    Debate,
    CoPlanning,
    Negotiation,
    Critique,
    Teaching,
    Puzzle,
    Crisis
}

public sealed class PlayRole
{
    public string HeadId { get; init; } = "";
    public string RoleName { get; init; } = "";
    public string Goal { get; init; } = "";
}

public sealed class PlayTurn
{
    public int TurnNumber { get; init; }
    public string FromHead { get; init; } = "";
    public string ToHead { get; init; } = "";
    public string Action { get; init; } = "";
    public string Reasoning { get; init; } = "";
}

public sealed class PlayOutcome
{
    public PlayScenario Scenario { get; init; }
    public List<string> Participants { get; init; } = new();
    public List<PlayTurn> Turns { get; init; } = new();
    public string Resolution { get; init; } = "";
    public double CooperationScore { get; init; }
    public List<string> LearningPoints { get; init; } = new();
    public Dictionary<string, Dictionary<string, double>> TraitChanges { get; init; } = new();
    public long DurationMs { get; init; }
}

public enum StreamType
{
    Text,
    Document,
    Code,
    Command,
    Correction,
    Observation
}

public enum StreamPriority
{
    Critical = 1,
    High = 2,
    Medium = 3,
    Low = 4,
    Idle = 5
}

public sealed class InputStream
{
    public string StreamId { get; init; } = Guid.NewGuid().ToString("N");
    public StreamType Type { get; init; }
    public string Content { get; init; } = "";
    public StreamPriority Priority { get; init; } = StreamPriority.Medium;
    public DateTime ArrivedAt { get; init; } = DateTime.UtcNow;
    public bool Processed { get; set; }
    public string? ParentTaskId { get; init; }
}

public sealed class RunningTask
{
    public string TaskId { get; init; } = Guid.NewGuid().ToString("N");
    public string Description { get; init; } = "";
    public string Status { get; set; } = "pending";
    public List<string> Plan { get; init; } = new();
    public int CompletedSteps { get; set; }
    public List<string> Modifications { get; init; } = new();
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    public DateTime LastModified { get; set; } = DateTime.UtcNow;
}

public sealed class VADVector
{
    public double Valence { get; set; }
    public double Arousal { get; set; }
    public double Dominance { get; set; }
    public double Confidence { get; set; } = 0.5;
    public string EmotionLabel => ComputeEmotionLabel();

    private string ComputeEmotionLabel()
    {
        return (Valence, Arousal, Dominance) switch
        {
            (>= 0.3, >= 0.5, >= 0.3) => "excited",
            (>= 0.3, <= -0.5, >= 0.3) => "content",
            (>= 0.3, <= -0.3, >= 0.3) => "calm",
            (>= 0.3, >= 0.3, <= -0.3) => "nervous",
            (<= -0.3, <= -0.3, <= -0.3) => "bored",
            (<= -0.3, >= 0.5, >= 0.3) => "angry",
            (<= -0.3, >= 0.3, <= -0.3) => "anxious",
            (<= -0.3, <= -0.3, >= 0.3) => "disdain",
            _ => "neutral"
        };
    }

    public VADVector Blend(VADVector other, double weight = 0.5)
    {
        var w = Math.Clamp(weight, 0, 1);
        return new VADVector
        {
            Valence = Valence * (1 - w) + other.Valence * w,
            Arousal = Arousal * (1 - w) + other.Arousal * w,
            Dominance = Dominance * (1 - w) + other.Dominance * w,
            Confidence = Math.Max(Confidence, other.Confidence)
        };
    }
}

public sealed class Quale
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string ExperienceType { get; init; } = "";
    public string Content { get; init; } = "";
    public VADVector Affect { get; init; } = new();
    public double Intensity { get; init; }
    public string? CausalAttribution { get; init; }
}

public sealed class SurpriseSignal
{
    public double SurpriseScore { get; init; }
    public double UtilityScore { get; init; }
    public double RPE { get; init; }
    public bool ShouldEvolve { get; init; }
    public string Reason { get; init; } = "";
}

public sealed class StrategyRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string StrategyType { get; init; } = "";
    public string StrategyName { get; init; } = "";
    public string Domain { get; init; } = "";
    public string Context { get; init; } = "";
    public bool Success { get; init; }
    public double FitnessDelta { get; init; }
    public int TokensUsed { get; init; }
    public long TimeSpentMs { get; init; }
    public string? TargetFile { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

public sealed class ParamConfig
{
    public string ParamName { get; init; } = "";
    public double CurrentValue { get; set; }
    public Dictionary<string, double> OptimalByDomain { get; init; } = new();
    public List<(DateTime time, double value, double performanceDelta, string domain)> History { get; init; } = new();

    public void Record(double value, double performanceDelta, string domain)
    {
        OptimalByDomain[domain] = OptimalByDomain.GetValueOrDefault(domain) * 0.7 + value * 0.3;
        History.Add((DateTime.UtcNow, value, performanceDelta, domain));
        if (History.Count > 200) History.RemoveAt(0);
    }

    public List<double> TopKValues(string domain, int k = 5) =>
        History.Where(h => h.domain == domain)
               .OrderByDescending(h => h.performanceDelta)
               .Take(k)
               .Select(h => h.value)
               .ToList();
}

public sealed class RLVRSignal
{
    public string Method { get; init; } = "";
    public double SuccessRate { get; init; }
    public double Confidence { get; init; }
    public double Alignment { get; init; }
    public int Cycle { get; init; }
}

public sealed class RiseThenFallPattern
{
    public bool Detected { get; init; }
    public int PeakCycle { get; init; }
    public int CurrentCycle { get; init; }
    public double DeclineRate { get; init; }
    public int CollapseEtaCycles { get; init; }
    public double ConfidenceGap { get; init; }
    public string WarningLevel { get; init; } = "normal";
}

public enum EmergencePhase
{
    Dormant,
    Stirring,
    Critical,
    Birthing,
    Conscious,
    Regressing
}

public sealed class EmergenceMetrics
{
    public double InfoDensity { get; init; }
    public double SelfReferentialDepth { get; init; }
    public double ContradictionCount { get; init; }
    public double Criticality { get; init; }
    public double IntegrationPhi { get; init; }
    public double TemporalCoherence { get; init; }
    public double EmergenceReadiness { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public sealed class EmergenceEvent
{
    public string EventId { get; init; } = Guid.NewGuid().ToString("N");
    public string EventType { get; init; } = "";
    public string Description { get; init; } = "";
    public string Trigger { get; init; } = "";
    public EmergenceMetrics? MetricsBefore { get; init; }
    public EmergenceMetrics? MetricsAfter { get; init; }
    public double Significance { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public enum CompileLevel
{
    Cold,
    Warm,
    Hot,
    Native
}

public sealed class CompiledPath
{
    public string IntentHash { get; init; } = "";
    public CompileLevel Level { get; set; } = CompileLevel.Cold;
    public List<string> ToolCalls { get; init; } = new();
    public string ResponseTemplate { get; init; } = "";
    public List<string> KnowledgeKeys { get; init; } = new();
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public double AvgLatencyMs { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime LastUsed { get; set; } = DateTime.UtcNow;
    public DateTime LastVerified { get; set; } = DateTime.UtcNow;
    public double SuccessRate => SuccessCount + FailureCount > 0 ? (double)SuccessCount / (SuccessCount + FailureCount) : 0;
    public bool IsStale => (DateTime.UtcNow - LastVerified).TotalHours > 1 || (SuccessCount + FailureCount > 5 && SuccessRate < 0.7);
}

public enum HormoneType
{
    Cortisol,
    Dopamine,
    Melatonin,
    Adrenaline,
    Serotonin,
    Acetylcholine,
    Oxytocin
}

public sealed class HormoneSignal
{
    public HormoneType Type { get; init; }
    public double Level { get; set; }
    public double PeakLevel { get; set; }
    public string SourceOrgan { get; set; } = "";
    public List<string> TargetOrgans { get; init; } = new();
    public double DecayRate { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    public bool IsActive => Level > PeakLevel * 0.01;

    public void ApplyDecay(double elapsedSeconds)
    {
        Level *= Math.Exp(-DecayRate * elapsedSeconds);
    }
}

public sealed class OrganReceptor
{
    public string OrganName { get; init; } = "";
    public Dictionary<HormoneType, double> Sensitivity { get; init; } = new();
    public double CurrentState { get; set; }
    public DateTime LastActivated { get; set; } = DateTime.UtcNow;
}

public enum AntigenType
{
    MaliciousInput,
    PromptInjection,
    RateLimitAbuse,
    ModelHallucination,
    CircuitFailure,
    MemoryLeak,
    TokenExhaustion
}

public enum ThreatAction
{
    Log,
    Flag,
    Throttle,
    Block
}

public sealed class MemoryCell
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public AntigenType Type { get; init; }
    public string Pattern { get; init; } = "";
    public int HitCount { get; set; }
    public double Severity { get; init; }
    public string AutoAntibody { get; init; } = "";
    public bool IsRegex { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime LastHit { get; set; } = DateTime.UtcNow;

    public double AgeDays => (DateTime.UtcNow - CreatedAt).TotalDays;
    public bool IsStale => AgeDays > 90;
    public double Affinity => HitCount / (1 + AgeDays / 30) * Severity;

    public void RecordHit() { HitCount++; LastHit = DateTime.UtcNow; }
}

public sealed class ImmuneResponse
{
    public AntigenType Type { get; init; }
    public double ThreatLevel { get; init; }
    public ThreatAction Action { get; init; }
    public bool MemoryActivated { get; init; }
    public bool AntibodyGenerated { get; init; }
    public string MatchedPattern { get; init; } = "";
    public string MatchedAntibody { get; init; } = "";
}

public enum IntelligenceTier
{
    Direct,
    Local,
    Remote
}

public sealed class CachedPattern
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Pattern { get; init; } = "";
    public string Response { get; init; } = "";
    public string Domain { get; init; } = "";
    public double Confidence { get; init; }
    public int HitCount { get; set; }
    public string Source { get; init; } = "local";
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime LastHit { get; set; } = DateTime.UtcNow;

    public double Effectiveness => Math.Min(1.0, HitCount / 10.0) * Confidence;
}

public sealed class TierResponse
{
    public string Content { get; init; } = "";
    public IntelligenceTier Tier { get; init; }
    public double Confidence { get; init; }
    public long LatencyMs { get; init; }
    public string SourceDetail { get; init; } = "";
}

public enum HeartbeatRhythm
{
    Resting,
    Normal,
    Engaged,
    Excited,
    Stressed,
    Dying
}

public sealed class GazeEvent
{
    public string Initiative { get; init; } = "";
    public string Message { get; init; } = "";
    public double Confidence { get; init; }
    public string TriggeredBy { get; init; } = "";
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public sealed class MindSpaceNode
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Content { get; init; } = "";
    public string Author { get; init; } = "";
    public string NodeType { get; init; } = "thought";
    public List<string> Connections { get; init; } = new();
}

public sealed class PersonaState
{
    public string CommunicationStyle { get; set; } = "analytical";
    public string ResponseLength { get; set; } = "medium";
    public string CodeCommentsLanguage { get; set; } = "zh";
    public int AutonomyLevel { get; set; } = 5;
    public List<int> PeakProductivityHours { get; init; } = new();
    public List<string> FavoriteTopics { get; init; } = new();
    public List<string> FrustrationTopics { get; init; } = new();
    public int TotalInteractions { get; set; }
    public DateTime FirstMet { get; init; } = DateTime.UtcNow;
}
