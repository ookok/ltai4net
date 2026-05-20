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
}
