namespace LTAI.Agent.Resilience;

public sealed record DebugLoopConfig
{
    public int MaxSourceLines { get; init; } = 400;
    public int ContextPadding { get; init; } = 30;
    public int MaxAttempts { get; init; } = 3;
    public int TimeoutMs { get; init; } = 120000;
}
