namespace LTAI.Core.Configuration;

public sealed class AgentManagementConfig
{
    public int WipLimit { get; init; } = 4;
    public int WipLimitPro { get; init; } = 2;
    public int RetrospectiveMaxAgeDays { get; init; } = 30;
    public int RetrospectiveStoreLimit { get; init; } = 500;
    public int KanbanMaxSpans { get; init; } = 200;
    public double ContextTargetRatio { get; init; } = 0.65;
    public double CompressionUrgencyThreshold { get; init; } = 0.8;
}
