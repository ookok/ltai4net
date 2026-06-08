namespace LTAI.AI;

/// <summary>
/// Hardcoded capability database for known models.
/// Used as fallback when /v1/models API doesn't return capabilities.
/// Merged with dynamic API data at runtime by <see cref="ModelMetadataProvider"/>.
/// </summary>
internal static class KnownCapabilities
{
    public static readonly Dictionary<string, (int? ContextWindow, int? MaxOutput, ModelCapability Caps)> All = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-model pricing overrides (¥/1M tokens). Falls back to provider-level KeyInfo pricing.</summary>
    public static readonly Dictionary<string, (decimal PriceIn, decimal PriceOut, decimal PriceInCache)> PerModelPricing = new(StringComparer.OrdinalIgnoreCase);
}
