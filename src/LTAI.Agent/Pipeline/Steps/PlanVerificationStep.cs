using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Pipeline.Steps;

public sealed record PlanDeviation(
    string Type,       // "UnusedPlannedTool" | "UnplannedTool" | "WrongGroup" | "ToolFailed"
    string Details);

public sealed class PlanVerificationStep : IPipelineStep
{
    private readonly ILogger<PlanVerificationStep> _logger;

    public string Name => "PlanVerification";

    public PlanVerificationStep(ILogger<PlanVerificationStep>? logger = null)
    {
        _logger = logger ?? NullLogger<PlanVerificationStep>.Instance;
    }

    public async Task<MessageContext> ProcessAsync(MessageContext context)
    {
        if (!context.TryGet<CompositionPlan>("CompositionPlan", out var plan) || plan == null)
            return context;

        var deviations = new List<PlanDeviation>();

        // ── Extract actual tool calls from messages ──
        var actualCalls = ExtractToolCalls(context.Messages);
        var calledNames = new HashSet<string>(actualCalls, StringComparer.OrdinalIgnoreCase);

        // ── 1. Check for unused planned tools ──
        var plannedTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plannedToolToTask = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in plan.SubTasks)
        {
            if (task.AssignedTool != null)
            {
                plannedTools.Add(task.AssignedTool);
                plannedToolToTask[task.AssignedTool] = task.Index;
            }
        }

        foreach (var tool in plannedTools)
        {
            if (!calledNames.Contains(tool))
            {
                var taskIdx = plannedToolToTask.GetValueOrDefault(tool, -1);
                deviations.Add(new PlanDeviation("UnusedPlannedTool",
                    $"计划中的工具 {tool} (子任务 [{taskIdx}]) 未被调用"));
            }
        }

        // ── 2. Check for unplanned tools used (excluding pinned tools) ──
        var pinnedTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ReadFileContent", "RunCommand", "ListFiles", "GetCurrentDateTime",
        };

        foreach (var name in calledNames)
        {
            if (!plannedTools.Contains(name) && !pinnedTools.Contains(name))
            {
                deviations.Add(new PlanDeviation("UnplannedTool",
                    $"使用了计划外的工具 {name}"));
            }
        }

        // ── 3. Check for failed tools ──
        foreach (var (name, args, result) in context.ToolCalls)
        {
            if (result.Contains("\"success\":false", StringComparison.OrdinalIgnoreCase) ||
                result.Contains("error", StringComparison.OrdinalIgnoreCase))
            {
                deviations.Add(new PlanDeviation("ToolFailed",
                    $"工具 {name} 执行失败: {Truncate(result, 80)}"));
            }
        }

        if (deviations.Count > 0)
        {
            context.Set("_PlanDeviations", deviations);
            context.Set("PlanVerificationBlocked", true);

            // Inject deviation report for next iteration
            var report = string.Join("\n", deviations.Select(d => $"  - {d.Type}: {d.Details}"));
            var msg = $"## [P:verification] — 计划执行偏差\n{report}\n请在下一次执行中修正。";

            lock (context.MessagesLock)
                context.Messages.Add(new ChatMessage(ChatRole.System, msg));

            _logger.LogWarning("PlanVerificationStep: {Count} deviation(s) detected\n{Report}",
                deviations.Count, report);
        }
        else
        {
            _logger.LogDebug("PlanVerificationStep: plan executed correctly");
        }

        return context;
    }

    private static HashSet<string> ExtractToolCalls(List<ChatMessage> messages)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var msg in messages)
        {
            if (msg.Contents == null) continue;
            foreach (var content in msg.Contents)
            {
                if (content is FunctionCallContent fc && fc.Name != null)
                    names.Add(fc.Name);
            }
        }
        return names;
    }

    private static string Truncate(string s, int maxLen)
        => s.Length <= maxLen ? s : s[..maxLen] + "…";
}
