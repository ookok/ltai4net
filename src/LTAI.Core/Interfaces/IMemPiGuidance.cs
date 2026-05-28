namespace LTAI.Core.Interfaces;

/// <summary>
/// Mem-π: Adaptive Memory through Learning When and What to Generate.
/// Cross-assembly interface — implemented by MemPiGuidanceEngine in LTAI.AI.
/// </summary>
public interface IMemPiGuidance
{
    bool IsAvailable { get; }
    Task<MemPiBridgeResult> GenerateGuidanceAsync(string sessionContext, string query, CancellationToken ct = default);
    bool ShouldAttemptGuidance(string sessionContext);
}

/// <summary>Bridge result for cross-assembly Mem-π calls.</summary>
public sealed record MemPiBridgeResult
{
    public bool Generated { get; init; }
    public string? Guidance { get; init; }
    public float Confidence { get; init; }
    public long LatencyMs { get; init; }
    public string ModelName { get; init; } = "";
    public string? AbstainReason { get; init; }
}
