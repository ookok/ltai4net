using System.Text.Json.Serialization;

namespace LTAI.Agent.Models;

public record RoutingCandidate
{
    public string Provider { get; init; } = "";
    public string Model { get; init; } = "";
    public string TaskType { get; init; } = "general";
    public Dictionary<string, double> Metrics { get; init; } = new();
    public Dictionary<string, string> Metadata { get; init; } = new();
}

public record RoutingDecision
{
    public string Provider { get; init; } = "";
    public string Model { get; init; } = "";
    public string Strategy { get; init; } = "";
    public double Score { get; init; }
    public Dictionary<string, double> Scores { get; init; } = new();
    public Dictionary<string, object?> Metadata { get; init; } = new();
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public record LearnedProfile
{
    public string Provider { get; init; } = "";
    public int ContextLength { get; init; } = 32768;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ReasoningCapable { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ToolCallCapable { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool StructuredOutput { get; init; }
    public double CostScore { get; init; } = 0.5;
    public double CapabilityScore { get; init; } = 0.5;
}

public record RoutingWeight
{
    public string TaskType { get; init; } = "";
    public string Provider { get; init; } = "";
    public double Weight { get; set; } = 1.0;
    public double SuccessRate { get; set; }
    public double AvgLatencyMs { get; set; }
    public int SampleCount { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}

public class BetaBelief
{
    public double Alpha { get; set; } = 1.0;
    public double Beta { get; set; } = 1.0;
    public double PriorAlpha { get; }
    public double PriorBeta { get; }

    public BetaBelief(double priorAlpha = 2.0, double priorBeta = 1.0)
    {
        Alpha = priorAlpha;
        Beta = priorBeta;
        PriorAlpha = priorAlpha;
        PriorBeta = priorBeta;
    }

    public double Sample(Random rng)
    {
        return SampleBeta(rng, Alpha, Beta);
    }

    public void Observe(bool success)
    {
        if (success) Alpha += 1.0;
        else Beta += 1.0;
    }

    public void Decay(double rate = 0.05)
    {
        Alpha = PriorAlpha + (Alpha - PriorAlpha) * (1.0 - rate);
        Beta = PriorBeta + (Beta - PriorBeta) * (1.0 - rate);
    }

    public double Mean => Alpha / (Alpha + Beta);

    public static double SampleBeta(Random rng, double alpha, double beta)
    {
        var x = SampleGamma(rng, alpha);
        var y = SampleGamma(rng, beta);
        return x / (x + y);
    }

    private static double SampleGamma(Random rng, double shape)
    {
        if (shape < 1.0)
        {
            var u = rng.NextDouble();
            return SampleGamma(rng, shape + 1.0) * Math.Pow(u, 1.0 / shape);
        }

        var d = shape - 1.0 / 3.0;
        var c = 1.0 / Math.Sqrt(9.0 * d);

        while (true)
        {
            double x, v;
            do
            {
                x = SampleNormal(rng);
                v = 1.0 + c * x;
            } while (v <= 0);

            v = v * v * v;
            var u = rng.NextDouble();

            if (u < 1.0 - 0.0331 * (x * x) * (x * x))
                return d * v;

            if (Math.Log(u) < 0.5 * x * x + d * (1.0 - v + Math.Log(v)))
                return d * v;
        }
    }

    private static double SampleNormal(Random rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BreakerState
{
    Closed,
    Open,
    HalfOpen
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GateState
{
    Accept,
    PredictOnly,
    Reject,
    Recalibrate
}

public record BreakerStats
{
    public string Provider { get; init; } = "";
    public BreakerState State { get; init; } = BreakerState.Closed;
    public int FailureCount { get; set; }
    public int SuccessCount { get; set; }
    public int ConsecutiveFailures { get; set; }
    public DateTime? LastFailureTime { get; set; }
    public DateTime? LastSuccessTime { get; set; }
    public DateTime? TrippedAt { get; set; }
    public int TripCount { get; set; }
    public int TotalBlocked { get; set; }
}

public record CoherenceDecision
{
    public GateState State { get; init; }
    public double Confidence { get; init; }
    public Dictionary<string, double> Scores { get; init; } = new();
    public string Reason { get; init; } = "";
    public int Depth { get; init; }
    public double DataCompleteness { get; init; }
    public bool RequiresRecalibration { get; init; }
    public List<string> RecalibrationHints { get; init; } = new();
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    public bool ShouldSimulate =>
        State == GateState.Accept || State == GateState.PredictOnly;

    public bool IsSafe =>
        State == GateState.Accept || State == GateState.PredictOnly;
}

public record ElectionSnapshot
{
    public List<ProviderScore> Scores { get; init; } = new();
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string CandidatesHash { get; init; } = "";
}

public record ProviderScore
{
    public string Provider { get; init; } = "";
    public bool Alive { get; set; } = true;
    public bool IsFree { get; set; }
    public Dictionary<string, double> Scores { get; init; } = new();
    public double Total { get; set; }
    public double LpoScore { get; set; }
    public double Latency { get; set; }
    public double SuccessRate { get; set; }
    public double CapabilityMatch { get; set; }
    public double CostYuanPer1K { get; set; }
    public double AvgLatencyMs { get; set; }
}

public record StrategicPrinciple
{
    public string Id { get; init; } = "";
    public string Principle { get; init; } = "";
    public string Category { get; init; } = "";
    public List<string> SourceTraces { get; init; } = new();
    public int SuccessEvidence { get; set; }
    public int FailureEvidence { get; set; }
    public double ApplicabilityScore { get; set; }
    public DateTime LastUsed { get; set; } = DateTime.UtcNow;
    public string EmbeddingHint { get; init; } = "";
}

public record DistillationResult
{
    public int TracesProcessed { get; init; }
    public int PrinciplesDistilled { get; init; }
    public int PrinciplesReinforced { get; init; }
    public double DurationMs { get; init; }
}

public record PromptVariant
{
    public string Id { get; init; } = "";
    public string Text { get; init; } = "";
    public double Alpha { get; set; } = 3.0;
    public double Beta { get; set; } = 3.0;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModelTierRank
{
    Pro,
    Mid,
    Flash,
    Eliminated
}

public class ModelRanking
{
    public string Provider { get; set; } = "";
    public double EloRating { get; set; } = 1200;
    public ModelTierRank Tier { get; set; } = ModelTierRank.Mid;
    public int Matches { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
    public int WinStreak { get; set; }
    public int LoseStreak { get; set; }
    public double EmAQuality { get; set; } = 0.5;
    public double EmASafety { get; set; } = 0.5;
    public double AvgLatencyMs { get; set; }
    public double AvgCostYuan { get; set; }
    public DateTime LastMatch { get; set; } = DateTime.UtcNow;
    public DateTime? EliminatedAt { get; set; }

    public double WinRate => Matches > 0 ? (double)Wins / Matches : 0;
    public bool IsEstablished => Matches >= 10;
    public bool IsEliminated => Tier == ModelTierRank.Eliminated;
    public double CooldownRemainingHours =>
        EliminatedAt == null ? 0 : Math.Max(0, 48 - (DateTime.UtcNow - EliminatedAt.Value).TotalHours);
    public bool CanRequalify => IsEliminated && CooldownRemainingHours <= 0;

    public void ToDict() { }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReasoningTier
{
    NonThink,
    ThinkHigh,
    ThinkMax
}

public class ReasoningBudget
{
    public int ThinkingTokens { get; set; }
    public ReasoningTier Tier { get; set; }
    public int DeepProbeDepth { get; set; }
    public int SelfPlayRounds { get; set; }
    public int AggregateModels { get; set; }
    public string ModelTier { get; set; } = "flash";
    public int ContextAvailable { get; set; }
    public int ContextAllocated { get; set; }
    public int ContextRemaining { get; set; }
    public double EstimatedLatencyMs { get; set; }
    public double UserPatienceMs { get; set; }
    public double TaskComplexity { get; set; }
    public double ConversationRhythm { get; set; }
    public int ActualTokensUsed { get; set; }
    public double BudgetEfficiency { get; set; }
}

public class SessionState
{
    public string SessionId { get; set; } = "";
    public string? BoundModel { get; set; }
    public DateTime BoundSince { get; set; } = DateTime.UtcNow;
    public int TurnCount { get; set; }
    public int ConsecutiveTurns { get; set; }
    public int SwitchCount { get; set; }
    public List<string> SwitchHistory { get; set; } = new();
    public string LastTaskType { get; set; } = "general";
    public string? UserPreference { get; set; }
}

public class CrossSessionMemory
{
    public string UserId { get; set; } = "";
    public List<MemoryEntry> Memories { get; set; } = new();
}

public class MemoryEntry
{
    public string Type { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class ReviewResult
{
    public bool Passed { get; set; }
    public string Reason { get; set; } = "";
    public double Confidence { get; set; }
}

public class RebuttalRound
{
    public int RoundNum { get; set; }
    public string OriginalAnswer { get; set; } = "";
    public List<string> CounterArguments { get; set; } = new();
    public string RevisedAnswer { get; set; } = "";
    public List<string> ChangesMade { get; set; } = new();
    public double JaccardToPrevious { get; set; }
    public int TokensSpent { get; set; }
    public double LatencyMs { get; set; }
}

public class SelfPlayResult
{
    public string OriginalAnswer { get; set; } = "";
    public string FinalAnswer { get; set; } = "";
    public List<RebuttalRound> Rounds { get; set; } = new();
    public string Status { get; set; } = "";
    public int TotalTokens { get; set; }
    public double TotalLatencyMs { get; set; }
    public int ConvergenceRound { get; set; }
    public double DepthGain { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StreamEventKind
{
    FlashToken,
    EarlyDispatch,
    FlashComplete,
    ProInsight,
    ProComplete,
    WeavePoint,
    Error,
    Meta
}

public class StreamEvent
{
    public StreamEventKind Kind { get; set; }
    public string Text { get; set; } = "";
    public string Provider { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public int Sequence { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
}

public class ConcurrentResult
{
    public string FlashOutput { get; set; } = "";
    public string ProOutput { get; set; } = "";
    public string FusedOutput { get; set; } = "";
    public List<StreamEvent> Events { get; set; } = new();
    public double FlashLatencyMs { get; set; }
    public double ProLatencyMs { get; set; }
    public int FlashTokens { get; set; }
    public int ProTokens { get; set; }
    public int WeaveCount { get; set; }
}

public class EmotionVector
{
    public double Valence { get; set; }
    public double Arousal { get; set; }
    public double Dominance { get; set; }
    public string? PrimaryEmotion { get; set; }
    public string? SecondaryEmotion { get; set; }
    public string? TertiaryEmotion { get; set; }
    public bool IsUrgent => Arousal > 0.7;
    public bool IsNegative => Valence < 0.3;
    public bool IsConfused => Dominance < 0.3 && Arousal > 0.5;
    public bool IsPositive => Valence > 0.7;
    public bool IsNeutral => Valence is >= 0.3 and <= 0.7;
}

public class TriageResult
{
    public double Complexity { get; set; }
    public string Label { get; set; } = "chat";
    public EmotionVector Emotion { get; set; } = new();
    public string? MatchedReflex { get; set; }
    public double Confidence { get; set; }
    public List<string> PredictedNeeds { get; set; } = new();
}

public class ReflexRule
{
    public string Pattern { get; set; } = "";
    public string Response { get; set; } = "";
    public int HitCount { get; set; }
    public DateTime LastHit { get; set; } = DateTime.UtcNow;
    public bool IsCold => HitCount < 3 || (DateTime.UtcNow - LastHit).TotalDays > 7;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NeedType
{
    Tool,
    Knowledge,
    File,
    Sql,
    Search,
    Human,
    Question
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DelegateLevel
{
    FireAndForget,
    NeedResult,
    NeedApproval
}

public class Need
{
    public string Id { get; set; } = "";
    public NeedType Type { get; set; }
    public DelegateLevel Level { get; set; }
    public string Description { get; set; } = "";
    public Dictionary<string, string> Params { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool Fulfilled { get; set; }
    public string? Result { get; set; }
    public string? Error { get; set; }
    public double ElapsedMs { get; set; }
}

public class CollaborationResult
{
    public string Text { get; set; } = "";
    public List<Need> Needs { get; set; } = new();
    public int Rounds { get; set; }
    public int TotalTokens { get; set; }
    public double TotalLatencyMs { get; set; }
    public List<string> Insights { get; set; } = new();
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BudgetStateEnum
{
    Normal,
    Warning,
    Throttled,
    Open
}

public class BudgetAllocation
{
    public int TopK { get; set; }
    public int MaxTokens { get; set; }
    public bool Aggregate { get; set; }
}

public class PredictedQuery
{
    public string QueryText { get; set; } = "";
    public double Probability { get; set; }
    public string Source { get; set; } = "";
    public double ExpectedLatencySavingMs { get; set; }
}

public class DensityReport
{
    public double TotalScore { get; set; }
    public double InfoDensity { get; set; }
    public double StructuralComplexity { get; set; }
    public double CausalSignals { get; set; }
    public double Novelty { get; set; }
    public double Utility { get; set; }
    public double Completeness { get; set; }
    public double IbMutualInfo { get; set; }
    public Dictionary<string, double> SubScores { get; set; } = new();
    public string Verdict { get; set; } = "";
    public List<string> Suggestions { get; set; } = new();
}

// Connection Pool
public record PoolConfig
{
    public int MaxConnectionsPerHost { get; init; } = 10;
    public int MaxTotalConnections { get; init; } = 50;
    public int KeepaliveTimeoutSeconds { get; init; } = 30;
    public int DnsCacheTtlSeconds { get; init; } = 300;
    public bool EnableTcpFastOpen { get; init; } = true;
}

public record PoolStats
{
    public int ActiveConnections { get; set; }
    public int IdleConnections { get; set; }
    public int TotalRequests { get; set; }
    public int TotalFailures { get; set; }
    public double AvgLatencyMs { get; set; }
    public double ReusedRatio { get; set; }
    public double UptimeSeconds { get; set; }
    public int Recreations { get; set; }
}

public record ProviderPoolStats
{
    public string Provider { get; init; } = "";
    public int Requests { get; set; }
    public int Failures { get; set; }
    public List<double> Latencies { get; set; } = new();
    public List<bool> ErrorFlags { get; set; } = new();
    public double AvgLatencyMs => Latencies.Count > 0 ? Latencies.Average() : 0;
    public double SuccessRate => Requests > 0 ? (double)(Requests - Failures) / Requests : 1.0;
}

// Continuous Consciousness
public record TaskBoundary
{
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string Reason { get; init; } = "";
    public double Confidence { get; init; }
    public string ContextSnapshot { get; init; } = "";
    public List<string> KeyPoints { get; init; } = new();
}

public class MemoryBlock
{
    public string Id { get; set; } = "";
    public string Content { get; set; } = "";
    public int OriginalLength { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public DateTime LastAccessed { get; set; } = DateTime.UtcNow;
    public int AccessCount { get; set; }
    public double RelevanceScore { get; set; }
    public List<string> Topics { get; set; } = new();
    public string TaskType { get; set; } = "general";
}

public record RelevanceScore
{
    public string BlockId { get; init; } = "";
    public double Score { get; init; }
    public double KeywordScore { get; init; }
    public double TemporalScore { get; init; }
    public double TaskTypeMatch { get; init; }
}

// Fluid Collective
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TraceType
{
    Insight,
    CounterArgument,
    Hypothesis,
    Decision,
    Gap,
    Pattern
}

public class StigmergicTrace
{
    public string TraceId { get; set; } = "";
    public string Model { get; set; } = "";
    public TraceType TraceType { get; set; }
    public string Content { get; set; } = "";
    public string Domain { get; set; } = "";
    public double Confidence { get; set; }
    public double DepthGrade { get; set; }
    public List<string> ParentTraceIds { get; set; } = new();
    public DateTime DepositedAt { get; set; } = DateTime.UtcNow;
    public int AccessCount { get; set; }
    public double DecayFactor { get; set; } = 1.0;

    public double Relevance => Confidence * DepthGrade * DecayFactor *
        Math.Exp(-(DateTime.UtcNow - DepositedAt).TotalDays / 7.0);

    public void Access() { AccessCount++; DecayFactor = Math.Min(1.0, DecayFactor + 0.05); }
    public void Evaporate(double rate) { DecayFactor = Math.Max(0.01, DecayFactor * (1 - rate)); }
}

public class TransientFormation
{
    public string FormationId { get; set; } = "";
    public List<string> Models { get; set; } = new();
    public string TaskDescription { get; set; } = "";
    public string FormationStrategy { get; set; } = "";
    public int BudgetTokens { get; set; }
    public string Status { get; set; } = "active";
    public double CreatedAt { get; set; }
    public List<string> TraceIds { get; set; } = new();
    public List<Dictionary<string, string>> SubTaskResults { get; set; } = new();
}

public class MobilityBudget
{
    public int TotalTokens { get; set; }
    public int ModelSwitches { get; set; }
    public int UniqueModelsUsed { get; set; }
    public double MobilityRatio => UniqueModelsUsed > 0 ? (double)ModelSwitches / UniqueModelsUsed : 0;
    public double CostYuan { get; set; }
    public double QualityEstimate { get; set; }
    public string Strategy { get; set; } = "";
}

// Free Model Pool
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PoolModelStatus
{
    Healthy,
    Degraded,
    RateLimited,
    Quarantined,
    Unknown
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResearchRole
{
    DataHunter,
    Coder,
    IdeaAgent,
    Reviewer
}

public class FreeModelProfile
{
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public bool IsFree { get; set; }
    public double CodingScore { get; set; }
    public double ReasoningScore { get; set; }
    public double ReadingScore { get; set; }
    public double InstructionScore { get; set; }
    public double SearchScore { get; set; }
    public int ContextWindow { get; set; } = 32768;
    public int RpmLimit { get; set; } = 60;
    public int RpdLimit { get; set; } = 1000;
    public int ConcurrentLimit { get; set; } = 5;
    public PoolModelStatus Status { get; set; } = PoolModelStatus.Unknown;
    public int FailureStreak { get; set; }
    public double EmaLatencyMs { get; set; } = 2000;
    public int TotalCalls { get; set; }
    public DateTime? QuarantinedUntil { get; set; }
    public Queue<DateTime> RecentRequests { get; set; } = new();
}

// Segmented KV Compressor
public record KVSegment
{
    public string Id { get; init; } = "";
    public int StartIndex { get; init; }
    public int EndIndex { get; init; }
    public int MessageCount { get; init; }
    public string KvtHash { get; init; } = "";
    public string KvtText { get; init; } = "";
    public List<string> DecisionKeys { get; init; } = new();
}

public record KVTail
{
    public string SourceSegmentId { get; init; } = "";
    public string Text { get; init; } = "";
    public string Hash { get; init; } = "";
    public List<string> DecisionSignatures { get; init; } = new();
    public int TokenCount { get; init; }
}

// Self Improver
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DefectSeverity
{
    Critical,
    High,
    Medium,
    Low
}

public class Defect
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "";
    public DefectSeverity Severity { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string FilePath { get; set; } = "";
    public int LineNumber { get; set; }
    public string Evidence { get; set; } = "";
    public string SuggestedFix { get; set; } = "";
    public double Confidence { get; set; }
}

public class Innovation
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    public string InspiredBy { get; set; } = "";
    public string ImplementationPlan { get; set; } = "";
    public double EstimatedImpact { get; set; }
    public string Complexity { get; set; } = "medium";
    public string? CodePatch { get; set; }
    public bool Validated { get; set; }
    public bool TestPassed { get; set; }
    public string? GitBranch { get; set; }
    public string? GitCommit { get; set; }
}

// Debug Loop
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DebugLevel
{
    Analyze,
    SemiAuto,
    Full,
    Closed
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AttemptResult
{
    Fixed,
    Partial,
    Worse,
    Unchanged,
    Hitl
}

public class ErrorSnapshot
{
    public string Id { get; set; } = "";
    public string ExceptionType { get; set; } = "";
    public string ExceptionMessage { get; set; } = "";
    public string TracebackText { get; set; } = "";
    public string FilePath { get; set; } = "";
    public int LineNumber { get; set; }
    public string FunctionName { get; set; } = "";
    public string SourceContext { get; set; } = "";
    public Dictionary<string, string> LocalsSnapshot { get; set; } = new();
    public string ProjectRoot { get; set; } = "";
    public string? TestName { get; set; }
}

public class FixAttempt
{
    public int AttemptNumber { get; set; }
    public ErrorSnapshot Error { get; set; } = new();
    public string GeneratedPatch { get; set; } = "";
    public string AppliedFile { get; set; } = "";
    public int AppliedLine { get; set; }
    public AttemptResult Result { get; set; }
    public string? NewError { get; set; }
    public string LlmProvider { get; set; } = "";
    public int LlmTokens { get; set; }
    public double DurationMs { get; set; }
    public string? GitCommit { get; set; }
}

public class DebugSession
{
    public string Id { get; set; } = "";
    public string Target { get; set; } = "";
    public string Args { get; set; } = "";
    public DebugLevel Level { get; set; }
    public int MaxAttempts { get; set; } = 3;
    public List<FixAttempt> Attempts { get; set; } = new();
    public bool Fixed { get; set; }
    public double TotalDurationMs { get; set; }
    public bool Escalated { get; set; }
}

// Error Interceptor
public class InterceptedError
{
    public string Id { get; set; } = "";
    public string ExceptionType { get; set; } = "";
    public string ExceptionMessage { get; set; } = "";
    public string TracebackText { get; set; } = "";
    public string FilePath { get; set; } = "";
    public int LineNumber { get; set; }
    public string FunctionName { get; set; } = "";
    public string SourceContext { get; set; } = "";
    public DateTime CaughtAt { get; set; } = DateTime.UtcNow;
    public string? CaughtByFile { get; set; }
    public int CaughtByLine { get; set; }
    public string? ThreadName { get; set; }
    public string? TaskName { get; set; }
    public long MemoryKb { get; set; }
}

public record LineSnapshot
{
    public string File { get; init; } = "";
    public int Line { get; init; }
    public string Function { get; init; } = "";
    public Dictionary<string, string> Locals { get; init; } = new();
    public int Depth { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public class ContextAnalysis
{
    public List<string> DependencyIssues { get; set; } = new();
    public List<string> TypeErrors { get; set; } = new();
    public Dictionary<string, DateTime> GitBlameInfo { get; set; } = new();
    public List<string> TestCoverageGaps { get; set; } = new();
    public List<string> SimilarFixes { get; set; } = new();
}

public class Lesson
{
    public string Id { get; set; } = "";
    public string Pattern { get; set; } = "";
    public string Fix { get; set; } = "";
    public int Occurrences { get; set; }
    public double AutoFixConfidence { get; set; } = 0.5;
    public DateTime LastUsed { get; set; } = DateTime.UtcNow;
}

public class PreAnalysisIssue
{
    public string FilePath { get; set; } = "";
    public string Severity { get; set; } = "";
    public int LineNumber { get; set; }
    public string Category { get; set; } = "";
    public string Description { get; set; } = "";
    public string? SuggestedFix { get; set; }
}
