using Microsoft.Extensions.Logging;

namespace LTAI.AI;

/// <summary>
/// Scores and ranks models against tier requirements.
/// Uses capability, pricing, speed, and availability to produce a 0–1 score.
/// </summary>
public sealed class ModelScoringEngine
{
    private readonly ILogger<ModelScoringEngine> _logger;

    public ModelScoringEngine(ILogger<ModelScoringEngine> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Scores a single model against tier requirements. Returns 0 if the model
    /// does not meet the minimum requirements (hard filter).
    /// </summary>
    public double Score(ModelInfo model, ModelTierRequirements req)
    {
        if (!MeetsRequirements(model, req))
            return 0;

        var w = req.Weights;
        return w.Capability * CapabilityScore(model, req)
             + w.Cost       * CostScore(model)
             + w.Speed      * SpeedScore(model, req)
             + w.Availability * 1.0; // default full score; adjusted at runtime via ProviderStats
    }

    /// <summary>
    /// Selects the best model and an alternate from a list, scored against
    /// the given tier requirements. Returns (primary, alt) — alt is null when
    /// only one model qualifies, or both null when none qualify.
    /// </summary>
    public (ModelInfo? Primary, ModelInfo? Alt) SelectBestPair(
        IEnumerable<ModelInfo> models,
        ModelTierRequirements req)
    {
        var ranked = models
            .Select(m => (Model: m, Score: Score(m, req)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ToList();

        if (ranked.Count == 0)
        {
            _logger.LogWarning("No models meet requirements for {Tier}", req.TierName);
            return (null, null);
        }

        var primary = ranked[0].Model;
        var alt = ranked.Count > 1 ? ranked[1].Model : null;

        _logger.LogInformation("{Tier}: primary={Primary}({PScore:F3}), alt={Alt}({AScore:F3})",
            req.TierName,
            primary.ShortId, ranked[0].Score,
            alt?.ShortId ?? "none", ranked.Count > 1 ? ranked[1].Score : 0);

        return (primary, alt);
    }

    // ── Scoring sub-functions ────────────────────────────────────

    private bool MeetsRequirements(ModelInfo model, ModelTierRequirements req)
    {
        if (req.MustHaveStreaming && !model.Temperature)
            return false; // streaming capability inferred from temperature support
        if (req.MustHaveToolCall && !model.ToolCall)
            return false;
        if (model.ContextWindow < req.MinContextWindow)
            return false;
        if ((int)model.EstimatedLatency > (int)req.MaxLatencyTier)
            return false;
        return true;
    }

    private static double CapabilityScore(ModelInfo model, ModelTierRequirements req)
    {
        double score = 0.6; // base score for meeting minimum requirements

        if (req.PreferStructuredOutput && model.StructuredOutput) score += 0.2;
        if (req.PreferVision && model.SupportsVision) score += 0.1;
        if (req.PreferReasoning && model.Reasoning) score += 0.1;
        if (model.ToolCall) score += 0.05;    // minor universal boost
        if (model.ContextWindow >= 128_000) score += 0.05;

        return Math.Min(1.0, score);
    }

    private static double CostScore(ModelInfo model)
    {
        var totalCost = model.PriceInPerM + model.PriceOutPerM;
        if (totalCost <= 0) return 0.5; // unknown pricing → neutral
        // Logarithmic scale: cheap models → ~1.0, expensive → ~0.1
        return 1.0 / (1.0 + Math.Log10(Math.Max(1.0, (double)totalCost)));
    }

    private static double SpeedScore(ModelInfo model, ModelTierRequirements req)
    {
        var baseScore = model.EstimatedLatency switch
        {
            LatencyTier.Fast => 1.0,
            LatencyTier.Medium => 0.7,
            LatencyTier.Slow => 0.4,
            _ => 0.5,
        };
        // Context window penalty: larger windows suggest slower inference
        if (model.ContextWindow > 200_000) baseScore *= 0.8;
        // Reasoning models are inherently slower
        if (model.Reasoning) baseScore *= 0.9;
        return baseScore;
    }
}
