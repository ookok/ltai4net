namespace LTAI.Core.Governors;

public sealed record ParetoRouterConfig
{
    public int EmbeddingDim { get; init; } = 768;
    public ParetoDistanceMetric Metric { get; init; } = ParetoDistanceMetric.Cosine;
    public int RouteHistorySize { get; init; } = 32;
    public float JitterThreshold { get; init; } = 0.40f;
    public int LockDuration { get; init; } = 20;
    public float ShadowRate { get; init; } = 0.10f;
    public int MaxShadowLogSize { get; init; } = 100;
}
