using System.Text.Json;
using LTAI.Agent.Memory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Evolution;

public sealed record ContrastiveReport(
    string TaskId,
    string Task,
    int HighCount,
    int LowCount,
    double MeanHighScore,
    double MeanLowScore,
    IReadOnlyList<string> SuccessFactors,
    IReadOnlyList<string> FailureModes,
    IReadOnlyList<string> DivergencePoints,
    string? CandidatePatch,
    string? ImplicatedModule);

public sealed record EvidencePackage(
    IReadOnlyList<string> SystemicWeaknesses,
    IReadOnlyList<string> SystemicStrengths,
    IReadOnlyList<(string Module, string Patch, double Impact)> PrioritizedRepairs,
    int TasksAnalyzed,
    int TotalTrajectories);

public sealed class ContrastiveReflectionService
{
    private readonly IChatClient? _reflector;
    private readonly MetaSkillStore _skillStore;
    private readonly ILogger<ContrastiveReflectionService> _logger;

    private const int MaxTrajectoriesPerTask = 20;
    private const int MaxTasksForSynthesis = 10;
    private const int MaxJsonRetries = 2;

    public ContrastiveReflectionService(
        MetaSkillStore skillStore,
        IChatClient? reflector = null,
        ILogger<ContrastiveReflectionService>? logger = null)
    {
        _skillStore = skillStore;
        _reflector = reflector;
        _logger = logger ?? NullLogger<ContrastiveReflectionService>.Instance;
    }

    public async Task<ContrastiveReport[]> WithinTaskContrastAsync(
        string taskId,
        string task,
        IReadOnlyList<TrajectoryData> trajectories,
        CancellationToken ct = default)
    {
        if (trajectories.Count < 4)
        {
            _logger.LogDebug("ContrastiveReflection: too few trajectories ({N}) for task '{Task}', skip",
                trajectories.Count, taskId);
            return [];
        }

        if (_reflector == null)
        {
            _logger.LogDebug("ContrastiveReflection: no reflector LLM configured");
            return [];
        }

        var scored = trajectories.OrderByDescending(t => t.Score).ToList();
        var high = scored.Take(scored.Count / 2).ToList();
        var low = scored.Skip(scored.Count / 2).ToList();

        var highMean = high.Average(t => t.Score);
        var lowMean = low.Average(t => t.Score);

        if (Math.Abs(highMean - lowMean) < 0.05)
        {
            _logger.LogDebug("ContrastiveReflection: insufficient score separation for '{Task}' (Δ={Delta:F3})",
                taskId, highMean - lowMean);
            return [];
        }

        var highSummary = SummarizeTrajectories(high);
        var lowSummary = SummarizeTrajectories(low);

        var currentSkill = await _skillStore.GetLatestAsync(ct).ConfigureAwait(false);
        var skillContext = currentSkill.ToMarkdown();

        var prompt = $$"""
            你是一个编排诊断分析器。对比以下两组轨迹（来自同一个任务"{{task}}"）。

            ## 当前 Meta-Skill
            {{skillContext}}

            ## 高分轨迹组（平均分 {{highMean:F3}}）
            {{highSummary}}

            ## 低分轨迹组（平均分 {{lowMean:F3}}）
            {{lowSummary}}

            请执行结构化对比分析：
            1. **Divergence Points**：高分和低分轨迹在哪些具体决策点开始分岔？
            2. **Success Factors**：高分轨迹的成功因素是什么？
            3. **Failure Modes**：低分轨迹的反复失败模式是什么？
            4. **Root Causes**：失败的根本原因（归因到三个 Meta-Skill 模块之一：TaskDecomposition / AgentEngineering / WorkflowOrchestration）
            5. **Candidate Patch**：针对该模块的可行改进方案（一句话概括）

            输出纯 JSON：
            {
              "successFactors": ["因子1", "因子2"],
              "failureModes": ["模式1", "模式2"],
              "divergencePoints": ["分岔1", "分岔2"],
              "implicatedModule": "TaskDecomposition|AgentEngineering|WorkflowOrchestration",
              "candidatePatch": "改进方案描述"
            }
            """;

        // Retry loop for structured JSON parsing
        for (int attempt = 0; attempt <= MaxJsonRetries; attempt++)
        {
            try
            {
                var response = await _reflector.GetResponseAsync(
                    [new ChatMessage(ChatRole.User, attempt == 0 ? prompt : prompt + "\n\n之前输出格式有误，请确保输出严格符合 JSON 格式。")],
                    new ChatOptions { Temperature = 0.1f, MaxOutputTokens = 512 },
                    ct).ConfigureAwait(false);

                var text = response.Text ?? "";
                var parsed = ParseContrastiveResult(text);

                if (parsed != null)
                {
                    _logger.LogInformation("ContrastiveReflection: task '{Task}' — {Factors} factors, {Modes} modes, module={Module}",
                        taskId, parsed.SuccessFactors.Count, parsed.FailureModes.Count, parsed.ImplicatedModule);

                    return
                    [
                        new ContrastiveReport(
                            TaskId: taskId,
                            Task: task,
                            HighCount: high.Count,
                            LowCount: low.Count,
                            MeanHighScore: highMean,
                            MeanLowScore: lowMean,
                            SuccessFactors: parsed.SuccessFactors,
                            FailureModes: parsed.FailureModes,
                            DivergencePoints: parsed.DivergencePoints,
                            CandidatePatch: parsed.CandidatePatch,
                            ImplicatedModule: parsed.ImplicatedModule)
                    ];
                }

                _logger.LogWarning("ContrastiveReflection: attempt {A} JSON parse failed for '{Task}', retrying",
                    attempt + 1, taskId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ContrastiveReflection: LLM call attempt {A} failed for '{Task}'",
                    attempt + 1, taskId);
                if (attempt == MaxJsonRetries) break;
            }
        }

        return [];
    }

    public async Task<EvidencePackage> CrossTaskSynthesisAsync(
        IReadOnlyList<ContrastiveReport> reports,
        CancellationToken ct = default)
    {
        if (reports.Count == 0)
        {
            return new EvidencePackage([], [], [], 0, 0);
        }

        if (_reflector == null)
        {
            _logger.LogDebug("ContrastiveReflection: no reflector LLM, skipping cross-task synthesis");
            return new EvidencePackage([], [], [], reports.Count,
                reports.Sum(r => r.HighCount + r.LowCount));
        }

        var reportSummaries = string.Join("\n---\n",
            reports.Take(MaxTasksForSynthesis).Select(r =>
                $"Task: {r.TaskId} ({r.Task})\n" +
                $"  Scores: high={r.MeanHighScore:F2} low={r.MeanLowScore:F2}\n" +
                $"  Success Factors: {string.Join("; ", r.SuccessFactors)}\n" +
                $"  Failure Modes: {string.Join("; ", r.FailureModes)}\n" +
                $"  Module: {r.ImplicatedModule ?? "?"}\n" +
                $"  Patch: {r.CandidatePatch ?? "?"}"));

        var currentSkill = await _skillStore.GetLatestAsync(ct).ConfigureAwait(false);

        var tdPrinciples = string.Join("\n", currentSkill.TaskDecomposition.Principles.Select(p => $"  - {p}"));
        var aePrinciples = string.Join("\n", currentSkill.AgentEngineering.Principles.Select(p => $"  - {p}"));
        var woPrinciples = string.Join("\n", currentSkill.WorkflowOrchestration.Principles.Select(p => $"  - {p}"));

        var prompt = $$"""
            综合以下 {{reports.Count}} 个任务的对比诊断报告，为当前 Meta-Skill 生成优化依据。

            ## 当前 Meta-Skill v{{currentSkill.Version}}
            Task Decomposition principles:
            {{tdPrinciples}}

            Agent Engineering principles:
            {{aePrinciples}}

            Workflow Orchestration principles:
            {{woPrinciples}}

            ## 各任务对比报告
            {{reportSummaries}}

            请分析：
            1. **Systemic Weaknesses** — 跨多个任务反复出现的系统性缺陷（归因到模块）
            2. **Systemic Strengths** — 应保留的成功策略
            3. **Prioritized Repairs** — 按影响排序的修复建议（模块 + 具体改动 + 预期影响值 0-1）

            输出纯 JSON 数组（最多 5 条修复）：
            [
              {"module": "TaskDecomposition|AgentEngineering|WorkflowOrchestration",
                "patch": "具体改进描述",
                "impact": 0.85,
                "evidence": "跨任务证据摘要"}
            ]
            """;

        try
        {
            var response = await _reflector.GetResponseAsync(
                [new ChatMessage(ChatRole.User, prompt)],
                new ChatOptions { Temperature = 0.1f, MaxOutputTokens = 768 },
                ct).ConfigureAwait(false);

            var text = response.Text ?? "";
            var repairs = ParseRepairs(text);

            var weaknesses = reports
                .SelectMany(r => r.FailureModes)
                .Distinct()
                .Take(8)
                .ToList();

            var strengths = reports
                .SelectMany(r => r.SuccessFactors)
                .Distinct()
                .Take(8)
                .ToList();

            _logger.LogInformation("CrossTaskSynthesis: {N} reports → {R} repairs, {W} weaknesses, {S} strengths",
                reports.Count, repairs.Count, weaknesses.Count, strengths.Count);

            return new EvidencePackage(
                SystemicWeaknesses: weaknesses,
                SystemicStrengths: strengths,
                PrioritizedRepairs: repairs,
                TasksAnalyzed: reports.Count,
                TotalTrajectories: reports.Sum(r => r.HighCount + r.LowCount));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CrossTaskSynthesis: LLM call failed");
            return new EvidencePackage([], [], [], reports.Count,
                reports.Sum(r => r.HighCount + r.LowCount));
        }
    }

    private static string SummarizeTrajectories(IReadOnlyList<TrajectoryData> trajectories)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < Math.Min(trajectories.Count, 10); i++)
        {
            var t = trajectories[i];
            var tools = string.Join(", ", t.ToolCalls.Select(tc => tc.Name).Distinct());
            int successCount = t.ToolCalls.Count(tc => tc.Success);
            int totalCount = t.ToolCalls.Count;
            var hasPlan = t.Plan != null;
            var fast = t.SkillWeaverFastPath;

            sb.AppendLine($"### Trajectory #{t.TrajectoryIndex} (score={t.Score:F3})");
            sb.AppendLine($"- Tools used: [{tools}] ({successCount}/{totalCount} success)");
            sb.AppendLine($"- Has plan: {hasPlan}, FastPath: {fast}");
            sb.AppendLine($"- Response length: {t.ResponseText?.Length ?? 0} chars");
            if (t.Decomposition is { Count: > 0 })
                sb.AppendLine($"- Decomposition: {string.Join(" | ", t.Decomposition)}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static ContrastiveResult? ParseContrastiveResult(string text)
    {
        try
        {
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start < 0 || end <= start) return null;

            var json = text[start..(end + 1)];
            var result = JsonSerializer.Deserialize<ContrastiveResult>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return result?.IsValid == true ? result : null;
        }
        catch { return null; }
    }

    private static List<(string Module, string Patch, double Impact)> ParseRepairs(string text)
    {
        try
        {
            var start = text.IndexOf('[');
            var end = text.LastIndexOf(']');
            if (start < 0 || end <= start) return [];

            var json = text[start..(end + 1)];
            var items = JsonSerializer.Deserialize<List<RepairItem>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (items == null) return [];

            return items
                .Where(i => i.IsValid)
                .Take(5)
                .Select(i => (i.Module!, i.Patch!, Math.Clamp(i.Impact, 0, 1)))
                .ToList();
        }
        catch { return []; }
    }

    private sealed record ContrastiveResult(
        List<string>? SuccessFactorsList,
        List<string>? FailureModesList,
        List<string>? DivergencePointsList,
        string? ImplicatedModule,
        string? CandidatePatch)
    {
        public IReadOnlyList<string> SuccessFactors => SuccessFactorsList ?? [];
        public IReadOnlyList<string> FailureModes => FailureModesList ?? [];
        public IReadOnlyList<string> DivergencePoints => DivergencePointsList ?? [];

        public bool IsValid =>
            SuccessFactors.Count > 0 && FailureModes.Count > 0 &&
            !string.IsNullOrWhiteSpace(ImplicatedModule) &&
            !string.IsNullOrWhiteSpace(CandidatePatch) &&
            ImplicatedModule is "TaskDecomposition" or "AgentEngineering" or "WorkflowOrchestration";
    }

    private sealed record RepairItem(
        string? Module,
        string? Patch,
        double Impact,
        string? Evidence)
    {
        public bool IsValid =>
            !string.IsNullOrWhiteSpace(Module) &&
            !string.IsNullOrWhiteSpace(Patch) &&
            Module is "TaskDecomposition" or "AgentEngineering" or "WorkflowOrchestration";
    }
}
