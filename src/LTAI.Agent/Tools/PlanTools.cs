using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using LTAI.AI;

namespace LTAI.Agent.Tools;

public static class PlanTools
{
    // F1: Per-session plan isolation using ConcurrentDictionary + AsyncLocal.
    // Replaces the old process-wide static field that leaked across conversations.
    // ChatAgent sets SessionKey before any plan tool call.
    private static readonly ConcurrentDictionary<string, PlanState> _plans = new();
    private static readonly AsyncLocal<string?> _sessionId = new();
    private static readonly TimeSpan PlanTimeout = TimeSpan.FromMinutes(30);

    /// <summary>Set by ChatAgent before invoking plan tools, scoping PlanState to a session.</summary>
    public static string? SessionId { get => _sessionId.Value; set => _sessionId.Value = value; }

    private static string SessionKey => SessionId ?? "default";

    private static PlanState? CurrentPlan
    {
        get => _plans.TryGetValue(SessionKey, out var p) ? p : null;
        set
        {
            if (value != null)
                _plans[SessionKey] = value;
            else
                _plans.TryRemove(SessionKey, out _);
        }
    }

    private sealed record PlanStep(
        string Id, string Title, string Action,
        string Risk = "low", string[]? Targets = null,
        string? Acceptance = null);

    private sealed record PlanState(
        string Summary, string Plan, PlanStep[] Steps,
        int CurrentStep, string Status, DateTime CreatedAt);

    private static bool IsExpired(PlanState? plan) =>
        plan is { Status: "proposed" or "approved" or "executing" }
        && DateTime.UtcNow - plan.CreatedAt > PlanTimeout;

    private static string? ExpiredGuard(PlanState? plan)
    {
        if (plan != null && IsExpired(plan))
        {
            CurrentPlan = plan with { Status = "timed_out" };
            return "Plan has expired (over 30 minutes). Submit a new plan.";
        }
        return null;
    }

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
        if (CurrentPlan is { Status: "approved" or "executing" })
            return $"A plan is currently **{CurrentPlan.Status}**. Call PlanStatus() first, or cancel it to submit a new plan.";

        var expired = ExpiredGuard(CurrentPlan);
        if (expired != null) { /* expired auto-cleared, proceed */ }

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

        CurrentPlan = new PlanState(summary, plan, steps, 0, "proposed", DateTime.UtcNow);

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
        var plan = CurrentPlan;
        if (plan == null) return "No active plan";
        {
            var expired = ExpiredGuard(plan);
            if (expired != null) return expired;
        }
        if (plan.Status != "executing") return "Plan is not currently executing";

        var step = plan.Steps.FirstOrDefault(s => s.Id == stepId);
        if (step == null) return $"Step '{stepId}' not found in plan";

        // F3: Check acceptance criteria if defined
        if (!string.IsNullOrWhiteSpace(step.Acceptance))
        {
            var acceptanceCheck = VerifyAcceptance(step.Acceptance, result, notes);
            if (!acceptanceCheck.IsMet)
                return $"⚠️ Step '{step.Title}' acceptance criteria not fully met:\n" +
                       $"  Criteria: {step.Acceptance}\n" +
                       $"  Issue: {acceptanceCheck.Reason}\n\n" +
                       $"Submit the result again with stronger evidence, or use RevisePlan to adjust the criteria.";
        }

        var nextIdx = Array.IndexOf(plan.Steps, step) + 1;
        if (nextIdx >= plan.Steps.Length)
        {
            CurrentPlan = plan with { Status = "completed", CurrentStep = plan.Steps.Length };
            return $"✅ Plan complete: **{CurrentPlan!.Summary}**\n\nFinal step '{step.Title}': {result}";
        }

        CurrentPlan = plan with { CurrentStep = nextIdx };
        var next = CurrentPlan!.Steps[nextIdx];
        return $"✅ Step '{step.Title}' complete: {result}\n\n➡️ Next: **{next.Title}** — {next.Action}";
    }

    private static (bool IsMet, string Reason) VerifyAcceptance(string criteria, string result, string? notes)
    {
        // Simple keyword-based verification: check if critical terms from criteria appear in result/notes
        var combined = $"{result} {notes ?? ""}";
        var lowerCriteria = criteria.ToLowerInvariant();
        var lowerCombined = combined.ToLowerInvariant();

        // Check for negation patterns in criteria
        if (lowerCriteria.Contains("no error") && !lowerCombined.Contains("no error")
            && !lowerCombined.Contains("0 errors") && !lowerCombined.Contains("zero errors"))
            return (false, "Result does not confirm 'no errors' were achieved.");

        if (lowerCriteria.Contains("pass") && lowerCriteria.Contains("test")
            && !lowerCombined.Contains("pass") && !lowerCombined.Contains("success"))
            return (false, "Result does not mention tests passing.");

        // Check for specific required keywords
        var requiredWords = criteria.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 4 && !w.Equals("and", StringComparison.OrdinalIgnoreCase)
                && !w.Equals("the", StringComparison.OrdinalIgnoreCase)
                && !w.Equals("that", StringComparison.OrdinalIgnoreCase)
                && !w.Equals("this", StringComparison.OrdinalIgnoreCase)
                && !w.Equals("with", StringComparison.OrdinalIgnoreCase)
                && !w.Equals("must", StringComparison.OrdinalIgnoreCase)
                && !w.Equals("should", StringComparison.OrdinalIgnoreCase)
                && !w.Equals("after", StringComparison.OrdinalIgnoreCase))
            .Select(w => w.Trim('"', '\'', '.', '(', ')'))
            .Where(w => w.Length > 4)
            .ToList();

        var missing = requiredWords.Where(w => !lowerCombined.Contains(w.ToLowerInvariant())).ToList();
        if (missing.Count > 0 && missing.Count <= requiredWords.Count / 2)
            // Some keywords missing — warn but don't block; partial match is acceptable
            return (true, $"Note: criteria mentions '{string.Join(", ", missing)}' but result doesn't explicitly reference it.");

        return (true, "");
    }

    [Description("Revise the remaining steps of an in-flight plan")]
    public static string RevisePlan(
        [Description("Reason for revision")] string reason,
        [Description("JSON array of new remaining steps")] string newStepsJson)
    {
        var newSteps = JsonSerializer.Deserialize<PlanStep[]>(newStepsJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        var plan = CurrentPlan;
        if (plan == null) return "No active plan";
        {
            var expired = ExpiredGuard(plan);
            if (expired != null) return expired;
        }
        if (plan.Status != "executing") return "Plan is not currently executing";

        var doneSteps = plan.Steps.Take(plan.CurrentStep).ToArray();
        CurrentPlan = plan with { Steps = doneSteps.Concat(newSteps).ToArray() };
        return $"🔄 Plan revised: {reason}\n{string.Join("\n", newSteps.Select((s, i) => $"  {i + 1}. {s.Title}"))}";
    }

    [Description("Show current plan status")]
    public static string PlanStatus()
    {
        var plan = CurrentPlan;
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
        var plan = CurrentPlan;
        if (plan == null) return "No plan to approve";
        {
            var expired = ExpiredGuard(plan);
            if (expired != null) return expired;
        }
        if (plan.Status != "proposed") return "Plan already " + plan.Status;
        CurrentPlan = plan with { Status = "approved" };
        return $"✅ Plan approved! Starting execution...";
    }

    public static string StartExecution()
    {
        var plan = CurrentPlan;
        if (plan == null) return "No plan";
        {
            var expired = ExpiredGuard(plan);
            if (expired != null) return expired;
        }
        if (plan.Status != "approved") return "Plan must be approved first";
        CurrentPlan = plan with { Status = "executing" };
        var first = CurrentPlan!.Steps[0];
        return $"🚀 Starting plan: **{CurrentPlan!.Summary}**\n\nStep 1: **{first.Title}** — {first.Action}";
    }
}
