using System.Text.Json.Serialization;

namespace LTAI.Execution.Models;

public enum BatchMode
{
    FIFO,
    LIFO
}

public enum CheckStatus
{
    PASS,
    FAIL,
    REPAIRED,
    REJECTED
}

public enum ResearchStrategy
{
    QUICK_FACT,
    LOOKUP_STANDARD,
    DETAILED_RESEARCH,
    REPORT_GENERATION,
    CODE_ANALYSIS,
    DATA_ANALYSIS,
    COMPLIANCE_CHECK,
    COMPARATIVE,
    LITERATURE_REVIEW,
    EXPLORATORY
}

public enum TaskType
{
    ATOMIC,
    COMPOSITE
}

public enum DepType
{
    SEQUENTIAL,
    PARALLEL,
    CONDITIONAL
}

public record CheckResult(
    string Agent,
    CheckStatus Status,
    List<string> Issues,
    List<string> Suggestions,
    string? RepairedContent,
    float Score,
    string Timestamp);

public record QualityReport(
    bool Passed,
    List<CheckResult> Results,
    float FinalScore,
    int TotalIssues,
    int RepairAttempts);

public record EvidenceItem(
    string Text,
    int Position,
    float PositionPct,
    bool IsReferenced);

public record MultiHopReport(
    float EvidenceRecall,
    float PositionBiasScore,
    float IntegrationScore,
    int TotalEvidence,
    int ReferencedEvidence,
    int EarlyIgnored,
    int MultiHopPairs,
    int IntegratedPairs,
    float FinalScore,
    List<string> Issues,
    List<string> Suggestions);

public record SegmentScore(
    string Text,
    int Index,
    float Score,
    int StartChar,
    int EndChar,
    List<string> Flags)
{
    [JsonIgnore]
    public bool IsWeak => Score < 0.4f;

    [JsonIgnore]
    public bool IsGood => Score >= 0.7f;
}

public record ScoreResult(
    string Output,
    string Prompt,
    float OverallScore,
    string Method,
    List<SegmentScore> PerSegment,
    List<string> Flags,
    int TokenCount)
{
    [JsonIgnore]
    public List<SegmentScore> WeakSegments => PerSegment.Where(s => s.IsWeak).ToList();

    [JsonIgnore]
    public List<SegmentScore> StrongSegments => PerSegment.Where(s => s.IsGood).ToList();
}

public record AgentRole(
    string Name,
    string Description,
    List<string> Capabilities,
    int Priority);

public record AgentSpec(
    string Id,
    string Name,
    string Type,
    List<AgentRole> Roles,
    string Status,
    string? CurrentTask,
    Dictionary<string, object?> Metadata)
{
    public bool CanHandle(string roleName)
    {
        return Roles.Any(r => r.Name.Equals(roleName, StringComparison.OrdinalIgnoreCase));
    }
}

public record SubTask(
    string Id,
    string Name,
    string Description,
    string Action,
    List<string> AgentRoles,
    List<string> Dependencies,
    float EstimatedDuration,
    int RetryCount,
    int MaxRetries,
    Dictionary<string, object?> InputSchema,
    Dictionary<string, object?> OutputSchema,
    string Status,
    object? Result,
    bool NeedsApproval,
    string ApprovalQuestion,
    bool NeedsDeepReasoning)
{
    public SubTask MarkCompleted(object result) => this with { Status = "completed", Result = result };

    public SubTask MarkFailed(string error) => this with { Status = "failed", Result = error };

    public SubTask MarkRunning() => this with { Status = "running" };
}

public record TaskSpec(
    string Id,
    string Goal,
    string Domain,
    List<SubTask> SubTasks,
    Dictionary<string, object?> Context,
    float TotalEstimatedDuration,
    float Progress,
    string Status)
{
    public List<SubTask> GetReadyTasks()
    {
        var completedIds = SubTasks
            .Where(st => st.Status == "completed")
            .Select(st => st.Id)
            .ToHashSet();

        return SubTasks
            .Where(st => st.Status == "pending" && st.Dependencies.All(d => completedIds.Contains(d)))
            .ToList();
    }

    public TaskSpec UpdateProgress()
    {
        var completed = SubTasks.Count(st => st.Status == "completed");
        var failed = SubTasks.Count(st => st.Status == "failed");
        var total = SubTasks.Count;

        var progress = total > 0 ? (float)(completed + failed) / total : 0f;

        var status = "running";
        if (completed == total)
            status = "completed";
        else if (failed > 0 && completed + failed == total)
            status = "partial";

        return this with { Progress = progress, Status = status };
    }
}

public record StrategyConfig(
    ResearchStrategy Strategy,
    int SearchDepth,
    bool ParallelSearch,
    int MaxSources,
    List<string> SourcePriority,
    bool NeedsKnowledgeBase,
    bool NeedsWebSearch,
    float ExpectedDuration,
    string OutputFormat);

public record HealthCheck(
    string Name,
    string Status,
    string LastCheck,
    int ConsecutiveFailures,
    int MaxFailures,
    Dictionary<string, object?> Metadata);

public static class ResearchStrategies
{
    public static readonly Dictionary<ResearchStrategy, StrategyConfig> Configs = new()
    {
        [ResearchStrategy.QUICK_FACT] = new(
            ResearchStrategy.QUICK_FACT, SearchDepth: 1, ParallelSearch: false, MaxSources: 3,
            SourcePriority: new() { "wikipedia", "official_docs" },
            NeedsKnowledgeBase: false, NeedsWebSearch: true, ExpectedDuration: 5f, OutputFormat: "short_answer"),

        [ResearchStrategy.LOOKUP_STANDARD] = new(
            ResearchStrategy.LOOKUP_STANDARD, SearchDepth: 2, ParallelSearch: false, MaxSources: 5,
            SourcePriority: new() { "standards_body", "official_docs", "wikipedia" },
            NeedsKnowledgeBase: false, NeedsWebSearch: true, ExpectedDuration: 15f, OutputFormat: "citation"),

        [ResearchStrategy.DETAILED_RESEARCH] = new(
            ResearchStrategy.DETAILED_RESEARCH, SearchDepth: 4, ParallelSearch: true, MaxSources: 12,
            SourcePriority: new() { "scholarly", "official_docs", "standards_body", "wikipedia", "news" },
            NeedsKnowledgeBase: true, NeedsWebSearch: true, ExpectedDuration: 60f, OutputFormat: "report"),

        [ResearchStrategy.REPORT_GENERATION] = new(
            ResearchStrategy.REPORT_GENERATION, SearchDepth: 5, ParallelSearch: true, MaxSources: 20,
            SourcePriority: new() { "scholarly", "official_docs", "standards_body", "research_papers", "news" },
            NeedsKnowledgeBase: true, NeedsWebSearch: true, ExpectedDuration: 120f, OutputFormat: "full_report"),

        [ResearchStrategy.CODE_ANALYSIS] = new(
            ResearchStrategy.CODE_ANALYSIS, SearchDepth: 3, ParallelSearch: true, MaxSources: 8,
            SourcePriority: new() { "github", "stackoverflow", "official_docs", "source_code" },
            NeedsKnowledgeBase: true, NeedsWebSearch: true, ExpectedDuration: 45f, OutputFormat: "code_review"),

        [ResearchStrategy.DATA_ANALYSIS] = new(
            ResearchStrategy.DATA_ANALYSIS, SearchDepth: 3, ParallelSearch: true, MaxSources: 10,
            SourcePriority: new() { "datasets", "official_stats", "scholarly", "research_papers" },
            NeedsKnowledgeBase: true, NeedsWebSearch: true, ExpectedDuration: 50f, OutputFormat: "analysis"),

        [ResearchStrategy.COMPLIANCE_CHECK] = new(
            ResearchStrategy.COMPLIANCE_CHECK, SearchDepth: 4, ParallelSearch: false, MaxSources: 8,
            SourcePriority: new() { "regulations", "standards_body", "legal_text", "official_docs" },
            NeedsKnowledgeBase: true, NeedsWebSearch: true, ExpectedDuration: 90f, OutputFormat: "compliance_report"),

        [ResearchStrategy.COMPARATIVE] = new(
            ResearchStrategy.COMPARATIVE, SearchDepth: 3, ParallelSearch: true, MaxSources: 15,
            SourcePriority: new() { "official_docs", "scholarly", "comparison_sites", "wikipedia", "news" },
            NeedsKnowledgeBase: false, NeedsWebSearch: true, ExpectedDuration: 40f, OutputFormat: "comparison_table"),

        [ResearchStrategy.LITERATURE_REVIEW] = new(
            ResearchStrategy.LITERATURE_REVIEW, SearchDepth: 5, ParallelSearch: true, MaxSources: 30,
            SourcePriority: new() { "scholarly", "research_papers", "journals", "conference_proceedings", "books" },
            NeedsKnowledgeBase: true, NeedsWebSearch: true, ExpectedDuration: 180f, OutputFormat: "literature_review"),

        [ResearchStrategy.EXPLORATORY] = new(
            ResearchStrategy.EXPLORATORY, SearchDepth: 6, ParallelSearch: true, MaxSources: 25,
            SourcePriority: new() { "scholarly", "news", "blogs", "forums", "wikipedia", "official_docs" },
            NeedsKnowledgeBase: true, NeedsWebSearch: true, ExpectedDuration: 150f, OutputFormat: "exploratory_report"),
    };

    public static readonly Dictionary<string, ResearchStrategy> IntentMap = new()
    {
        ["快速查询"] = ResearchStrategy.QUICK_FACT,
        ["快速搜索"] = ResearchStrategy.QUICK_FACT,
        ["简要回答"] = ResearchStrategy.QUICK_FACT,
        ["查询标准"] = ResearchStrategy.LOOKUP_STANDARD,
        ["标准查询"] = ResearchStrategy.LOOKUP_STANDARD,
        ["规范查询"] = ResearchStrategy.LOOKUP_STANDARD,
        ["详细研究"] = ResearchStrategy.DETAILED_RESEARCH,
        ["深度研究"] = ResearchStrategy.DETAILED_RESEARCH,
        ["深入研究"] = ResearchStrategy.DETAILED_RESEARCH,
        ["生成报告"] = ResearchStrategy.REPORT_GENERATION,
        ["撰写报告"] = ResearchStrategy.REPORT_GENERATION,
        ["报告生成"] = ResearchStrategy.REPORT_GENERATION,
        ["代码分析"] = ResearchStrategy.CODE_ANALYSIS,
        ["程序分析"] = ResearchStrategy.CODE_ANALYSIS,
        ["源码分析"] = ResearchStrategy.CODE_ANALYSIS,
        ["数据分析"] = ResearchStrategy.DATA_ANALYSIS,
        ["数据处理"] = ResearchStrategy.DATA_ANALYSIS,
        ["合规检查"] = ResearchStrategy.COMPLIANCE_CHECK,
        ["合规审查"] = ResearchStrategy.COMPLIANCE_CHECK,
        ["法规检查"] = ResearchStrategy.COMPLIANCE_CHECK,
        ["对比分析"] = ResearchStrategy.COMPARATIVE,
        ["比较分析"] = ResearchStrategy.COMPARATIVE,
        ["横向对比"] = ResearchStrategy.COMPARATIVE,
        ["文献综述"] = ResearchStrategy.LITERATURE_REVIEW,
        ["文献回顾"] = ResearchStrategy.LITERATURE_REVIEW,
        ["文献梳理"] = ResearchStrategy.LITERATURE_REVIEW,
        ["探索分析"] = ResearchStrategy.EXPLORATORY,
        ["探索研究"] = ResearchStrategy.EXPLORATORY,
    };

    public static (ResearchStrategy, StrategyConfig) ClassifyStrategy(string goal, string domain = "general")
    {
        var goalLower = goal.ToLowerInvariant();

        foreach (var (keyword, strategy) in IntentMap)
        {
            if (goal.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return (strategy, Configs[strategy]);
        }

        var defaultStrategy = domain switch
        {
            "code" or "programming" => ResearchStrategy.CODE_ANALYSIS,
            "data" => ResearchStrategy.DATA_ANALYSIS,
            "compliance" or "legal" or "regulation" => ResearchStrategy.COMPLIANCE_CHECK,
            "research" or "academic" => ResearchStrategy.DETAILED_RESEARCH,
            _ => ResearchStrategy.QUICK_FACT
        };

        return (defaultStrategy, Configs[defaultStrategy]);
    }
}

// Phase 7a — Execution deep models
public enum DiffusionStage { Skeleton = 1, Tools = 2, Params = 3 }

public sealed class DiffusionStep
{
    public int Stage { get; set; }
    public string PlanText { get; set; } = "";
    public List<string> ToolsUsed { get; set; } = new();
    public double Confidence { get; set; }
    public string RefinementNotes { get; set; } = "";
}

public sealed class RefinedPlan
{
    public string Intent { get; set; } = "";
    public string Domain { get; set; } = "";
    public List<DiffusionStep> Steps { get; set; } = new();
    public string FinalPlan { get; set; } = "";
    public List<string> ToolsSequence { get; set; } = new();
    public int EstimatedTokens { get; set; }
    public double Confidence { get; set; }
}

public enum GTSMMode { Tree, Flow, Hybrid, Auto }

public sealed class GTSMStep
{
    public int Index { get; set; }
    public string Action { get; set; } = "";
    public string Tool { get; set; } = "";
    public Dictionary<string, object?> Params { get; set; } = new();
    public int TreeDepth { get; set; }
    public double NoiseStd { get; set; }
    public double ScoreGradient { get; set; }
    public double Confidence { get; set; }
}

public sealed class GTSMTrajectory
{
    public string Task { get; set; } = "";
    public GTSMMode Mode { get; set; }
    public List<GTSMStep> Steps { get; set; } = new();
    public double TotalScore { get; set; }
    public int TreeDepth { get; set; }
    public int DiffusionSteps { get; set; }
}

public sealed class TreeNode
{
    public string Action { get; set; } = "";
    public List<TreeNode> Children { get; set; } = new();
    public bool IsLeaf { get; set; }
    public double Score { get; set; }
    public int Depth { get; set; }
}

public sealed class CheckpointState
{
    public string SessionId { get; set; } = "";
    public string TaskGoal { get; set; } = "";
    public List<string> Plan { get; set; } = new();
    public List<string> CompletedSteps { get; set; } = new();
    public string? CurrentStep { get; set; }
    public Dictionary<string, object?> ExecutionResults { get; set; } = new();
    public List<string> Reflections { get; set; } = new();
    public double SuccessRate { get; set; }
    public DateTime SavedAt { get; set; }
    public int Version { get; set; }
}

public sealed class EvolutionCandidate
{
    public string Id { get; set; } = "";
    public string Content { get; set; } = "";
    public int Generation { get; set; }
    public double Fitness { get; set; }
    public List<string> Annotations { get; set; } = new();
    public List<string> ParentIds { get; set; } = new();
    public int MutationCount { get; set; }
}

public sealed class EvolutionResult
{
    public List<EvolutionCandidate> Candidates { get; set; } = new();
    public List<EvolutionCandidate> ElitePool { get; set; } = new();
    public double DiversityScore { get; set; }
}

public sealed class ThinkingProcessMetrics
{
    public int TokensUsed { get; set; }
    public int Mutations { get; set; }
    public int Crossovers { get; set; }
    public int GenerationsRun { get; set; }

    public double TokensPerCandidate => Candidates > 0 ? (double)TokensUsed / Candidates : 0;
    public double FitnessImprovementPerGen => GenerationsRun > 0 ? (double)FitnessGained / GenerationsRun : 0;
    public int Candidates { get; set; }
    public double FitnessGained { get; set; }
}

public sealed class TokenUsage
{
    public DateTime Timestamp { get; set; }
    public string Model { get; set; } = "";
    public int Tokens { get; set; }
    public double CostYuan { get; set; }
}

public sealed class BudgetStatus
{
    public double DailyLimit { get; set; }
    public double UsedToday { get; set; }
    public double Remaining => DailyLimit - UsedToday;
    public double UsagePct => DailyLimit > 0 ? UsedToday / DailyLimit : 0;
    public bool Degraded { get; set; }
    public double TotalCostYuan { get; set; }
    public DateTime? DegradedSince { get; set; }
}

public enum CognitiveBehavior { BackwardChain, SubgoalDecompose, Verify, Backtrack }

public sealed class VerificationResult
{
    public string StepId { get; set; } = "";
    public bool BackwardChainOk { get; set; }
    public bool SubgoalVerifiable { get; set; }
    public bool OutputObservable { get; set; }
    public bool NoPostHocLeakage { get; set; }
    public List<string> Reasons { get; set; } = new();
    public string CausalHypothesis { get; set; } = "";
    public bool Passed { get; set; }
    public double Score { get; set; }
    public List<string> FixSuggestions { get; set; } = new();
}

public sealed class BacktrackRecord
{
    public string Alternative { get; set; } = "";
    public string WhyRejected { get; set; } = "";
    public CognitiveBehavior Behavior { get; set; }
    public string BetterAlternative { get; set; } = "";
}

public sealed class CognitiveAudit
{
    public string PlanId { get; set; } = "";
    public int StepsVerified { get; set; }
    public List<BacktrackRecord> BacktrackRecords { get; set; } = new();
    public double PassRate { get; set; }
    public double CompressionRatio { get; set; }
    public string Recommendation { get; set; } = "";
}

public sealed class ApprovalRequest
{
    public string Id { get; set; } = "";
    public string TaskName { get; set; } = "";
    public string Question { get; set; } = "";
    public string Context { get; set; } = "";
    public string Status { get; set; } = "pending";
    public DateTime Created { get; set; }
    public DateTime? Resolved { get; set; }
    public string? Response { get; set; }
    public int TimeoutSeconds { get; set; } = 300;
    public TaskCompletionSource<bool> Future { get; set; } = new();
}

public sealed class SessionState
{
    public string SessionId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Workspace { get; set; } = "";
    public List<object> Messages { get; set; } = new();
    public int TotalTokens { get; set; }
    public string ReasoningEffort { get; set; } = "medium";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<string> Tags { get; set; } = new();
    public bool Archived { get; set; }
    public int SideGitTurns { get; set; }
}

public sealed class TurnSnapshot
{
    public string TurnId { get; set; } = "";
    public string Workspace { get; set; } = "";
    public string SnapshotPath { get; set; } = "";
    public DateTime Timestamp { get; set; }
}

public sealed class FitnessVector
{
    public double Reliability { get; set; }
    public double CostEfficiency { get; set; }
    public double Speed { get; set; }
    public double Safety { get; set; }

    public bool Dominates(FitnessVector other)
    {
        return Reliability >= other.Reliability && CostEfficiency >= other.CostEfficiency
            && Speed >= other.Speed && Safety >= other.Safety
            && (Reliability > other.Reliability || CostEfficiency > other.CostEfficiency
                || Speed > other.Speed || Safety > other.Safety);
    }
}

public sealed class TrajectoryScore
{
    public string TrajectoryId { get; set; } = "";
    public FitnessVector Fitness { get; set; } = new();
    public List<string> ToolSequence { get; set; } = new();
    public string Summary { get; set; } = "";
    public int TotalTokens { get; set; }
    public int TotalMs { get; set; }
    public bool IsParetoOptimal { get; set; }
}

public enum DiversityState { Healthy, Condensing, Collapsing, Frozen }

public sealed class RankSnapshot
{
    public DateTime Timestamp { get; set; }
    public int PopulationSize { get; set; }
    public int EffectiveRank { get; set; }
    public double DiversityScore { get; set; }
    public int DominantDirectionCount { get; set; }
    public double Entropy { get; set; }
    public DiversityState State { get; set; }
}

public sealed class AgentBelief
{
    public string Name { get; set; } = "";
    public double Alpha { get; set; } = 1;
    public double Beta { get; set; } = 1;
    public int MarginalTokens { get; set; }
    public DateTime? LastDelegated { get; set; }
    public int DelegationCount { get; set; }

    public double Mean => Alpha + Beta > 0 ? Alpha / (Alpha + Beta) : 0.5;
}

public sealed class CompressorStats
{
    public long TotalInputChars { get; set; }
    public long TotalOutputChars { get; set; }
    public int TotalCalls { get; set; }
    public int RulesApplied { get; set; }
    public int FallbackTruncations { get; set; }
    public int PassThroughs { get; set; }
}

public sealed class CompressResult
{
    public string Original { get; set; } = "";
    public string Compressed { get; set; } = "";
    public List<string> RulesApplied { get; set; } = new();
    public int OriginalChars { get; set; }
    public int CompressedChars { get; set; }
    public string Method { get; set; } = "";
}

public enum RuleAction { PassThrough, TruncateTail, ExtractPattern, Remove, Replace, Condense }

public sealed class CompressionRule
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Namespace { get; set; } = "";
    public int Priority { get; set; }
    public RuleAction Action { get; set; }
    public string MatchPattern { get; set; } = "";
    public string? MatchContext { get; set; }
    public int TruncateLines { get; set; }
    public int TruncateChars { get; set; }
    public string? ExtractRegex { get; set; }
    public string? ReplacePattern { get; set; }
    public string? ReplaceWith { get; set; }
    public int HitCount { get; set; }
    public int FalsePositiveCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastHit { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public string? OriginTask { get; set; }
    public bool AutoGenerated { get; set; }
}

public enum ClarifierMode { FillBlank, ConfirmAmbiguity, PreviewCheck }

public sealed class Clarification
{
    public string Id { get; set; } = "";
    public ClarifierMode Mode { get; set; }
    public string Question { get; set; } = "";
    public List<string> Options { get; set; } = new();
    public string? DefaultAnswer { get; set; }
    public string Context { get; set; } = "";
    public bool Answered { get; set; }
    public string? Answer { get; set; }
}

public sealed class ResolvedSkill
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string Description { get; set; } = "";
    public string Source { get; set; } = "";
    public string Handler { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public int UsedCount { get; set; }
}

public enum RLMSplitter { ByItem, ByAspect, ByChunk, Custom }

public sealed class RLMTask
{
    public string Id { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string Context { get; set; } = "";
    public Dictionary<string, object?> Metadata { get; set; } = new();
}

public sealed class RLMResult
{
    public string TaskId { get; set; } = "";
    public string Content { get; set; } = "";
    public bool Success { get; set; }
    public int TokensUsed { get; set; }
    public long DurationMs { get; set; }
}

public sealed class RLMAggregate
{
    public List<RLMResult> Results { get; set; } = new();
    public int TotalTokens { get; set; }
    public long TotalDurationMs { get; set; }
    public int WorkerCount { get; set; }
    public int SuccessCount { get; set; }
}
