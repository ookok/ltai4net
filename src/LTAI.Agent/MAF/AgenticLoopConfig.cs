namespace LTAI.Agent.MAF;

public sealed record AgenticLoopConfig
{
    public int MaxIterations { get; init; } = 20;
    public int DebugLoopTriggerThreshold { get; init; } = 3;
    /// <summary>Hard global timeout per task. Loop throws TimeoutException when exceeded.</summary>
    public TimeSpan MaxTotalDuration { get; init; } = TimeSpan.FromMinutes(5);
}
