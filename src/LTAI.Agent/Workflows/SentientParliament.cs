using System.Diagnostics;
using LTAI.Agent.Agents;
using LTAI.Agent.Routing;
using LTAI.Core.Observability;
using LTAI.Knowledge.Memory;
using LTAI.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Workflows;

public sealed class SentientParliament
{
    private readonly ILogger<SentientParliament> _logger;
    private readonly UnifiedSemanticRouter _router;
    private readonly Dictionary<string, BaseAgent> _agents = new();
    private const double ConfidenceThreshold = 0.9;
    private const int MaxRevisionRounds = 2;

    public bool EnableExternalGrounding { get; set; }
    public Func<string, CancellationToken, Task<string>>? GroundingCallback { get; set; }

    public SentientParliament(ILogger<SentientParliament> logger, UnifiedSemanticRouter router)
    {
        _logger = logger;
        _router = router;
    }

    public void RegisterAgent(string name, BaseAgent agent)
    {
        _agents[name] = agent;
    }

    public async Task<ParliamentResult> DeliberateAsync(
        string query, IEnumerable<ChatMessage> history, AgentSession? session, CancellationToken ct)
    {
        using var activity = LtaiActivitySource.Workflow.StartActivity("parliament.deliberate");
        activity?.SetTag("parliament.query_length", query.Length);

        var context = new AgentContext(query, history.ToList(), session);
        int round = 0;

        return await DeliberateCoreAsync(query, context, session, round, ct);
    }

    private async Task<ParliamentResult> DeliberateCoreAsync(
        string query, AgentContext context, AgentSession? session, int round, CancellationToken ct)
    {
        using var activity = LtaiActivitySource.Workflow.StartActivity("parliament.round");
        activity?.SetTag("parliament.round", round);

        if (round >= MaxRevisionRounds)
        {
            _logger.LogWarning("Parliament: max revision rounds ({Max}) reached", MaxRevisionRounds);
            return new ParliamentResult(ParliamentVerdict.RequiresRevision, "Max rounds reached",
                new(), 0, 0, 0, 0, "Unable to reach consensus after revisions.");
        }

        // Phase 1: Primary Agent
        AgentResponse primary;
        using (var span = LtaiActivitySource.Agent.StartActivity("parliament.primary"))
        {
            span?.SetTag("parliament.phase", "generation");
            primary = await ExecutePrimaryAsync(query, context, session, ct);
            span?.SetTag("parliament.primary_length", primary.Text?.Length ?? 0);
        }

        // Phase 2: Critic Agent
        using var criticSpan = LtaiActivitySource.Agent.StartActivity("parliament.critic");
        criticSpan?.SetTag("parliament.phase", "critique");
        var critic = await ExecuteCriticAsync(query, primary, context, session, ct);

        // Phase 3: Oracle Agent
        string oracleFacts;
        double oracleConfidence;
        using (var oracleSpan = LtaiActivitySource.Agent.StartActivity("parliament.oracle"))
        {
            oracleSpan?.SetTag("parliament.phase", "fact_check");
            var oracle = await ExecuteOracleAsync(primary, ct);
            oracleFacts = oracle.Facts;
            oracleConfidence = oracle.Confidence;
            oracleSpan?.SetTag("parliament.oracle_confidence", oracleConfidence);

            // ExternalGrounding: 置信度极低时受控搜索
            if (oracleConfidence < 0.5 && EnableExternalGrounding && GroundingCallback != null)
            {
                _logger.LogWarning("Parliament: Oracle confidence {Conf:F2} < 0.5, invoking ExternalGrounding", oracleConfidence);
                var grounded = await GroundingCallback(primary.Text ?? "", ct);
                oracleFacts = $"{oracleFacts}\n[ExternalGrounding]: {grounded}";
                oracleConfidence = Math.Max(oracleConfidence, 0.6);
            }
        }

        // Phase 4: Voting
        var votes = new List<ParliamentVote>
        {
            new("primary", AgentType.Custom, 0.85f, "accept", primary.Text ?? "", 1.0),
            new("critic", AgentType.EiaCritic, (float)critic.Confidence,
                critic.Issues.Count == 0 ? "accept" : "reject",
                critic.Summary, 0.8),
            new("oracle", AgentType.Reasoning, (float)oracleConfidence,
                oracleFacts.Contains("issue") || oracleFacts.Contains("error") ? "reject" : "accept",
                oracleFacts, 1.2)
        };

        var consensusScore = votes.Average(v => v.Confidence * v.Weight);
        var passedVotes = votes.Count(v => v.Verdict == "accept");
        var rejectedVotes = votes.Count(v => v.Verdict == "reject");

        activity?.SetTag("parliament.consensus", consensusScore);
        activity?.SetTag("parliament.passed", passedVotes);
        activity?.SetTag("parliament.rejected", rejectedVotes);

        if (passedVotes < 2 || consensusScore < ConfidenceThreshold)
        {
            _logger.LogWarning("Parliament: consensus {Score:F2} < {Threshold}, round {Round}/{Max}",
                consensusScore, ConfidenceThreshold, round + 1, MaxRevisionRounds);

            var revisedQuery = $"{query}\n\n[Critic feedback]: {critic.Summary}\n[Oracle facts]: {oracleFacts}";
            var revisedContext = new AgentContext(revisedQuery, context.FullHistory, session);
            return await DeliberateCoreAsync(revisedQuery, revisedContext, session, round + 1, ct);
        }

        _logger.LogInformation("Parliament: PASSED with {Score:P0} consensus, {Passed}/{Total} votes",
            consensusScore, passedVotes, votes.Count);

        return new ParliamentResult(ParliamentVerdict.Passed, primary.Text ?? "",
            votes, votes.Count, passedVotes, rejectedVotes, consensusScore,
            $"Passed: {consensusScore:P0} consensus ({passedVotes}/{votes.Count} votes)");
    }

    private async Task<AgentResponse> ExecutePrimaryAsync(
        string query, AgentContext context, AgentSession? session, CancellationToken ct)
    {
        if (_agents.TryGetValue("eia", out var eia))
            return await eia.RunAsync(new[] { new ChatMessage(ChatRole.User, query) }, session, null, ct);
        if (_agents.TryGetValue("code", out var code))
            return await code.RunAsync(new[] { new ChatMessage(ChatRole.User, query) }, session, null, ct);
        return new(new ChatMessage(ChatRole.Assistant, "No primary agent available"));
    }

    private async Task<CriticResult> ExecuteCriticAsync(
        string query, AgentResponse primary, AgentContext context, AgentSession? session, CancellationToken ct)
    {
        if (_agents.TryGetValue("eia_critic", out var critic))
        {
            var review = await critic.RunAsync(
                [new(ChatRole.User, $"Review for errors, bias, compliance:\n\nQuery: {query}\n\nOutput:\n{primary.Text}")],
                session, null, ct);
            var hasIssues = review.Text?.Contains("issue", StringComparison.OrdinalIgnoreCase) == true ||
                           review.Text?.Contains("error", StringComparison.OrdinalIgnoreCase) == true;
            return new CriticResult(hasIssues ? 0.5 : 0.9, review.Text ?? "", new());
        }
        return new CriticResult(0.8, "No critic available", new());
    }

    private async Task<OracleResult> ExecuteOracleAsync(AgentResponse primary, CancellationToken ct)
    {
        if (_agents.TryGetValue("chat", out var oracle))
        {
            var facts = await oracle.RunAsync(
                [new(ChatRole.User, $"Fact-check this output. List verifiable facts and any factual errors:\n\n{primary.Text}")],
                null, null, ct);
            bool hasIssues = facts.Text?.Contains("error", StringComparison.OrdinalIgnoreCase) == true ||
                            facts.Text?.Contains("incorrect", StringComparison.OrdinalIgnoreCase) == true;
            return new OracleResult(hasIssues ? 0.3 : 0.85, facts.Text ?? "");
        }
        return new OracleResult(0.5, "No oracle available");
    }

    private sealed record CriticResult(double Confidence, string Summary, List<string> Issues);
    private sealed record OracleResult(double Confidence, string Facts);
}
