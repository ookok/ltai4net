namespace LTAI.Core.Interfaces;

/// <summary>
/// ProAct: idle-time anticipation cache bridge.
/// Implemented by ProActAnticipator in LTAI.Agent.
/// </summary>
public interface IProActCache
{
    /// <summary>Check if query matches a pre-computed anticipation.</summary>
    ProActCacheResult? TryMatch(string userQuery);

    /// <summary>Record this interaction for future anticipation cycles.</summary>
    void RecordInteraction(string userQuery, string? agentResponse = null);

    /// <summary>Statistics for observability.</summary>
    ProActCacheStats GetStats();
}

public sealed record ProActCacheResult
{
    public string? PreComputedResponse { get; init; }
    public float Confidence { get; init; }
    public string? PreRetrievedContext { get; init; }
}

public sealed record ProActCacheStats
{
    public int TotalPredictions { get; init; }
    public int TotalHits { get; init; }
    public int TotalMisses { get; init; }
    public double HitRate { get; init; }
    public int ActiveAnticipations { get; init; }
}
