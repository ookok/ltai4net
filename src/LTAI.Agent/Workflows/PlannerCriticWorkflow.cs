using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Workflows;

public sealed class PlannerCriticWorkflow
{
    private static readonly ActivitySource ActivitySource = new("LTAI.Agent.PlannerCritic");
    private const int MaxRevisionRounds = 2;

    private readonly ILogger<PlannerCriticWorkflow> _logger;
    private readonly Dictionary<string, AIAgent> _agents = new();

    public PlannerCriticWorkflow(ILogger<PlannerCriticWorkflow> logger)
    {
        _logger = logger;
    }

    public void RegisterAgent(string name, AIAgent agent)
    {
        _agents[name] = agent;
    }

    public bool HasPlannerCriticPair(string domain)
    {
        return _agents.ContainsKey($"{domain}_planner") && _agents.ContainsKey($"{domain}_critic");
    }

    public async Task<AgentResponse> ExecuteAsync(
        string domain,
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        CancellationToken cancellationToken = default)
    {
        using var span = ActivitySource.StartActivity($"planner-critic.{domain}");

        var plannerName = $"{domain}_planner";
        var criticName = $"{domain}_critic";

        if (!_agents.TryGetValue(domain, out var executor))
        {
            span?.SetStatus(ActivityStatusCode.Error, $"Executor '{domain}' not found");
            return new AgentResponse(new ChatMessage(ChatRole.Assistant,
                $"[PlannerCritic] Executor agent '{domain}' not available."));
        }

        var planner = _agents.GetValueOrDefault(plannerName);
        var critic = _agents.GetValueOrDefault(criticName);

        if (planner == null || critic == null)
        {
            _logger.LogWarning("PlannerCriticWorkflow: Incomplete pair for '{Domain}' (planner={P}, critic={C})",
                domain, planner != null, critic != null);
            return await executor.RunAsync(messages, session, null, cancellationToken);
        }

        span?.SetTag("planner-critic.domain", domain);
        span?.SetTag("planner-critic.max_rounds", MaxRevisionRounds);

        var msgList = messages.ToList();
        var userMsg = msgList.LastOrDefault(m => m.Role == ChatRole.User);
        var input = userMsg?.Text ?? "";
        string? criticFeedback = null;

        for (int round = 0; round <= MaxRevisionRounds; round++)
        {
            span?.SetTag("planner-critic.round", round);

            var planPrompt = BuildPlanPrompt(input, round, criticFeedback);
            var planMessages = new List<ChatMessage> { new(ChatRole.User, planPrompt) };

            using var planSpan = ActivitySource.StartActivity($"planner.{plannerName}.round{round}");
            var planResponse = await planner.RunAsync(planMessages, session, null, cancellationToken);
            var plan = planResponse.Text ?? "";
            planSpan?.SetTag("planner.plan_length", plan.Length);

            var execPrompt = BuildExecPrompt(input, plan);
            var execMessages = new List<ChatMessage> { new(ChatRole.User, execPrompt) };

            using var execSpan = ActivitySource.StartActivity($"executor.{domain}.round{round}");
            var result = await executor.RunAsync(execMessages, session, null, cancellationToken);
            var resultText = result.Text ?? "";
            execSpan?.SetTag("executor.result_length", resultText.Length);

            var reviewPrompt = BuildReviewPrompt(resultText, plan);
            var reviewMessages = new List<ChatMessage> { new(ChatRole.User, reviewPrompt) };

            using var criticSpan = ActivitySource.StartActivity($"critic.{criticName}.round{round}");
            var review = await critic.RunAsync(reviewMessages, session, null, cancellationToken);
            var reviewText = review.Text ?? "";
            criticSpan?.SetTag("critic.review_length", reviewText.Length);

            var verdict = ExtractVerdict(reviewText);
            criticSpan?.SetTag("critic.verdict", verdict);

            _logger.LogInformation(
                "PlannerCriticWorkflow: Round {Round} for '{Domain}' — verdict={Verdict}",
                round, domain, verdict);

            if (verdict == "PASS")
            {
                span?.SetStatus(ActivityStatusCode.Ok, $"Passed at round {round}");
                var finalText = $"{resultText}\n\n---\n## Critic Review (Round {round + 1})\n{reviewText}";
                return new AgentResponse(new ChatMessage(ChatRole.Assistant, finalText));
            }

            criticFeedback = reviewText;
            input = $"{input}\n\n[Critic Feedback Round {round + 1}]:\n{reviewText}";
        }

        span?.SetStatus(ActivityStatusCode.Ok, "MaxRevisionReached");
        _logger.LogWarning("PlannerCriticWorkflow: Max revision rounds reached for '{Domain}'", domain);

        var fallbackResult = await executor.RunAsync(messages, session, null, cancellationToken);
        var fallbackText = $"{fallbackResult.Text}\n\n---\n⚠️ **[MaxRevisionReached]** Output may require manual review. Critic did not pass after {MaxRevisionRounds} revision rounds.";
        return new AgentResponse(new ChatMessage(ChatRole.Assistant, fallbackText));
    }

    private static string BuildPlanPrompt(string input, int round, string? previousFeedback)
    {
        var prompt = $"Generate an execution plan for the following task:\n\n{input}\n\n";
        prompt += "Plan requirements:\n";
        prompt += "1. Break down into clear, actionable steps\n";
        prompt += "2. Specify tools or methods for each step\n";
        prompt += "3. Include validation checkpoints\n";
        prompt += "4. Estimate complexity for each step\n";

        if (round > 0 && previousFeedback != null)
        {
            prompt += $"\n\nPrevious plan was rejected by critic. Feedback:\n{previousFeedback}\n";
            prompt += "Address the issues above and improve the plan.";
        }

        return prompt;
    }

    private static string BuildExecPrompt(string input, string plan)
    {
        return $"Execute the following task using the provided plan:\n\n" +
               $"## Task\n{input}\n\n## Plan\n{plan}\n\n" +
               $"Provide a complete, high-quality execution result following the plan steps.";
    }

    private static string BuildReviewPrompt(string result, string plan)
    {
        return $"Review the following execution result against the plan:\n\n" +
               $"## Plan\n{plan}\n\n## Execution Result\n{result}\n\n" +
               $"Review criteria:\n" +
               $"1. Completeness: Are all plan steps addressed?\n" +
               $"2. Quality: Is the output accurate and well-structured?\n" +
               $"3. Compliance: Are standards/regulations correctly referenced?\n" +
               $"4. Factual accuracy: Are data and calculations correct?\n\n" +
               $"Output format:\n" +
               $"- VERDICT: PASS | FAIL | REVISE\n" +
               $"- ISSUES: [numbered list if any]\n" +
               $"- REQUIRED_CHANGES: [specific changes if REVISE]";
    }

    private static string ExtractVerdict(string reviewText)
    {
        var upper = reviewText.ToUpperInvariant();
        if (upper.Contains("VERDICT: PASS") || upper.Contains("VERDICT：PASS"))
            return "PASS";
        if (upper.Contains("VERDICT: FAIL") || upper.Contains("VERDICT：FAIL"))
            return "FAIL";
        if (upper.Contains("VERDICT: REVISE") || upper.Contains("VERDICT：REVISE"))
            return "REVISE";
        return "REVISE";
    }
}
