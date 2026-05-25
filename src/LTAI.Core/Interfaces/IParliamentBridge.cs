namespace LTAI.Core.Interfaces;

/// <summary>
/// Cross-assembly bridge for multi-agent parliament deliberation.
/// SentientParliament (in LTAI.Agent) implements this interface,
/// LivingTreeSystem (in LTAI.AI) calls it for high-stakes verification.
/// </summary>
public interface IParliamentBridge
{
    bool IsAvailable { get; }
    Task<ParliamentVerdict> DeliberateAsync(string query, string response, CancellationToken ct = default);
}

public sealed record ParliamentVerdict
{
    public bool IsConsensus { get; init; }
    public double AvgConfidence { get; init; }
    public string? RevisionNotes { get; init; }
    public int VoterCount { get; init; }
}
