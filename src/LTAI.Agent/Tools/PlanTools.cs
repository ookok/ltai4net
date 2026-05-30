using System.ComponentModel;
using System.Text;
using System.Text.Json;

namespace LTAI.Agent.Tools;

/// <summary>
/// Multi-step plan approval workflow.
/// Ported from DeepSeek-Reasonix plan-core.ts.
/// State machine: proposed → approved → executing → completed.
/// State is per-AsyncLocal context (not static), so concurrent calls/tasks
/// do not interfere with each other.
/// </summary>
public static class PlanTools
{
    /// <summary>Per-async-context plan state — isolates concurrent users/agents.</summary>
    private static readonly AsyncLocal<PlanState?> _currentPlan = new();

    /// <summary>
    /// Acceptance check rules for a plan step. Deserialized from JSON in the step definition.
    /// Supported formats:
    ///   - {"contains": ["keyword1", "keyword2"]} — result must contain all keywords
    ///   - {"min_length": 50} — result must be at least N chars
    ///   - {"regex": "pattern"} — result must match the regex
    ///   - null/absent — skip verification (backward compatible)
    /// </summary>
    private sealed record PlanStep(
        string Id, string Title, string Action,
        string Risk = "low", string[]? Targets = null,
        string? Acceptance = null);  // JSON string of acceptance rules

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

        _currentPlan.Value = new PlanState(summary, plan, steps, 0, "proposed", DateTime.UtcNow);

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

    [Description("Mark the current plan step as complete and advance. " +
                 "Runs acceptance check if the step defines acceptance rules.")]
    public static string MarkStepComplete(
        [Description("Step ID")] string stepId,
        [Description("One-sentence result summary")] string result,
        [Description("Optional notes")] string? notes = null)
    {
        var plan = _currentPlan.Value;
        if (plan == null) return "No active plan";
        if (plan.Status != "executing") return "Plan is not currently executing";

        var step = plan.Steps.FirstOrDefault(s => s.Id == stepId);
        if (step == null) return $"Step '{stepId}' not found in plan";

        // Run acceptance check if step defines one
        var checkResult = RunAcceptanceCheck(step, result);
        if (checkResult != null && !checkResult.StartsWith("✅"))
        {
            return $"⚠️ Step '{step.Title}' completed but acceptance check failed:\n{checkResult}\n\n" +
                   $"Result: {result}\n" +
                   $"Use `plan_revise` to adjust, or re-submit with corrected result.";
        }

        var nextIdx = Array.IndexOf(plan.Steps, step) + 1;
        if (nextIdx >= plan.Steps.Length)
        {
            _currentPlan.Value = plan with { Status = "completed", CurrentStep = plan.Steps.Length };
            return $"✅ Plan complete: **{_currentPlan.Value!.Summary}**\n\nFinal step '{step.Title}': {result}";
        }

        _currentPlan.Value = plan with { CurrentStep = nextIdx };
        var next = _currentPlan.Value!.Steps[nextIdx];
        return $"✅ Step '{step.Title}' complete: {result}\n\n➡️ Next: **{next.Title}** — {next.Action}";
    }

    /// <summary>
    /// Run acceptance check for a step's result. Returns null if no check defined,
    /// "✅ ..." if passed, or an error description if failed.
    /// </summary>
    private static string? RunAcceptanceCheck(PlanStep step, string result)
    {
        if (string.IsNullOrEmpty(step.Acceptance)) return null;

        try
        {
            using var doc = JsonDocument.Parse(step.Acceptance);
            var root = doc.RootElement;

            // Check: "contains": ["keyword1", "keyword2", ...]
            if (root.TryGetProperty("contains", out var contains) && contains.ValueKind == JsonValueKind.Array)
            {
                var missing = new List<string>();
                foreach (var kw in contains.EnumerateArray())
                {
                    var keyword = kw.GetString();
                    if (keyword != null && !result.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                        missing.Add(keyword);
                }
                if (missing.Count > 0)
                    return $"❌ Missing keywords: {string.Join(", ", missing)}";
            }

            // Check: "min_length": 50
            if (root.TryGetProperty("min_length", out var minLen) && minLen.TryGetInt32(out var min))
            {
                if (result.Length < min)
                    return $"❌ Result too short: {result.Length} chars (minimum {min})";
            }

            // Check: "regex": "pattern"
            if (root.TryGetProperty("regex", out var regexEl))
            {
                var pattern = regexEl.GetString();
                if (!string.IsNullOrEmpty(pattern) && !System.Text.RegularExpressions.Regex.IsMatch(result, pattern))
                    return $"❌ Result does not match pattern: `{pattern}`";
            }

            return $"✅ Acceptance passed";
        }
        catch (JsonException ex)
        {
            return $"❌ Invalid acceptance rule JSON: {ex.Message}";
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

        var plan = _currentPlan.Value;
        if (plan == null) return "No active plan";
        if (plan.Status != "executing") return "Plan is not currently executing";

        var doneSteps = plan.Steps.Take(plan.CurrentStep).ToArray();
        var allSteps = doneSteps.Concat(newSteps).ToArray();

        _currentPlan.Value = plan with { Steps = allSteps };
        return $"🔄 Plan revised: {reason}\n{string.Join("\n", newSteps.Select((s, i) => $"  {i + 1}. {s.Title}"))}";
    }

    [Description("Show current plan status")]
    public static string PlanStatus()
    {
        var plan = _currentPlan.Value;
        if (plan == null) return "No active plan.";

        var sb = new StringBuilder();
        sb.AppendLine($"## Plan: {plan.Summary}");
        sb.AppendLine($"Status: **{plan.Status}**");
        sb.AppendLine();

        for (int i = 0; i < plan.Steps.Length; i++)
        {
            var s = plan.Steps[i];
            var status = i < plan.CurrentStep ? "✅" :
                         i == plan.CurrentStep ? "➡️" : "⬜";
            sb.AppendLine($"{status} **{s.Title}** — {s.Action}");
        }
        return sb.ToString();
    }

    /// <summary>Approve the current plan (called via user confirmation).</summary>
    public static string ApprovePlan()
    {
        var plan = _currentPlan.Value;
        if (plan == null) return "No plan to approve";
        if (plan.Status != "proposed") return "Plan already " + plan.Status;
        _currentPlan.Value = plan with { Status = "approved" };
        return $"✅ Plan approved! Starting execution...";
    }

    /// <summary>Start executing an approved plan.</summary>
    public static string StartExecution()
    {
        var plan = _currentPlan.Value;
        if (plan == null) return "No plan";
        if (plan.Status != "approved") return "Plan must be approved first";
        _currentPlan.Value = plan with { Status = "executing" };
        var first = _currentPlan.Value!.Steps[0];
        return $"🚀 Starting plan: **{_currentPlan.Value!.Summary}**\n\nStep 1: **{first.Title}** — {first.Action}";
    }
}
