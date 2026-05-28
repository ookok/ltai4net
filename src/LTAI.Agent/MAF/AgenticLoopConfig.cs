namespace LTAI.Agent.MAF;

public sealed record AgenticLoopConfig
{
    public int MaxIterations { get; init; } = 20;
    public int DebugLoopTriggerThreshold { get; init; } = 3;
}
