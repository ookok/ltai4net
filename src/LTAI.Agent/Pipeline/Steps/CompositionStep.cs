using System.Text;
using System.Text.Json;
using LTAI.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Pipeline.Steps;

public sealed record SubTaskPlan(
    int Index,
    string Description,
    string? AssignedTool,
    float ToolScore,
    string Domain,
    IReadOnlyList<int> DependsOn);

public sealed record CompositionPlan(
    IReadOnlyList<SubTaskPlan> SubTasks,
    IReadOnlyList<IReadOnlyList<int>> ExecutionGroups);

public sealed class CompositionStep : IPipelineStep
{
    private readonly IToolRegistry? _toolRegistry;
    private readonly EmbeddingClient? _embedder;
    private readonly IChatClient? _llm;
    private readonly PlanLearningStore? _planStore;
    private readonly ILogger<CompositionStep> _logger;

    public string Name => "Composition";

    public CompositionStep(
        IToolRegistry? toolRegistry = null,
        EmbeddingClient? embedder = null,
        IChatClient? llm = null,
        PlanLearningStore? planStore = null,
        ILogger<CompositionStep>? logger = null)
    {
        _toolRegistry = toolRegistry;
        _embedder = embedder;
        _llm = llm;
        _planStore = planStore;
        _logger = logger ?? NullLogger<CompositionStep>.Instance;
    }

    public async Task<MessageContext> ProcessAsync(MessageContext context)
    {
        // ── Fast path: skip if plan already exists (e.g. from DynamicReplanStep or cross-session lookup) ──
        if (context.TryGet<CompositionPlan>("CompositionPlan", out _))
        {
            _logger.LogDebug("CompositionStep: plan already exists, skipping");
            return context;
        }

        // ── Fast path: skip composition for simple queries ──
        if (context.TryGet<bool>("SkillWeaverFastPath", out var fast) && fast)
        {
            _logger.LogDebug("CompositionStep: fast path, skipping");
            return context;
        }

        var tasks = (context.TryGet<List<string>>("_FinalDecomposition", out var refined) ? refined : null)
            ?? (context.TryGet<List<string>>("Decomposition", out var raw) ? raw : null);

        if (tasks is not { Count: > 0 })
        {
            _logger.LogDebug("CompositionStep: no decomposition available, skipping");
            return context;
        }

        // ── Stage 1: Assign best tool to each sub-task ──
        var plans = new List<SubTaskPlan>(tasks.Count);
        bool canRetrieve = _toolRegistry is { IsInitialized: true } && _embedder != null;

        for (int i = 0; i < tasks.Count; i++)
        {
            string? toolName = null;
            float score = 0f;
            string domain = "";

            if (canRetrieve)
            {
                try
                {
                    var hits = await _toolRegistry!.SearchTopKAsync(tasks[i], _embedder!, null,
                        k: 1, context.CancellationToken).ConfigureAwait(false);
                    if (hits.Count > 0)
                    {
                        toolName = hits[0].Name;
                        domain = hits[0].Domain;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "CompositionStep: tool retrieval failed for task {I}: '{Task}'", i, tasks[i]);
                }
            }

            plans.Add(new SubTaskPlan(i, tasks[i], toolName, score, domain, []));
        }

        // ── Stage 2: Detect dependencies (use LLM for pairwise analysis) ──
        IReadOnlyList<int>[] dependencies;
        if (_llm != null && plans.Count > 1)
        {
            dependencies = await DetectDependenciesAsync(tasks, context.CancellationToken).ConfigureAwait(false);
        }
        else
        {
            dependencies = BuildSequentialDependencies(plans.Count);
        }

        var planWithDeps = plans.Select((p, i) => p with { DependsOn = dependencies[i] }).ToList();

        // ── Stage 3: Build execution groups (DAG → parallel groups) ──
        var groups = BuildExecutionGroups(planWithDeps);

        var plan = new CompositionPlan(planWithDeps, groups);
        context.Set("CompositionPlan", plan);

        // ── Store pre-selected tool names for ToolFilteringChatClient ──
        var preSelectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in planWithDeps)
            if (p.AssignedTool != null)
                preSelectedNames.Add(p.AssignedTool);
        context.Set("_PreSelectedToolNames", preSelectedNames);

        // ── Stage 4: Critical path detection ──
        var criticalPath = FindCriticalPath(planWithDeps);
        context.Set("_CriticalPath", criticalPath);

        // ── Stage 5: Inject compact BabelTele-style plan with critical path annotations ──
        var planText = BuildPlanTextStatic(plan, criticalPath);
        lock (context.MessagesLock)
            context.Messages.Add(new ChatMessage(ChatRole.System, planText));

        // ── Debug export to .livingtree/refs/ ──
        await ExportPlanToRefsAsync(plan, context.TraceId ?? "unknown").ConfigureAwait(false);

        // ── Cross-session plan learning: store successful plan ──
        if (_planStore != null)
        {
            await _planStore.StoreAsync(context.Request, plan, success: true, context.CancellationToken)
                .ConfigureAwait(false);
        }

        _logger.LogInformation("CompositionStep: built plan with {Count} sub-tasks in {GroupCount} groups, critical path length={Cpl}",
            planWithDeps.Count, groups.Count, criticalPath.Count);

        return context;
    }

    private async Task<IReadOnlyList<int>[]> DetectDependenciesAsync(
        List<string> tasks, CancellationToken ct)
    {
        var taskList = string.Join("\n", tasks.Select((t, i) => $"  [{i}] {t}"));
        var prompt = "分析以下子任务之间的执行依赖关系。" +
            "如果子任务[i]必须在子任务[j]之前完成（即[j]依赖[i]的结果），则记录依赖。\n" +
            "子任务列表：\n" + taskList + "\n" +
            "输出JSON对象，格式为：{\"deps\": [[依赖者索引, 被依赖者索引], ...]}\n" +
            "例如 3个任务时：{\"deps\": [[1,0], [2,1]]} 表示任务1依赖任务0，任务2依赖任务1\n" +
            "如果没有依赖关系，返回：{\"deps\": []}";

        try
        {
            var response = await _llm!.GetResponseAsync(
                [new ChatMessage(ChatRole.User, prompt)],
                new ChatOptions { Temperature = 0.1f, MaxOutputTokens = 256 },
                ct).ConfigureAwait(false);

            var text = response.Text ?? "";
            return ParseDependencies(text, tasks.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CompositionStep: dependency detection failed");
            return BuildSequentialDependencies(tasks.Count);
        }
    }

    private static IReadOnlyList<int>[] ParseDependencies(string text, int taskCount)
    {
        var result = new IReadOnlyList<int>[taskCount];
        for (int i = 0; i < taskCount; i++)
            result[i] = Array.Empty<int>();

        try
        {
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start < 0 || end <= start) return result;

            var json = text[start..(end + 1)];
            using var doc = JsonDocument.Parse(json);
            var deps = doc.RootElement.GetProperty("deps").EnumerateArray()
                .Select(e => (Dependent: e[0].GetInt32(), Dependency: e[1].GetInt32()))
                .Where(d => d.Dependent >= 0 && d.Dependent < taskCount
                         && d.Dependency >= 0 && d.Dependency < taskCount
                         && d.Dependent != d.Dependency)
                .GroupBy(d => d.Dependent)
                .ToDictionary(g => g.Key, g => g.Select(d => d.Dependency).Distinct().ToList());

            for (int i = 0; i < taskCount; i++)
                if (deps.TryGetValue(i, out var list))
                    result[i] = list.AsReadOnly();
        }
        catch { }

        return result;
    }

    private static IReadOnlyList<int>[] BuildSequentialDependencies(int count)
    {
        var result = new IReadOnlyList<int>[count];
        for (int i = 0; i < count; i++)
            result[i] = i > 0 ? new[] { i - 1 } : Array.Empty<int>();
        return result;
    }

    internal static IReadOnlyList<IReadOnlyList<int>> BuildExecutionGroups(List<SubTaskPlan> plans)
    {
        int n = plans.Count;
        var inDegree = new int[n];
        var outEdges = new List<int>[n];
        for (int i = 0; i < n; i++)
        {
            outEdges[i] = [];
            inDegree[i] = 0;
        }

        for (int i = 0; i < n; i++)
        {
            foreach (var dep in plans[i].DependsOn)
            {
                if (dep >= 0 && dep < n && dep != i)
                {
                    outEdges[dep].Add(i);
                    inDegree[i]++;
                }
            }
        }

        // Kahn's algorithm
        var groups = new List<List<int>>();
        var queue = new Queue<int>();
        for (int i = 0; i < n; i++)
            if (inDegree[i] == 0)
                queue.Enqueue(i);

        var tempInDegree = (int[])inDegree.Clone();
        while (queue.Count > 0)
        {
            var currentGroup = new List<int>();
            var count = queue.Count;
            for (int i = 0; i < count; i++)
            {
                var node = queue.Dequeue();
                currentGroup.Add(node);
                foreach (var next in outEdges[node])
                {
                    tempInDegree[next]--;
                    if (tempInDegree[next] == 0)
                        queue.Enqueue(next);
                }
            }
            groups.Add(currentGroup);
        }

        return groups.Select(g => (IReadOnlyList<int>)g.AsReadOnly()).ToList().AsReadOnly();
    }

    private static string BuildPlanText(CompositionPlan plan)
        => BuildPlanTextStatic(plan, FindCriticalPath([.. plan.SubTasks]));

    public static string BuildPlanTextStatic(CompositionPlan plan, IReadOnlySet<int>? criticalPath = null)
    {
        criticalPath ??= FindCriticalPath([.. plan.SubTasks]);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## [P:plan] — 紧凑执行计划");
        sb.AppendLine("格式: [G{g}:{idx}:{desc}#{tool}<(deps)]  ← result→input 流");

        for (int g = 0; g < plan.ExecutionGroups.Count; g++)
        {
            var parallel = plan.ExecutionGroups[g].Count > 1 ? "||" : "->";
            sb.Append($"{parallel}[G{g}:");

            for (int i = 0; i < plan.ExecutionGroups[g].Count; i++)
            {
                var idx = plan.ExecutionGroups[g][i];
                var p = plan.SubTasks[idx];
                var tool = p.AssignedTool != null ? $"#{p.AssignedTool}" : "";
                var deps = p.DependsOn.Count > 0
                    ? $"<({string.Join(",", p.DependsOn)})"
                    : "";
                var crit = criticalPath.Contains(idx) ? "★" : "";
                if (i > 0) sb.Append('|');
                var desc = p.Description.Length > 60 ? p.Description[..57] + "…" : p.Description;
                sb.Append($"{idx}:{desc}{tool}{deps}{crit}");
            }
            sb.AppendLine("]");
        }

        // Critical path annotation
        if (criticalPath.Count > 0)
        {
            var critIndices = string.Join(", ", criticalPath.Order());
            sb.AppendLine($"## [P:crit] — 关键路径: [{critIndices}]");
            sb.AppendLine("  关键路径子任务决定整体延迟，投入更多推理 effort");
        }

        // Dependency flow hints
        var flowHints = new List<string>();
        for (int i = 0; i < plan.SubTasks.Count; i++)
        {
            var p = plan.SubTasks[i];
            if (p.DependsOn.Count > 0)
                flowHints.Add($"  [{i}] ← [{string.Join(",", p.DependsOn)}] result; prev output → this input");
        }
        if (flowHints.Count > 0)
        {
            sb.AppendLine("## [P:flow] — 结果传递");
            foreach (var h in flowHints)
                sb.AppendLine(h);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Find critical path using longest-path in DAG via topological sort.
    /// Returns indices of sub-tasks on the critical path.
    /// </summary>
    public static HashSet<int> FindCriticalPath(List<SubTaskPlan> plans)
    {
        int n = plans.Count;
        if (n == 0) return [];

        var inEdges = new List<int>[n];
        var outEdges = new List<int>[n];
        for (int i = 0; i < n; i++)
        {
            inEdges[i] = [];
            outEdges[i] = [];
        }

        for (int i = 0; i < n; i++)
        {
            foreach (var dep in plans[i].DependsOn)
            {
                if (dep >= 0 && dep < n && dep != i)
                {
                    outEdges[dep].Add(i);
                    inEdges[i].Add(dep);
                }
            }
        }

        // Topological order via Kahn
        var order = new List<int>();
        var inDegree = new int[n];
        for (int i = 0; i < n; i++)
            inDegree[i] = inEdges[i].Count;

        var queue = new Queue<int>();
        for (int i = 0; i < n; i++)
            if (inDegree[i] == 0) queue.Enqueue(i);

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            order.Add(node);
            foreach (var next in outEdges[node])
            {
                inDegree[next]--;
                if (inDegree[next] == 0)
                    queue.Enqueue(next);
            }
        }

        // Longest path (each edge weight = 1)
        var dist = new int[n];
        var prev = new int[n];
        Array.Fill(prev, -1);

        foreach (var u in order)
        {
            foreach (var v in outEdges[u])
            {
                if (dist[u] + 1 > dist[v])
                {
                    dist[v] = dist[u] + 1;
                    prev[v] = u;
                }
            }
        }

        // Find sink with max distance
        var sink = 0;
        for (int i = 0; i < n; i++)
            if (dist[i] > dist[sink]) sink = i;

        // Trace back
        var path = new HashSet<int>();
        var cur = sink;
        while (cur >= 0)
        {
            path.Add(cur);
            cur = prev[cur];
        }

        return path;
    }

    /// <summary>
    /// Export the CompositionPlan to .livingtree/refs/{traceId}-composition.md
    /// for debugging and observability.
    /// </summary>
    private static async Task ExportPlanToRefsAsync(CompositionPlan plan, string traceId)
    {
        try
        {
            var refsDir = Path.Combine(Directory.GetCurrentDirectory(), ".livingtree", "refs");
            Directory.CreateDirectory(refsDir);
            var path = Path.Combine(refsDir, $"{traceId}-composition.md");

            var sb = new StringBuilder();
            sb.AppendLine("# CompositionPlan");
            sb.AppendLine($"- TraceId: {traceId}");
            sb.AppendLine($"- Sub-tasks: {plan.SubTasks.Count}");
            sb.AppendLine($"- Groups: {plan.ExecutionGroups.Count}");
            sb.AppendLine();

            for (int i = 0; i < plan.SubTasks.Count; i++)
            {
                var p = plan.SubTasks[i];
                sb.AppendLine($"## [{i}] {p.Description}");
                sb.AppendLine($"- AssignedTool: {p.AssignedTool ?? "(none)"}");
                sb.AppendLine($"- Domain: {p.Domain}");
                sb.AppendLine($"- DependsOn: {(p.DependsOn.Count > 0 ? string.Join(", ", p.DependsOn) : "(none)")}");
                sb.AppendLine();
            }

            sb.AppendLine("## Execution Groups");
            for (int g = 0; g < plan.ExecutionGroups.Count; g++)
            {
                var parallel = plan.ExecutionGroups[g].Count > 1 ? "parallel" : "sequential";
                sb.AppendLine($"### Group {g} ({parallel})");
                foreach (var idx in plan.ExecutionGroups[g])
                    sb.AppendLine($"- [{idx}] {plan.SubTasks[idx].Description}");
                sb.AppendLine();
            }

            await File.WriteAllTextAsync(path, sb.ToString()).ConfigureAwait(false);
        }
        catch
        {
            // non-critical; best-effort
        }
    }
}
