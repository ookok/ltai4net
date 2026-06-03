using System.Text.Json.Serialization;

namespace LTAI.AI;

[Flags]
public enum ModelCapability
{
    None = 0,
    Chat = 1 << 0,
    Streaming = 1 << 1,
    ToolCall = 1 << 2,
    FunctionCall = 1 << 3,
    StructuredOutput = 1 << 4,
    Vision = 1 << 5,
    Embedding = 1 << 6,
    ImageGeneration = 1 << 7,
}

public sealed record ModelMetadata(
    string Id,
    string Provider,
    int? ContextWindow,
    int? MaxOutput,
    ModelCapability Capabilities,
    decimal? PriceInPerM,
    decimal? PriceOutPerM,
    DateTime FetchedAt)
{
    [JsonIgnore]
    public bool SupportsTools => Capabilities.HasFlag(ModelCapability.ToolCall);

    [JsonIgnore]
    public bool SupportsStreaming => Capabilities.HasFlag(ModelCapability.Streaming);

    [JsonIgnore]
    public bool SupportsVision => Capabilities.HasFlag(ModelCapability.Vision);
}

public sealed record ProviderModels(
    string Name,
    string Endpoint,
    string EnvVar,
    IReadOnlyList<string> ModelIds,
    DateTime FetchedAt);

public sealed record ModelFetchResult(
    ProviderModels Provider,
    IReadOnlyList<ModelMetadata> Models);
