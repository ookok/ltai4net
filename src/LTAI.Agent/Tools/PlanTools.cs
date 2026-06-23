using System.ComponentModel;
using System.Text;
using System.Text.Json;
using LTAI.AI;
using LTAI.Agent.Execution;

namespace LTAI.Agent.Tools;

public static class PlanTools
{
    /// <summary>Bounded plan state store. Set during DI init.</summary>
    public static PlanStore? Store { get; set; }

    /// <summary>Set by ChatAgent before invoking plan tools, scoping PlanState to a session.</summary>
    public static string? SessionId { get; set; }

    /// <summary>
    /// Optional ExecutionEngine for plan execution. Set during DI initialization.
    /// When set, SubmitPlan → PlanAsync, ApprovePlan → ExecuteAsync.
    /// </summary>
    public static Execution.ExecutionEngine? ExecutionEngine { get; set; }

    private static string SessionKey => SessionId ?? "default";

    private static string PlanStateKey(string session) => $"plan:{session}";

    private static string ExecPlanKey(string session) => $"exec:{session}";

    private static PlanState? CurrentPlan
    {
        get => Store!.TryGet<PlanState>(PlanStateKey(SessionKey), out var p) ? p : null;
        set
        {
            if (value != null) Store!.Set(PlanStateKey(SessionKey), value);
            else Store!.Remove(PlanStateKey(SessionKey));
        }
    }

    private static Execution.ExecutionPlan? CurrentExecPlan
    {
        get => Store!.TryGet<Execution.ExecutionPlan>(ExecPlanKey(SessionKey), out var p) ? p : null;
        set
        {
            if (value != null) Store!.Set(ExecPlanKey(SessionKey), value);
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
        && DateTime.UtcNow - plan.CreatedAt > TimeSpan.FromMinutes(30);

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
    public static async Task<string> SubmitPlan(
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

        // If ExecutionEngine is available, plan the routing
        if (ExecutionEngine != null)
        {
            try
            {
                var execPlan = await ExecutionEngine.PlanAsync(summary).ConfigureAwait(false);
                CurrentExecPlan = execPlan;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"PlanTools: ExecutionEngine planning failed: {ex.Message}");
            }
        }

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

        if (!string.IsNullOrWhiteSpace(step.Acceptance))
        {
            var acceptanceCheck = VerifyAcceptance(step.Acceptance, result, notes);
            if (!acceptanceCheck.IsMet)
                return $"⚠️ Step '{step.Title}' acceptance criteria not fully met:\n" +
                       $"  Criteria: {step.Acceptance}\n" +
                       $"  Issue: {acceptanceCheck.Reason}\n\n" +
                       "Submit the result again with stronger evidence, or use RevisePlan to adjust the criteria.";
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
        var combined = $"{result} {notes ?? ""}";
        var lowerCriteria = criteria.ToLowerInvariant();
        var lowerCombined = combined.ToLowerInvariant();

        var negationChecks = new (string Pattern, string Hint)[]
        {
            ("no error", "Result does not confirm 'no errors'"),
            ("no failure", "Result does not confirm 'no failures'"),
            ("no warning", "Result does not confirm 'no warnings'"),
            ("pass test", "Result does not confirm tests passing"),
            ("all test", "Result does not confirm all tests"),
            ("no regression", "Result does not confirm no regression"),
            ("zero defect", "Result does not confirm zero defects"),
        };
        foreach (var (pattern, hint) in negationChecks)
        {
            if (lowerCriteria.Contains(pattern))
            {
                var positiveIndicators = new[] { "pass", "success", "ok", "done", "complete", "0 error", "0 failure" };
                var hasAny = positiveIndicators.Any(p => lowerCombined.Contains(p));
                if (!hasAny)
                    return (false, $"{hint} were achieved. Expected indicators: none found.");
            }
        }

        var stopwords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "and", "the", "that", "this", "with", "must", "should", "after", "for", "but",
            "not", "are", "was", "were", "been", "have", "has", "had", "will", "would",
            "could", "can", "may", "all", "each", "every", "both", "few", "more",
            "most", "other", "some", "such", "than", "too", "very", "just",
            "的", "了", "在", "是", "有", "和", "就", "不", "都", "要", "会",
            "一个", "上", "也", "很", "到", "说", "去", "能", "没有", "好",
        };
        var requiredWords = criteria.Split([' ', ',', '，', '。'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('"', '\'', '.', '(', ')', '”', '“', '【', '】', '：'))
            .Where(w => w.Length > 4 && !stopwords.Contains(w))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (requiredWords.Count > 0)
        {
            var missing = requiredWords.Where(w => !lowerCombined.Contains(w.ToLowerInvariant())).ToList();
            if (missing.Count > requiredWords.Count / 2)
            {
                return (false,
                    $"Result is missing {missing.Count}/{requiredWords.Count} key terms from criteria: " +
                    $"'{string.Join(", ", missing.Take(5))}'.");
            }
            if (missing.Count > 0)
            {
                return (true,
                    $"Note: criteria mentions '{string.Join(", ", missing)}' but result doesn't explicitly reference it.");
            }
        }

        if (result.Length < criteria.Length * 0.3 && criteria.Length > 30)
        {
            return (false,
                $"Result is too brief ({result.Length} chars vs criteria length {criteria.Length}). " +
                "Include more detail in the result.");
        }

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

    public static async Task<string> ApprovePlan()
    {
        var plan = CurrentPlan;
        if (plan == null) return "No plan to approve";
        {
            var expired = ExpiredGuard(plan);
            if (expired != null) return expired;
        }
        if (plan.Status != "proposed") return "Plan already " + plan.Status;
        CurrentPlan = plan with { Status = "approved" };

        var execPlan = CurrentExecPlan;
        if (ExecutionEngine != null && execPlan != null)
        {
            try
            {
                var result = await ExecutionEngine.ExecuteAsync(execPlan).ConfigureAwait(false);
                if (result.Success)
                {
                    CurrentPlan = CurrentPlan! with { Status = "completed", CurrentStep = CurrentPlan!.Steps.Length };
                    return $"✅ Plan approved and executed!\n\n{result.Text}";
                }
                return $"✅ Plan approved. Execution note: {result.ErrorMessage ?? "started"}";
            }
            catch (Exception ex)
            {
                return $"✅ Plan approved. Execution engine unavailable: {ex.Message}";
            }
        }

        return $"✅ Plan approved! Use StartExecution to begin.";
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

        var execPlan = CurrentExecPlan;
        if (ExecutionEngine != null && execPlan != null)
        {
            var startResult = ExecutionEngine.PlanAsync(plan.Summary).GetAwaiter().GetResult();
            if (startResult != null)
            {
                CurrentExecPlan = startResult;
                _ = FireAndForgetExecute(startResult);
            }
        }

        return $"🚀 Starting plan: **{CurrentPlan!.Summary}**\n\nStep 1: **{first.Title}** — {first.Action}";
    }

    private static async Task FireAndForgetExecute(Execution.ExecutionPlan execPlan)
    {
        try
        {
            await ExecutionEngine!.ExecuteAsync(execPlan).ConfigureAwait(false);
        }
        catch { }
    }

    /// <summary>Periodically evict stale session plans. Called by ChatAgent.</summary>
    public static void EvictStaleSessions()
    {
        Store?.EvictStaleSessions();
    }
}
