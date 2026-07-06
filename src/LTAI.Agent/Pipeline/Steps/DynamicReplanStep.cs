using LTAI.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Pipeline.Steps;

public sealed class DynamicReplanStep : IPipelineStep
{
    private readonly ILogger<DynamicReplanStep> _logger;

    public string Name => "DynamicReplan";

    public DynamicReplanStep(ILogger<DynamicReplanStep>? logger = null)
    {
        _logger = logger ?? NullLogger<DynamicReplanStep>.Instance;
    }

    public async Task<MessageContext> ProcessAsync(MessageContext context)
    {
        if (!context.TryGet<CompositionPlan>("CompositionPlan", out var plan) || plan == null)
            return context;

        // ── Check for failed tool calls ──
        var failedTasks = new List<int>();
        var completedTasks = new HashSet<int>();

        foreach (var (name, args, result) in context.ToolCalls)
        {
            var isError = result.Contains("\"success\":false", StringComparison.OrdinalIgnoreCase)
                       || result.Contains("error", StringComparison.OrdinalIgnoreCase);

            // Find which sub-task(s) use this tool
            foreach (var task in plan.SubTasks)
            {
                if (string.Equals(task.AssignedTool, name, StringComparison.OrdinalIgnoreCase))
                {
                    if (isError)
                        failedTasks.Add(task.Index);
                    else
                        completedTasks.Add(task.Index);
                }
            }
        }

        if (failedTasks.Count == 0)
            return context;

        _logger.LogWarning("DynamicReplanStep: {Count} failed sub-task(s), re-planning remaining groups",
            failedTasks.Count);

        // ── Determine remaining un-executed and un-failed sub-tasks ──
        var completedOrFailed = new HashSet<int>(completedTasks);
        foreach (var f in failedTasks) completedOrFailed.Add(f);

        var remaining = new List<int>();
        for (int g = 0; g < plan.ExecutionGroups.Count; g++)
        {
            foreach (var idx in plan.ExecutionGroups[g])
            {
                if (!completedOrFailed.Contains(idx))
                    remaining.Add(idx);
            }
        }

        if (remaining.Count == 0)
        {
            // All remaining tasks have failed — inject recovery guidance
            var failMsg = $"## [P:replan] — 计划调整：以下子任务全部失败，请尝试替代方案\n"
                        + string.Join("\n", failedTasks.Select(i =>
                            $"  - [{i}] {plan.SubTasks[i].Description} (工具: {plan.SubTasks[i].AssignedTool ?? "none"})"));
            lock (context.MessagesLock)
                context.Messages.Add(new ChatMessage(ChatRole.System, failMsg));
            return context;
        }

        // ── Build reduced plan: only remaining tasks, preserve original dependencies ──
        var remainingSet = new HashSet<int>(remaining);
        var replanned = new List<SubTaskPlan>();

        foreach (var idx in remaining)
        {
            var original = plan.SubTasks[idx];
            // Filter dependencies to only include other remaining tasks
            var filteredDeps = original.DependsOn.Where(d => remainingSet.Contains(d)).ToList();
            replanned.Add(original with { DependsOn = filteredDeps });
        }

        var newGroups = CompositionStep.BuildExecutionGroups(replanned);

        var reducedPlan = new CompositionPlan(replanned, newGroups);
        context.Set("CompositionPlan", reducedPlan);

        // ── Store pre-selected tool names for ToolFilteringChatClient ──
        var preSelectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in replanned)
            if (p.AssignedTool != null)
                preSelectedNames.Add(p.AssignedTool);
        context.Set("_PreSelectedToolNames", preSelectedNames);

        // ── Inject re-plan message ──
        var criticalPath = CompositionStep.FindCriticalPath(replanned);
        var planText = CompositionStep.BuildPlanTextStatic(reducedPlan, criticalPath);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## [P:replan] — 动态重排（失败 {failedTasks.Count} 个，剩余 {remaining.Count} 个）");
        sb.AppendLine(string.Join("\n", failedTasks.Select(i =>
            $"  - ❌ [{i}] {plan.SubTasks[i].Description} (工具: {plan.SubTasks[i].AssignedTool ?? "none"})")));
        sb.AppendLine("\n已调整执行计划：");
        sb.Append(planText);

        lock (context.MessagesLock)
            context.Messages.Add(new ChatMessage(ChatRole.System, sb.ToString()));

        _logger.LogInformation("DynamicReplanStep: re-planned {Remaining} tasks into {Groups} groups",
            remaining.Count, newGroups.Count);

        return context;
    }
}
