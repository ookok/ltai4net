using System.Diagnostics;
using LTAI.Agent.Feedback;
using LTAI.Agent.Routing;
using LTAI.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Workflows;

public enum ParliamentVerdict { Passed, Rejected, RequiresRevision, Hung }

public sealed record ParliamentVote(
    string AgentName, AgentType Intent, float Confidence,
    string? Verdict, string? Reasoning, double Weight);

public sealed record ParliamentResult(
    ParliamentVerdict Verdict,
    string FinalResponse,
    List<ParliamentVote> Votes,
    int TotalAgents, int PassedVotes, int RejectedVotes,
    double ConsensusScore,
    string Summary);

public sealed class AgentParliament
{
    private static readonly ActivitySource ActivitySource = new("LTAI.Agent.Parliament");
    private const int DefaultRequiredPassVotes = 2;
    private const int MaxRevisionRounds = 2;

    private readonly ILogger<AgentParliament> _logger;
    private readonly IntentRouter _router;
    private readonly Dictionary<string, AIAgent> _agents = new();
    private readonly ABExperimentEngine? _abEngine;

    public AgentParliament(ILogger<AgentParliament> logger, IntentRouter router, ABExperimentEngine? abEngine = null)
    {
        _logger = logger;
        _router = router;
        _abEngine = abEngine;
    }

    public void RegisterAgent(string name, AIAgent agent)
    {
        _agents[name] = agent;
    }

    public async Task<ParliamentResult> ConveneAsync(
        IEnumerable<ChatMessage> messages,
        string? overrideVoterAgents = null,
        string? criticAgent = null,
        int requiredPassVotes = DefaultRequiredPassVotes,
        AgentSession? session = null,
        CancellationToken cancellationToken = default)
    {
        using var span = ActivitySource.StartActivity("parliament.convene");

        var msgList = messages.ToList();
        var userMsg = msgList.LastOrDefault(m => m.Role == ChatRole.User);
        if (userMsg?.Text is null)
            return HungResult("No user message");

        var voterNames = (overrideVoterAgents ?? "chat,code,reasoning").Split(',', StringSplitOptions.TrimEntries);
        var voters = voterNames
            .Where(n => _agents.ContainsKey(n))
            .Select(n => (name: n, agent: _agents[n]))
            .ToList();

        if (voters.Count < 2)
            return HungResult($"Need at least 2 voters, found {voters.Count}");

        span?.SetTag("parliament.voters", voters.Count);
        span?.SetTag("parliament.query", userMsg.Text[..Math.Min(userMsg.Text.Length, 200)]);

        _logger.LogInformation("AgentParliament: Convening with {Count} voters on query: {Query}",
            voters.Count, userMsg.Text[..Math.Min(userMsg.Text.Length, 100)]);

        // Round 1: Collect votes
        var votes = new List<ParliamentVote>();
        var tasks = voters.Select(async v =>
        {
            using var voterSpan = ActivitySource.StartActivity($"parliament.vote.{v.name}");
            var route = _router.Classify(userMsg.Text);
            var response = await v.agent.RunAsync(messages, session, null, cancellationToken).ConfigureAwait(false);
            var vote = ExtractVote(v.name, route.Intent, route.Confidence, response.Text ?? "");
            voterSpan?.SetTag("parliament.vote.verdict", vote.Verdict);
            return vote;
        });

        var allVotes = await Task.WhenAll(tasks).ConfigureAwait(false);
        votes.AddRange(allVotes);

        // Evaluate
        var passCount = votes.Count(v => v.Verdict == "PASS");
        var rejectCount = votes.Count(v => v.Verdict == "REJECT");
        var consensusScore = (double)passCount / votes.Count;

        _logger.LogInformation("AgentParliament: Round 1 — pass={Pass} reject={Reject} consensus={Consensus:F2}",
            passCount, rejectCount, consensusScore);

        // Round 2 with critic if hung or borderline
        var finalResponse = "";
        if (passCount >= requiredPassVotes && rejectCount == 0)
        {
            finalResponse = BuildConsensusResponse(votes, ParliamentVerdict.Passed);
        }
        else if (rejectCount >= requiredPassVotes)
        {
            finalResponse = BuildConsensusResponse(votes, ParliamentVerdict.Rejected);
        }
        else if (criticAgent != null && _agents.TryGetValue(criticAgent, out var critic))
        {
            _logger.LogInformation("AgentParliament: Hung vote, invoking critic '{Critic}'", criticAgent);
            using var criticSpan = ActivitySource.StartActivity("parliament.critic");

            var criticInput = $"Review these agent responses and decide:\n\n" +
                votes.Select((v, i) => $"Agent {i + 1} ({v.AgentName}): {v.Verdict}\n{v.Reasoning}").Aggregate((a, b) => $"{a}\n\n{b}");

            var criticResponse = await critic.RunAsync(
                [new ChatMessage(ChatRole.User, criticInput)], session, null, cancellationToken).ConfigureAwait(false);

            var criticVote = ExtractVote(criticAgent, AgentType.Custom, 0.9f, criticResponse.Text ?? "");
            votes.Add(criticVote);

            var finalVerdict = criticVote.Verdict == "PASS" ? ParliamentVerdict.Passed : ParliamentVerdict.RequiresRevision;
            finalResponse = BuildConsensusResponse(votes, finalVerdict);
        }
        else
        {
            finalResponse = BuildConsensusResponse(votes, ParliamentVerdict.Hung);
        }

        span?.SetStatus(ActivityStatusCode.Ok);

        return new ParliamentResult(
            Verdict: passCount >= requiredPassVotes ? ParliamentVerdict.Passed
                : rejectCount >= requiredPassVotes ? ParliamentVerdict.Rejected
                : criticAgent != null ? ParliamentVerdict.RequiresRevision : ParliamentVerdict.Hung,
            FinalResponse: finalResponse,
            Votes: votes,
            TotalAgents: votes.Count,
            PassedVotes: passCount,
            RejectedVotes: rejectCount,
            ConsensusScore: consensusScore,
            Summary: $"Parliament: {passCount}/{votes.Count} passed (consensus={consensusScore:F2})"
        );
    }

    private static ParliamentVote ExtractVote(string agentName, AgentType intent, float confidence, string response)
    {
        var upper = response.ToUpperInvariant();
        var verdict = "PASS";
        if (upper.Contains("VERDICT: REJECT") || upper.Contains("VERDICT：REJECT")) verdict = "REJECT";
        else if (upper.Contains("VERDICT: REVISE") || upper.Contains("VERDICT：REVISE")) verdict = "REVISE";
        else if (upper.Contains("VERDICT: PASS") || upper.Contains("VERDICT：PASS")) verdict = "PASS";

        var reasoning = response.Length > 500 ? response[..500] + "..." : response;
        var weight = confidence >= 0.7 ? 2.0 : confidence >= 0.4 ? 1.0 : 0.5;

        return new ParliamentVote(agentName, intent, confidence, verdict, reasoning, weight);
    }

    private static string BuildConsensusResponse(List<ParliamentVote> votes, ParliamentVerdict verdict)
    {
        var sb = new System.Text.StringBuilder();

        var verdictEmoji = verdict switch
        {
            ParliamentVerdict.Passed => "✅ PASSED",
            ParliamentVerdict.Rejected => "❌ REJECTED",
            ParliamentVerdict.RequiresRevision => "⚠️ REQUIRES REVISION",
            ParliamentVerdict.Hung => "⚖️ HUNG (no consensus)",
            _ => "?"
        };

        sb.AppendLine($"## 🏛️ Agent Parliament — Verdict: {verdictEmoji}");
        sb.AppendLine();

        foreach (var vote in votes)
        {
            sb.AppendLine($"### {vote.AgentName} (confidence: {vote.Confidence:F2}, weight: {vote.Weight})");
            sb.AppendLine($"- Verdict: **{vote.Verdict}**");
            sb.AppendLine($"- Reasoning: {vote.Reasoning?[..Math.Min(vote.Reasoning.Length, 300)]}");
            sb.AppendLine();
        }

        var passCount = votes.Count(v => v.Verdict == "PASS");
        sb.AppendLine($"---");
        sb.AppendLine($"**Result**: {passCount}/{votes.Count} agents passed | consensus score: {(double)passCount / votes.Count:F2}");

        return sb.ToString();
    }

    private static ParliamentResult HungResult(string reason)
    {
        return new ParliamentResult(
            ParliamentVerdict.Hung, reason,
            new List<ParliamentVote>(), 0, 0, 0, 0, reason);
    }
}
