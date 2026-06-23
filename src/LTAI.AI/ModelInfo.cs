using System.Text.Json.Serialization;

namespace LTAI.AI;

// ────────────────────────────────────────────────────────────────
//  Unified provider/model types — sourced from models.dev api.json
//  Replaces: KnownKeys.KeyInfo, KnownCapabilities, UsageTracker.PerModelPricing, UsageTracker.KnownContextWindows
// ────────────────────────────────────────────────────────────────

/// <summary>
/// API format hint derived from models.dev <c>npm</c> field.
/// </summary>
public enum ApiFormat
{
    Unknown = 0,
    OpenAICompatible = 1,    // "@ai-sdk/openai-compatible" → OpenAIChatClientFactory
    Anthropic = 2,           // "@ai-sdk/anthropic" → AnthropicChatClientFactory
}

/// <summary>
/// Estimated latency tier for speed scoring.
/// </summary>
public enum LatencyTier
{
    Unknown = 0,
    Fast = 1,     // flash / mini / lite / turbo
    Medium = 2,   // default / balanced
    Slow = 3,     // pro / max / preview / reasoning
}

/// <summary>
/// A single benchmark result from models.dev.
/// </summary>
/// <param name="Name">Benchmark name, e.g. "Aider Polyglot".</param>
/// <param name="Score">Numeric score.</param>
/// <param name="Metric">Score unit, e.g. "percent correct".</param>
/// <param name="Source">Source URL.</param>
/// <param name="Date">Benchmark date.</param>
public sealed record ModelBenchmark(
    string Name,
    double Score,
    string Metric,
    string Source,
    string? Date);

/// <summary>
/// Full model metadata sourced from models.dev.
/// </summary>
/// <param name="Id">Canonical model ID, e.g. "deepseek/deepseek-chat".</param>
/// <param name="Name">Display name, e.g. "DeepSeek Chat".</param>
/// <param name="Family">Model family, e.g. "deepseek", "deepseek-thinking".</param>
/// <param name="ToolCall">Supports function/tool calling.</param>
/// <param name="Reasoning">Supports deep reasoning / chain-of-thought.</param>
/// <param name="StructuredOutput">Supports JSON mode / structured output.</param>
/// <param name="Attachment">Supports file attachment.</param>
/// <param name="Temperature">Supports temperature parameter.</param>
/// <param name="InputModalities">Supported input types.</param>
/// <param name="OutputModalities">Supported output types.</param>
/// <param name="ContextWindow">Max context window in tokens.</param>
/// <param name="MaxOutput">Max output tokens.</param>
/// <param name="PriceInPerM">Input price in USD per 1M tokens (or 0 if unknown).</param>
/// <param name="PriceOutPerM">Output price in USD per 1M tokens (or 0 if unknown).</param>
/// <param name="KnowledgeCutoff">Knowledge cutoff date.</param>
/// <param name="ReleaseDate">Model release date.</param>
/// <param name="Benchmarks">Coding / reasoning benchmarks.</param>
/// <param name="OpenWeights">Whether model weights are publicly available.</param>
public sealed record ModelInfo(
    string Id,
    string Name,
    string Family,
    bool ToolCall,
    bool Reasoning,
    bool StructuredOutput,
    bool Attachment,
    bool Temperature,
    string[] InputModalities,
    string[] OutputModalities,
    int ContextWindow,
    int MaxOutput,
    decimal PriceInPerM,
    decimal PriceOutPerM,
    string? KnowledgeCutoff,
    string? ReleaseDate,
    ModelBenchmark[]? Benchmarks,
    bool OpenWeights)
{
    /// <summary>Short model ID without provider prefix, e.g. "deepseek-chat".</summary>
    [JsonIgnore]
    public string ShortId => Id.Contains('/') ? Id[(Id.LastIndexOf('/') + 1)..] : Id;

    /// <summary>Provider ID part, e.g. "deepseek".</summary>
    [JsonIgnore]
    public string ProviderId => Id.Contains('/') ? Id[..Id.IndexOf('/')] : "";

    [JsonIgnore]
    public bool SupportsVision => InputModalities.Any(m =>
        string.Equals(m, "image", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Heuristic latency tier based on model family and context window.
    /// </summary>
    [JsonIgnore]
    public LatencyTier EstimatedLatency => Family switch
    {
        var f when f.Contains("flash", StringComparison.OrdinalIgnoreCase) => LatencyTier.Fast,
        var f when f.Contains("mini", StringComparison.OrdinalIgnoreCase) => LatencyTier.Fast,
        var f when f.Contains("lite", StringComparison.OrdinalIgnoreCase) => LatencyTier.Fast,
        var f when f.Contains("turbo", StringComparison.OrdinalIgnoreCase) => LatencyTier.Fast,
        var f when f.Contains("haiku", StringComparison.OrdinalIgnoreCase) => LatencyTier.Fast,
        var f when f.Contains("pro", StringComparison.OrdinalIgnoreCase) => LatencyTier.Slow,
        var f when f.Contains("max", StringComparison.OrdinalIgnoreCase) => LatencyTier.Slow,
        var f when f.Contains("preview", StringComparison.OrdinalIgnoreCase) => LatencyTier.Slow,
        var f when f.Contains("thinking", StringComparison.OrdinalIgnoreCase) => LatencyTier.Slow,
        _ => ContextWindow > 128000 ? LatencyTier.Slow
             : ContextWindow > 64000 ? LatencyTier.Medium
             : LatencyTier.Fast,
    };

    /// <summary>
    /// Convert to the legacy <see cref="ModelMetadata"/> format for existing consumers.
    /// </summary>
    public ModelMetadata ToLegacy(string providerName)
    {
        var caps = ModelCapability.Chat | ModelCapability.Streaming;
        if (ToolCall) caps |= ModelCapability.ToolCall;
        if (StructuredOutput) caps |= ModelCapability.StructuredOutput;
        if (SupportsVision) caps |= ModelCapability.Vision;

        return new ModelMetadata(
            ShortId,
            providerName,
            ContextWindow,
            MaxOutput,
            caps,
            (decimal?)PriceInPerM,
            (decimal?)PriceOutPerM,
            null,                         // no cache pricing from models.dev
            DateTime.MinValue);           // hardcoded source marker
    }
}

/// <summary>
/// Full provider metadata sourced from models.dev, merged with LTAI local supplements.
/// </summary>
/// <param name="Id">Provider ID, e.g. "deepseek".</param>
/// <param name="Name">Display name, e.g. "DeepSeek".</param>
/// <param name="EnvVars">API key environment variables (from models.dev <c>env</c> field).</param>
/// <param name="Endpoint">API base URL (from models.dev <c>api</c> field).</param>
/// <param name="ApiFormat">Client format derived from models.dev <c>npm</c> field.</param>
/// <param name="DocUrl">Documentation URL.</param>
/// <param name="KeyUrl">API key management URL (LTAI local supplement).</param>
/// <param name="Models">All models offered by this provider.</param>
/// <param name="FetchedAt">When this data was last fetched from models.dev.</param>
public sealed record ProviderInfo(
    string Id,
    string Name,
    string[] EnvVars,
    string? Endpoint,
    ApiFormat ApiFormat,
    string? DocUrl,
    string? KeyUrl,
    ModelInfo[] Models,
    DateTime FetchedAt)
{
    /// <summary>Primary env var name (first in the array).</summary>
    [JsonIgnore]
    public string EnvVar => EnvVars.Length > 0 ? EnvVars[0] : "";

    /// <summary>Whether this provider has a usable LLM endpoint, format, and at least one text-generation model.</summary>
    [JsonIgnore]
    public bool IsLlmProvider =>
        Endpoint != null
        && ApiFormat != ApiFormat.Unknown
        && Models.Any(m => m.ContextWindow > 0);

    /// <summary>Number of models offered by this provider.</summary>
    [JsonIgnore]
    public int ModelCount => Models.Length;

    /// <summary>Finds a model by its short ID (case-insensitive).</summary>
    public ModelInfo? FindModel(string shortId) =>
        Models.FirstOrDefault(m => string.Equals(m.ShortId, shortId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Finds a model by its full canonical ID (case-insensitive).</summary>
    public ModelInfo? FindModelById(string fullId) =>
        Models.FirstOrDefault(m => string.Equals(m.Id, fullId, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Auto-selection result for the current provider.
/// </summary>
/// <param name="Provider">Provider ID.</param>
/// <param name="L1">L1 model short ID.</param>
/// <param name="L1Alt">L1 alternate model short ID (optional).</param>
/// <param name="L2">L2 model short ID.</param>
/// <param name="L2Alt">L2 alternate model short ID (optional).</param>
/// <param name="L3">L3 model short ID (null = reuse L1).</param>
/// <param name="SelectedAt">When this selection was made.</param>
public sealed record AutoSelectResult(
    string Provider,
    string L1,
    string? L1Alt,
    string L2,
    string? L2Alt,
    string? L3,
    DateTime SelectedAt)
{
    /// <summary>Effective L3 model ID (falls back to L1 when L3 is null).</summary>
    [JsonIgnore]
    public string EffectiveL3 => L3 ?? L1;
}
