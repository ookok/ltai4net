namespace LTAI.Core.Interfaces;

/// <summary>
/// Cross-assembly bridge for multi-agent parliament deliberation.
/// SentientParliament (in LTAI.Agent) implements this interface,
/// LivingTreeSystem (in LTAI.AI) calls it for high-stakes verification.
/// </summary>
/// <summary>
/// Multi-agent deliberation bridge — a "parliament" of agents votes on
/// the quality/consensus of a response before it's returned to the user.
/// Implemented by SentientParliament in LTAI.Agent.
/// Callers: LTAI.Agent.Workflows.SentientParliament, LTAI.AI.Governors.LivingTreeSystem.
/// </summary>
public interface IParliamentBridge
{
    bool IsAvailable { get; }
    Task<ParliamentVerdict> DeliberateAsync(string query, string response, CancellationToken ct = default);
}

/// <summary>
/// Result of a parliamentary deliberation — consensus level, average confidence,
/// revision notes, and total voter count.
/// </summary>
public sealed record ParliamentVerdict
{
    public bool IsConsensus { get; init; }
    public double AvgConfidence { get; init; }
    public string? RevisionNotes { get; init; }
    public int VoterCount { get; init; }
}
