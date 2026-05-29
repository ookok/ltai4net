namespace LTAI.Core.Interfaces;

public sealed record LLMChatOptions
{
    public string? Model { get; init; }
    public float Temperature { get; init; } = 0.3f;
    public int MaxTokens { get; init; } = 4096;
    public int TimeoutMs { get; init; } = 60000;
    public bool EnableCoach { get; init; }
    public bool EnableOnto { get; init; }
    public Dictionary<string, object?>? ExtraParams { get; init; }

    /// <summary>
    /// Structured output schema as serialized JSON (response_format).
    /// When set, the provider will constrain its output to match this schema.
    /// Set via <c>options.WithStructuredOutput(schema)</c> from LTAI.AI or
    /// directly as <c>options with { StructuredSchemaJson = json }</c>.
    /// </summary>
    public string? StructuredSchemaJson { get; init; }
}
