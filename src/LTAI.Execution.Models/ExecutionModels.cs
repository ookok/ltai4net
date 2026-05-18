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
