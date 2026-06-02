using System.ComponentModel;
using System.Text;
using System.Text.Json;
using LTAI.AI;

namespace LTAI.Agent.Tools;

public static class PlanTools
{
    // A4: was static AsyncLocal (cross-user leak across async Continuations).
    // Now using explicit static field for single-user TUI/Desktop — multi-user
    // scenarios should store per-agent PlanState in the AgentSession.
    private static PlanState? _currentPlan;

    private sealed record PlanStep(
        string Id, string Title, string Action,
        string Risk = "low", string[]? Targets = null,
        string? Acceptance = null);

    private sealed record PlanState(
        string Summary, string Plan, PlanStep[] Steps,
        int CurrentStep, string Status, DateTime CreatedAt);

    [Description("Plan mode 已完成。调用此工具退出 Plan mode 切换到 Build mode，允许执行代码变更。")]
    public static string PlanExit(
        [Description("最终计划概述")] string summary,
        [Description("下一步执行建议")] string? nextSteps = null)
    {
        return $"✅ Plan complete.\n{summary}" + (nextSteps != null ? $"\n\nNext: {nextSteps}" : "");
    }

    [Description("Submit a multi-step plan for review (user must approve before execution)")]
    public static string SubmitPlan(
        [Description("Plan summary (one line)")] string summary,
        [Description("JSON array of steps: [{id, title, action, acceptance?}, ...]")] string stepsJson,
        [Description("Markdown plan body")] string plan)
    {
        PlanStep[] steps;
        try
        {
            steps = JsonSerializer.Deserialize<PlanStep[]>(stepsJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        }
        catch (JsonException ex)
        {
            return $"Invalid JSON: {ex.Message}";
        }

        if (steps.Length == 0) return "Plan must have at least one step";

        _currentPlan = new PlanState(summary, plan, steps, 0, "proposed", DateTime.UtcNow);

        var sb = new StringBuilder();
        sb.AppendLine($"## Plan: {summary}\n");
        sb.AppendLine(plan);
        sb.AppendLine("\n### Steps\n");
        sb.AppendLine("| # | Step | Action | Risk | Acceptance |");
        sb.AppendLine("|---|------|--------|------|------------|");
        for (int i = 0; i < steps.Length; i++)
        {
            var acc = !string.IsNullOrEmpty(steps[i].Acceptance) ? steps[i].Acceptance : "—";
            sb.AppendLine($"| {i + 1} | {steps[i].Title} | {steps[i].Action} | {steps[i].Risk} | `{acc}` |");
        }

        sb.AppendLine("\n---");
        sb.AppendLine("Reply **approve** to start execution, **refine** to modify, or **cancel** to discard.");
        return sb.ToString();
    }

    [Description("Mark the current plan step as complete and advance.")]
    public static string MarkStepComplete(
        [Description("Step ID")] string stepId,
        [Description("One-sentence result summary")] string result,
        [Description("Optional notes")] string? notes = null)
    {
        var plan = _currentPlan;
        if (plan == null) return "No active plan";
        if (plan.Status != "executing") return "Plan is not currently executing";

        var step = plan.Steps.FirstOrDefault(s => s.Id == stepId);
        if (step == null) return $"Step '{stepId}' not found in plan";

        var nextIdx = Array.IndexOf(plan.Steps, step) + 1;
        if (nextIdx >= plan.Steps.Length)
        {
            _currentPlan = plan with { Status = "completed", CurrentStep = plan.Steps.Length };
            return $"✅ Plan complete: **{_currentPlan!.Summary}**\n\nFinal step '{step.Title}': {result}";
        }

        _currentPlan = plan with { CurrentStep = nextIdx };
        var next = _currentPlan!.Steps[nextIdx];
        return $"✅ Step '{step.Title}' complete: {result}\n\n➡️ Next: **{next.Title}** — {next.Action}";
    }

    [Description("Revise the remaining steps of an in-flight plan")]
    public static string RevisePlan(
        [Description("Reason for revision")] string reason,
        [Description("JSON array of new remaining steps")] string newStepsJson)
    {
        var newSteps = JsonSerializer.Deserialize<PlanStep[]>(newStepsJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        var plan = _currentPlan;
        if (plan == null) return "No active plan";
        if (plan.Status != "executing") return "Plan is not currently executing";

        var doneSteps = plan.Steps.Take(plan.CurrentStep).ToArray();
        _currentPlan = plan with { Steps = doneSteps.Concat(newSteps).ToArray() };
        return $"🔄 Plan revised: {reason}\n{string.Join("\n", newSteps.Select((s, i) => $"  {i + 1}. {s.Title}"))}";
    }

    [Description("Show current plan status")]
    public static string PlanStatus()
    {
        var plan = _currentPlan;
        if (plan == null) return "No active plan.";

        var sb = new StringBuilder();
        sb.AppendLine($"## Plan: {plan.Summary}");
        sb.AppendLine($"Status: **{plan.Status}**\n");
        for (int i = 0; i < plan.Steps.Length; i++)
        {
            var s = plan.Steps[i];
            var status = i < plan.CurrentStep ? "✅" : i == plan.CurrentStep ? "➡️" : "⬜";
            sb.AppendLine($"{status} **{s.Title}** — {s.Action}");
        }
        return sb.ToString();
    }

    public static string ApprovePlan()
    {
        var plan = _currentPlan;
        if (plan == null) return "No plan to approve";
        if (plan.Status != "proposed") return "Plan already " + plan.Status;
        _currentPlan = plan with { Status = "approved" };
        return $"✅ Plan approved! Starting execution...";
    }

    public static string StartExecution()
    {
        var plan = _currentPlan;
        if (plan == null) return "No plan";
        if (plan.Status != "approved") return "Plan must be approved first";
        _currentPlan = plan with { Status = "executing" };
        var first = _currentPlan!.Steps[0];
        return $"🚀 Starting plan: **{_currentPlan!.Summary}**\n\nStep 1: **{first.Title}** — {first.Action}";
    }
}
