using System.ComponentModel;
using System.Text;
using System.Text.Json;

namespace LTAI.Agent.Tools;

/// <summary>
/// Multi-step plan approval workflow.
/// Ported from DeepSeek-Reasonix plan-core.ts.
/// State machine: proposed → approved → executing → completed.
/// </summary>
public static class PlanTools
{
    private static PlanState? _currentPlan;
    private static readonly object _lock = new();

    private sealed record PlanStep(string Id, string Title, string Action, string Risk = "low", string[]? Targets = null);
    private sealed record PlanState(
        string Summary,
        string Plan,
        PlanStep[] Steps,
        int CurrentStep,
        string Status, // "proposed" | "approved" | "executing" | "completed" | "rejected"
        DateTime CreatedAt);

    [Description("Submit a multi-step plan for review (user must approve before execution)")]
    public static string SubmitPlan(
        [Description("Plan summary (one line)")] string summary,
        [Description("JSON array of steps: [{id, title, action}, ...]")] string stepsJson,
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

        lock (_lock)
        {
            _currentPlan = new PlanState(summary, plan, steps, 0, "proposed", DateTime.UtcNow);
        }

        var sb = new StringBuilder();
        sb.AppendLine($"## Plan: {summary}\n");
        sb.AppendLine(plan);
        sb.AppendLine("\n### Steps\n");
        sb.AppendLine("| # | Step | Action | Risk |");
        sb.AppendLine("|---|------|--------|------|");
        for (int i = 0; i < steps.Length; i++)
            sb.AppendLine($"| {i + 1} | {steps[i].Title} | {steps[i].Action} | {steps[i].Risk} |");

        sb.AppendLine("\n---");
        sb.AppendLine("Reply **approve** to start execution, **refine** to modify, or **cancel** to discard.");
        return sb.ToString();
    }

    [Description("Mark the current plan step as complete and advance")]
    public static string MarkStepComplete(
        [Description("Step ID")] string stepId,
        [Description("One-sentence result summary")] string result,
        [Description("Optional notes")] string? notes = null)
    {
        lock (_lock)
        {
            if (_currentPlan == null) return "No active plan";
            if (_currentPlan.Status != "executing") return "Plan is not currently executing";

            var step = _currentPlan.Steps.FirstOrDefault(s => s.Id == stepId);
            if (step == null) return $"Step '{stepId}' not found in plan";

            var nextIdx = Array.IndexOf(_currentPlan.Steps, step) + 1;
            if (nextIdx >= _currentPlan.Steps.Length)
            {
                _currentPlan = _currentPlan with { Status = "completed", CurrentStep = _currentPlan.Steps.Length };
                return $"✅ Plan complete: **{_currentPlan.Summary}**\n\nFinal step '{step.Title}': {result}";
            }

            _currentPlan = _currentPlan with { CurrentStep = nextIdx };
            var next = _currentPlan.Steps[nextIdx];
            return $"✅ Step '{step.Title}' complete: {result}\n\n➡️ Next: **{next.Title}** — {next.Action}";
        }
    }

    [Description("Revise the remaining steps of an in-flight plan")]
    public static string RevisePlan(
        [Description("Reason for revision")] string reason,
        [Description("JSON array of new remaining steps: [{id, title, action}, ...]")] string newStepsJson)
    {
        PlanStep[] newSteps;
        try
        {
            newSteps = JsonSerializer.Deserialize<PlanStep[]>(newStepsJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        }
        catch (JsonException ex)
        {
            return $"Invalid JSON: {ex.Message}";
        }

        lock (_lock)
        {
            if (_currentPlan == null) return "No active plan";
            if (_currentPlan.Status != "executing") return "Plan is not currently executing";

            var doneSteps = _currentPlan.Steps.Take(_currentPlan.CurrentStep).ToArray();
            var allSteps = doneSteps.Concat(newSteps).ToArray();

            _currentPlan = _currentPlan with { Steps = allSteps };
            return $"🔄 Plan revised: {reason}\n{string.Join("\n", newSteps.Select((s, i) => $"  {i + 1}. {s.Title}"))}";
        }
    }

    [Description("Show current plan status")]
    public static string PlanStatus()
    {
        lock (_lock)
        {
            if (_currentPlan == null) return "No active plan.";

            var sb = new StringBuilder();
            sb.AppendLine($"## Plan: {_currentPlan.Summary}");
            sb.AppendLine($"Status: **{_currentPlan.Status}**");
            sb.AppendLine();

            for (int i = 0; i < _currentPlan.Steps.Length; i++)
            {
                var s = _currentPlan.Steps[i];
                var status = i < _currentPlan.CurrentStep ? "✅" :
                             i == _currentPlan.CurrentStep ? "➡️" : "⬜";
                sb.AppendLine($"{status} **{s.Title}** — {s.Action}");
            }
            return sb.ToString();
        }
    }

    /// <summary>Approve the current plan (called via user confirmation).</summary>
    public static string ApprovePlan()
    {
        lock (_lock)
        {
            if (_currentPlan == null) return "No plan to approve";
            if (_currentPlan.Status != "proposed") return "Plan already " + _currentPlan.Status;
            _currentPlan = _currentPlan with { Status = "approved" };
            return $"✅ Plan approved! Starting execution...";
        }
    }

    /// <summary>Start executing an approved plan.</summary>
    public static string StartExecution()
    {
        lock (_lock)
        {
            if (_currentPlan == null) return "No plan";
            if (_currentPlan.Status != "approved") return "Plan must be approved first";
            _currentPlan = _currentPlan with { Status = "executing" };
            var first = _currentPlan.Steps[0];
            return $"🚀 Starting plan: **{_currentPlan.Summary}**\n\nStep 1: **{first.Title}** — {first.Action}";
        }
    }
}
