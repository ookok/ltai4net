using LTAI.Agent.Memory;
using LTAI.Agent.Pipeline;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Evolution;

public sealed class SkillEvolutionOrchestrator : BackgroundService
{
    private readonly MetaSkillStore _skillStore;
    private readonly ContrastiveReflectionService _contrastiveReflection;
    private readonly PlanLearningStore _planStore;
    private readonly PalaceStore _palaceStore;
    private readonly IChatClient? _optimizer;
    private readonly RegressionTestSuite _regressionSuite;
    private readonly ILogger<SkillEvolutionOrchestrator> _logger;

    private static readonly TimeSpan EvolutionInterval = TimeSpan.FromHours(4);
    private const int MaxRounds = 10;
    private const int MinTrajectoriesPerTask = 4;
    private const double MinMeanScoreForExclusion = 0.15;

    private List<RegressionCase>? _regressionSuiteCache;

    public SkillEvolutionOrchestrator(
        MetaSkillStore skillStore,
        ContrastiveReflectionService contrastiveReflection,
        PlanLearningStore planStore,
        PalaceStore palaceStore,
        RegressionTestSuite regressionSuite,
        IChatClient? optimizer = null,
        ILogger<SkillEvolutionOrchestrator>? logger = null)
    {
        _skillStore = skillStore;
        _contrastiveReflection = contrastiveReflection;
        _planStore = planStore;
        _palaceStore = palaceStore;
        _regressionSuite = regressionSuite;
        _optimizer = optimizer;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SkillEvolutionOrchestrator>.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunEvolutionRoundAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SkillEvolutionOrchestrator: round failed");
            }

            await Task.Delay(EvolutionInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    public async Task RunEvolutionRoundAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("SkillEvolutionOrchestrator: starting evolution round");

        // ── Step 1: Collect trajectories from available sources ──
        var trajectories = await CollectTrajectoriesAsync(ct).ConfigureAwait(false);
        if (trajectories.Count < MinTrajectoriesPerTask)
        {
            _logger.LogInformation("SkillEvolutionOrchestrator: insufficient trajectories ({N}), skip round",
                trajectories.Count);
            return;
        }

        // ── Step 2: Group by task (normalized) ──
        var grouped = GroupByTask(trajectories);
        _logger.LogInformation("SkillEvolutionOrchestrator: {Groups} task groups from {Total} trajectories",
            grouped.Count, trajectories.Count);

        // ── Step 3: Priority-driven task selection (elbow truncation) ──
        var selected = SelectPriorityTasks(grouped);
        if (selected.Count == 0)
        {
            _logger.LogInformation("SkillEvolutionOrchestrator: no high-priority tasks selected");
            return;
        }

        _logger.LogInformation("SkillEvolutionOrchestrator: selected {N} tasks for contrastive analysis",
            selected.Count);

        // ── Step 4: Within-task contrastive analysis ──
        var allReports = new List<ContrastiveReport>();
        foreach (var (taskId, task, taskTrajs) in selected)
        {
            var reports = await _contrastiveReflection.WithinTaskContrastAsync(
                taskId, task, taskTrajs, ct).ConfigureAwait(false);
            allReports.AddRange(reports);
        }

        if (allReports.Count == 0)
        {
            _logger.LogInformation("SkillEvolutionOrchestrator: no contrastive reports generated");
            return;
        }

        // ── Step 5: Cross-task synthesis → EvidencePackage ──
        var evidence = await _contrastiveReflection.CrossTaskSynthesisAsync(allReports, ct)
            .ConfigureAwait(false);

        if (evidence.PrioritizedRepairs.Count == 0)
        {
            _logger.LogInformation("SkillEvolutionOrchestrator: no prioritized repairs, skip rewrite");
            return;
        }

        // ── Step 6: Rewrite Meta-Skill based on evidence ──
        var updated = await RewriteMetaSkillAsync(evidence, ct).ConfigureAwait(false);

        if (updated != null)
        {
            // ── Step 6b: Regression gate — roll back if quality drops ──
            var regressionPassed = await RunRegressionGateAsync(updated, ct).ConfigureAwait(false);
            if (!regressionPassed)
            {
                var prevVersion = updated.Version - 1;
                var prev = await _skillStore.LoadVersionAsync(prevVersion, ct).ConfigureAwait(false);
                if (prev != null)
                {
                    await _skillStore.SaveVersionAsync(prev, ct).ConfigureAwait(false);
                    _logger.LogWarning(
                        "SkillEvolutionOrchestrator: regression FAILED, rolled back to v{V}", prevVersion);
                }
                else
                {
                    _logger.LogWarning(
                        "SkillEvolutionOrchestrator: regression FAILED, could not rollback (v{V} not found)",
                        prevVersion);
                }
                return;
            }

            _logger.LogInformation(
                "SkillEvolutionOrchestrator: round complete → Meta-Skill v{V} (R{R}), {Patches} patches",
                updated.Version, updated.Round, evidence.PrioritizedRepairs.Count);
        }
        else
        {
            _logger.LogInformation("SkillEvolutionOrchestrator: round complete, no skill changes");
        }
    }

    /// <summary>Run a full multi-round evolution (triggered mode, up to MaxRounds).</summary>
    public async Task<Evolution.MetaSkill> RunFullEvolutionAsync(
        int rounds = 3,
        CancellationToken ct = default)
    {
        rounds = Math.Clamp(rounds, 1, MaxRounds);

        for (int r = 0; r < rounds; r++)
        {
            _logger.LogInformation("SkillEvolutionOrchestrator: full evolution round {R}/{N}",
                r + 1, rounds);

            await RunEvolutionRoundAsync(ct).ConfigureAwait(false);

            // Between rounds, wait briefly for any background processing
            if (r < rounds - 1)
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }

        return await _skillStore.GetLatestAsync(ct).ConfigureAwait(false);
    }

    // ── Trajectory Collection ──

    private async Task<List<TrajectoryData>> CollectTrajectoriesAsync(CancellationToken ct)
    {
        var result = new List<TrajectoryData>();

        // Source 1: PlanLearningStore — successful/failed plan executions
        try
        {
            var plans = await _planStore.GetAllPlansAsync(ct).ConfigureAwait(false);
            foreach (var plan in plans)
            {
                if (plan.SuccessCount > 0)
                {
                    var toolCalls = plan.Plan.SubTasks
                        .Where(p => p.AssignedTool != null)
                        .Select(p => new ToolCallRecord(
                            p.AssignedTool!, "", "", true, 0))
                        .ToList();

                    result.Add(new TrajectoryData(
                        TaskId: plan.Normalized,
                        Task: plan.Query,
                        TrajectoryIndex: 0,
                        MetaSkillVersion: await _skillStore.GetLatestAsync(ct)
                            .ContinueWith(t => t.Result.Version, ct).ConfigureAwait(false),
                        Score: 0.8 + (plan.SuccessCount * 0.05),
                        SkillWeaverFastPath: plan.Plan.SubTasks.Count <= 2,
                        Decomposition: [.. plan.Plan.SubTasks.Select(p => p.Description)],
                        Plan: plan.Plan,
                        ToolCalls: toolCalls,
                        ResponseText: null,
                        CreatedAt: plan.LastUsed));
                }

                if (plan.FailureCount > 0)
                {
                    result.Add(new TrajectoryData(
                        TaskId: plan.Normalized,
                        Task: plan.Query,
                        TrajectoryIndex: 1,
                        MetaSkillVersion: await _skillStore.GetLatestAsync(ct)
                            .ContinueWith(t => t.Result.Version, ct).ConfigureAwait(false),
                        Score: 0.3 - (plan.FailureCount * 0.05),
                        SkillWeaverFastPath: plan.Plan.SubTasks.Count <= 2,
                        Decomposition: [.. plan.Plan.SubTasks.Select(p => p.Description)],
                        Plan: plan.Plan,
                        ToolCalls: [],
                        ResponseText: null,
                        CreatedAt: plan.LastUsed));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SkillEvolutionOrchestrator: failed to collect plans");
        }

        // Source 2: PalaceStore — experience entries with importance scores
        try
        {
            var experiences = _palaceStore.SearchByRoom("experience", maxCount: 200);
            foreach (var exp in experiences.Take(100))
            {
                var score = Math.Clamp(exp.Importance, 0.0, 1.0);
                result.Add(new TrajectoryData(
                    TaskId: NormalizeForGrouping(exp.Content),
                    Task: exp.Content.Length > 200 ? exp.Content[..197] + "..." : exp.Content,
                    TrajectoryIndex: 0,
                    MetaSkillVersion: await _skillStore.GetLatestAsync(ct)
                        .ContinueWith(t => t.Result.Version, ct).ConfigureAwait(false),
                    Score: score,
                    SkillWeaverFastPath: false,
                    Decomposition: null,
                    Plan: null,
                    ToolCalls: [],
                    ResponseText: exp.Content,
                    CreatedAt: DateTimeOffset.FromUnixTimeMilliseconds(exp.CreatedAt).UtcDateTime));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SkillEvolutionOrchestrator: failed to collect experiences");
        }

        return result;
    }

    // ── Task Grouping ──

    private static List<(string TaskId, string Task, List<TrajectoryData>)> GroupByTask(
        List<TrajectoryData> trajectories)
    {
        var groups = new Dictionary<string, (string Task, List<TrajectoryData> List)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var t in trajectories)
        {
            if (!groups.ContainsKey(t.TaskId))
                groups[t.TaskId] = (t.Task, []);
            groups[t.TaskId].List.Add(t);
        }

        return groups
            .Where(g => g.Value.List.Count >= MinTrajectoriesPerTask)
            .Select(g => (g.Key, g.Value.Task, g.Value.List))
            .ToList();
    }

    // ── Priority-Driven Selection (elbow truncation) ──

    private List<(string TaskId, string Task, List<TrajectoryData> Selected)>
        SelectPriorityTasks(List<(string TaskId, string Task, List<TrajectoryData> Group)> groups)
    {
        if (groups.Count == 0) return [];

        // Compute priority score for each group
        var priorities = new List<(int Index, double Priority, double MeanScore)>();
        for (int i = 0; i < groups.Count; i++)
        {
            var (_, _, trajs) = groups[i];
            var meanScore = trajs.Average(t => t.Score);
            var stdDev = Math.Sqrt(trajs.Average(t => Math.Pow(t.Score - meanScore, 2)));

            // Normalize: difficulty = 1 - meanScore, uncertainty = stdDev
            var difficulty = Math.Clamp(1.0 - meanScore, 0.0, 1.0);
            var uncertainty = Math.Clamp(stdDev * 2.0, 0.0, 1.0);

            var priority = 0.5 * difficulty + 0.5 * uncertainty;
            priorities.Add((i, priority, meanScore));
        }

        // Sort by priority descending
        priorities = [.. priorities.OrderByDescending(p => p.Priority)];

        // Elbow truncation (discrete second derivative)
        var elbowIdx = FindElbow([.. priorities.Select(p => p.Priority)]);

        return priorities
            .Take(elbowIdx)
            .Where(p => p.MeanScore >= MinMeanScoreForExclusion)
            .Select(p => groups[p.Index])
            .ToList();
    }

    private static int FindElbow(List<double> values)
    {
        if (values.Count <= 3) return values.Count;

        // Compute first-order differences
        var diffs = new double[values.Count - 1];
        for (int i = 0; i < diffs.Length; i++)
            diffs[i] = values[i] - values[i + 1];

        // Find max second-order difference (= elbow)
        var maxIdx = 0;
        var maxVal = double.MinValue;
        for (int i = 0; i < diffs.Length - 1; i++)
        {
            var secondDiff = Math.Abs(diffs[i] - diffs[i + 1]);
            if (secondDiff > maxVal)
            {
                maxVal = secondDiff;
                maxIdx = i;
            }
        }

        // Elbow is at maxIdx + 1 (at least 1, at most values.Count)
        return Math.Clamp(maxIdx + 1, 1, values.Count);
    }

    // ── Meta-Skill Rewriting ──

    private async Task<Evolution.MetaSkill?> RewriteMetaSkillAsync(
        EvidencePackage evidence,
        CancellationToken ct)
    {
        if (_optimizer == null)
        {
            _logger.LogDebug("SkillEvolutionOrchestrator: no optimizer LLM configured");
            return await ApplyFallbackPatchesAsync(evidence, ct).ConfigureAwait(false);
        }

        var current = await _skillStore.GetLatestAsync(ct).ConfigureAwait(false);

        var evidenceText = FormatEvidence(evidence);
        var tdStr = current.TaskDecomposition.ToFormattedString(2);
        var aeStr = current.AgentEngineering.ToFormattedString(2);
        var woStr = current.WorkflowOrchestration.ToFormattedString(2);

        var prompt = $$"""
            你是一个 Meta-Skill 优化器。当前 Meta-Skill v{{current.Version}} 定义了多智能体系统的编排原则。
            基于进化证据，优化这三个模块的编排原则。

            ## 当前 Meta-Skill
            ### Task Decomposition
            {{tdStr}}

            ### Agent Engineering
            {{aeStr}}

            ### Workflow Orchestration
            {{woStr}}

            ## 进化证据（跨任务综合）
            {{evidenceText}}

            ## 优化指令
            1. 审查当前原则：哪些被证据表明无效或反效果？移除或重写它们
            2. 引入新原则：针对已识别的系统性缺陷，添加通用化的编排原则
            3. 严格维护三模块支架（TaskDecomposition / AgentEngineering / WorkflowOrchestration）
            4. 每条原则必须是通用策略，而不是某个具体任务的修复
            5. 每条原则应是可操作的（agent 可执行的明确指令）

            输出纯 JSON：
            {
              "taskDecomposition": ["原则1", "原则2", ...],
              "agentEngineering": ["原则1", "原则2", ...],
              "workflowOrchestration": ["原则1", "原则2", ...],
              "rationale": "本轮优化的核心思路"
            }
            """;

        try
        {
            var response = await _optimizer.GetResponseAsync(
                [new ChatMessage(ChatRole.User, prompt)],
                new ChatOptions { Temperature = 0.3f, MaxOutputTokens = 1024 },
                ct).ConfigureAwait(false);

            var text = response.Text ?? "";
            var parsed = ParseOptimizerResult(text);

            if (parsed != null)
            {
                var patches = evidence.PrioritizedRepairs
                    .Select(r => new MetaSkillPatch(r.Module, r.Patch, r.Impact))
                    .ToArray();

                var next = new Evolution.MetaSkill(
                    Version: current.Version + 1,
                    Round: current.Round + 1,
                    EvolvedFrom: $"v{current.Version}",
                    TaskDecomposition: new MetaSkillModule(parsed.TaskDecomposition),
                    AgentEngineering: new MetaSkillModule(parsed.AgentEngineering),
                    WorkflowOrchestration: new MetaSkillModule(parsed.WorkflowOrchestration),
                    CreatedAt: DateTime.UtcNow,
                    PatchesApplied: patches);

                await _skillStore.SaveVersionAsync(next, ct).ConfigureAwait(false);

                // Write markdown export for debugging
                await ExportSkillToRefsAsync(next, evidence).ConfigureAwait(false);

                _logger.LogInformation(
                    "SkillEvolutionOrchestrator: Meta-Skill evolved to v{V} — {ModuleCount}. Rationale: {Rationale}",
                    next.Version, next.ModuleCountLabel, parsed.Rationale ?? "(none)");

                return next;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SkillEvolutionOrchestrator: LLM skill optimization failed");
        }

        return await ApplyFallbackPatchesAsync(evidence, ct).ConfigureAwait(false);
    }

    private async Task<Evolution.MetaSkill?> ApplyFallbackPatchesAsync(
        EvidencePackage evidence,
        CancellationToken ct)
    {
        var current = await _skillStore.GetLatestAsync(ct).ConfigureAwait(false);

        var patches = evidence.PrioritizedRepairs
            .Select(r => new MetaSkillPatch(r.Module, r.Patch, r.Impact))
            .ToArray();

        if (patches.Length == 0) return null;

        // Fallback: only apply module-specific patches as new principles
        var tdPrinciples = current.TaskDecomposition.Principles.ToList();
        var aePrinciples = current.AgentEngineering.Principles.ToList();
        var woPrinciples = current.WorkflowOrchestration.Principles.ToList();

        foreach (var patch in patches)
        {
            var target = patch.Module switch
            {
                "TaskDecomposition" => tdPrinciples,
                "AgentEngineering" => aePrinciples,
                "WorkflowOrchestration" => woPrinciples,
                _ => null
            };

            if (target != null && !target.Contains(patch.Description))
                target.Add(patch.Description);
        }

        var next = new Evolution.MetaSkill(
            Version: current.Version + 1,
            Round: current.Round + 1,
            EvolvedFrom: $"v{current.Version}",
            TaskDecomposition: new MetaSkillModule(tdPrinciples),
            AgentEngineering: new MetaSkillModule(aePrinciples),
            WorkflowOrchestration: new MetaSkillModule(woPrinciples),
            CreatedAt: DateTime.UtcNow,
            PatchesApplied: patches);

        await _skillStore.SaveVersionAsync(next, ct).ConfigureAwait(false);
        return next;
    }

    // ── Formatting & Parsing ──

    private static string FormatEvidence(EvidencePackage evidence)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Tasks analyzed: {evidence.TasksAnalyzed}");
        sb.AppendLine($"Total trajectories: {evidence.TotalTrajectories}");
        sb.AppendLine();

        if (evidence.SystemicWeaknesses.Count > 0)
        {
            sb.AppendLine("Systemic Weaknesses:");
            foreach (var w in evidence.SystemicWeaknesses)
                sb.AppendLine($"- {w}");
            sb.AppendLine();
        }

        if (evidence.SystemicStrengths.Count > 0)
        {
            sb.AppendLine("Systemic Strengths (preserve):");
            foreach (var s in evidence.SystemicStrengths)
                sb.AppendLine($"- {s}");
            sb.AppendLine();
        }

        if (evidence.PrioritizedRepairs.Count > 0)
        {
            sb.AppendLine("Prioritized Repairs:");
            foreach (var (module, patch, impact) in evidence.PrioritizedRepairs)
                sb.AppendLine($"- [{module}] (impact={impact:P1}): {patch}");
        }

        return sb.ToString();
    }

    private static OptimizerResult? ParseOptimizerResult(string text)
    {
        try
        {
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start < 0 || end <= start) return null;

            var json = text[start..(end + 1)];
            return System.Text.Json.JsonSerializer.Deserialize<OptimizerResult>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { return null; }
    }

    private sealed record OptimizerResult(
        List<string>? TaskDecomposition,
        List<string>? AgentEngineering,
        List<string>? WorkflowOrchestration,
        string? Rationale);

    // ── Regression Gate ──

    private async Task<bool> RunRegressionGateAsync(
        Evolution.MetaSkill candidate,
        CancellationToken ct)
    {
        _regressionSuiteCache ??= await _regressionSuite.BuildSuiteAsync(ct)
            .ConfigureAwait(false);

        if (_regressionSuiteCache.Count == 0)
        {
            _logger.LogInformation("RegressionGate: no regression cases, skipping gate");
            return true;
        }

        var result = await _regressionSuite.EvaluateAsync(_regressionSuiteCache, ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "RegressionGate: {Result} — avg={Avg:F3} baseline={Base:F3} Δ={Delta:F3} failed={Failed}/{Total}",
            result.Passed ? "PASS" : "FAIL",
            result.AverageScore, result.BaselineAverageScore,
            result.Delta, result.FailedCases, result.TotalCases);

        if (!result.Passed && result.Failures.Count > 0)
        {
            foreach (var f in result.Failures.Take(5))
                _logger.LogWarning("RegressionGate: failure detail: {Detail}", f);
        }

        return result.Passed;
    }

    private static string NormalizeForGrouping(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var cleaned = string.Join(" ", text
            .ToLowerInvariant()
            .Split([' ', '\t', '\n', '\r', ',', '.', '!', '?', '：', '，', '。', '！', '？'],
                StringSplitOptions.RemoveEmptyEntries));
        return cleaned.Length > 100 ? cleaned[..100] : cleaned;
    }

    private static async Task ExportSkillToRefsAsync(
        Evolution.MetaSkill skill,
        EvidencePackage evidence)
    {
        try
        {
            var refsDir = Path.Combine(Directory.GetCurrentDirectory(), ".livingtree", "refs");
            Directory.CreateDirectory(refsDir);
            var path = Path.Combine(refsDir, $"meta-skill-v{skill.Version}.md");

            var md = skill.ToMarkdown();
            md += $"\n## Evolution Evidence\n\n{FormatEvidence(evidence)}\n";

            await File.WriteAllTextAsync(path, md).ConfigureAwait(false);
        }
        catch
        {
            // non-critical
        }
    }
}
