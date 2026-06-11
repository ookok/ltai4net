namespace LTAI.AI;

/// <summary>
/// Defines the requirements for each model tier in the auto-selection system.
/// </summary>
/// <param name="TierName">Human-readable tier name (L1, L2, L3).</param>
/// <param name="MustHaveToolCall">Model must support tool calling.</param>
/// <param name="MustHaveStreaming">Model must support streaming.</param>
/// <param name="PreferStructuredOutput">Prefer models with structured output support.</param>
/// <param name="PreferVision">Prefer models with vision support.</param>
/// <param name="PreferReasoning">Prefer models with deep reasoning capability.</param>
/// <param name="MinContextWindow">Minimum context window in tokens.</param>
/// <param name="MaxLatencyTier">Maximum acceptable latency tier (inclusive).</param>
/// <param name="Weights">Scoring weights for this tier.</param>
public sealed record ModelTierRequirements(
    string TierName,
    bool MustHaveToolCall,
    bool MustHaveStreaming,
    bool PreferStructuredOutput,
    bool PreferVision,
    bool PreferReasoning,
    int MinContextWindow,
    LatencyTier MaxLatencyTier,
    ScoringWeights Weights)
{
    // ── Presets ──────────────────────────────────────────────────

    /// <summary>L1 — fast model for routine tasks. Emphasizes speed and cost.</summary>
    public static ModelTierRequirements L1 => new(
        TierName: "L1",
        MustHaveToolCall: false,       // preferred, not required
        MustHaveStreaming: true,
        PreferStructuredOutput: false,
        PreferVision: false,
        PreferReasoning: false,
        MinContextWindow: 32_000,
        MaxLatencyTier: LatencyTier.Medium,
        Weights: new(Capability: 0.25, Cost: 0.30, Speed: 0.35, Availability: 0.10));

    /// <summary>L2 — deep reasoning model. Emphasizes capability and context.</summary>
    public static ModelTierRequirements L2 => new(
        TierName: "L2",
        MustHaveToolCall: true,
        MustHaveStreaming: true,
        PreferStructuredOutput: true,
        PreferVision: false,
        PreferReasoning: true,
        MinContextWindow: 64_000,
        MaxLatencyTier: LatencyTier.Slow,
        Weights: new(Capability: 0.50, Cost: 0.15, Speed: 0.15, Availability: 0.15));

    /// <summary>L3 — lightweight judge/steer model. Emphasizes cost and speed.</summary>
    public static ModelTierRequirements L3 => new(
        TierName: "L3",
        MustHaveToolCall: false,
        MustHaveStreaming: false,
        PreferStructuredOutput: false,
        PreferVision: false,
        PreferReasoning: false,
        MinContextWindow: 8_000,
        MaxLatencyTier: LatencyTier.Fast,
        Weights: new(Capability: 0.10, Cost: 0.40, Speed: 0.30, Availability: 0.20));
}

/// <summary>
/// Scoring weights for model evaluation. All values should sum to approximately 1.0.
/// </summary>
public readonly record struct ScoringWeights(
    double Capability,
    double Cost,
    double Speed,
    double Availability)
{
    public static ScoringWeights Default => new(0.40, 0.30, 0.20, 0.10);
}
