namespace LTAI.Core.Configuration;

public sealed class WebConfig
{
    public int Port { get; init; } = 5100;
    public string[] CorsOrigins { get; init; } = Array.Empty<string>();
    public int ChatTimeoutSeconds { get; init; } = 60;
    public int StreamTimeoutSeconds { get; init; } = 300;
    public int MaxMessageLength { get; init; } = 50000;
}

public sealed class SessionConfig
{
    public string Path { get; init; } = ".livingtree/sessions";
    public int MaxSessions { get; init; } = 500;
    public int KeyRotationMonths { get; init; } = 6;
}

public sealed class WorkflowsConfig
{
    public string WatchDirectory { get; init; } = ".livingtree/workflows";
}

public sealed class SecurityConfig
{
    public string SystemPathFallback { get; init; } = @"C:\Windows\system32;C:\Windows";
}

public sealed class EscalationConfig
{
    public int ComplexityProFastTrack { get; init; } = 4;
    public int GrammarRetryMaxDepth { get; init; } = 2;
    public int CorrectionLoopMaxDepth { get; init; } = 2;
    public int JudgeConfidenceThreshold { get; init; } = 3;
    public double CalibratedScoreThreshold { get; init; } = 0.6;
    public double ValueOfInfoThreshold { get; init; } = 0.5;
    public double ShouldEscalateGapThreshold { get; init; } = 0.3;
    public int ShouldEscalateSupportThreshold { get; init; } = 2;
    public int ShouldEscalateStepsThreshold { get; init; } = 3;
    public int SessionMaxErrors { get; init; } = 5;
    public int SessionCircuitDurationMinutes { get; init; } = 5;
    public double FusionRouteUncertaintyThreshold { get; init; } = 0.4;
    public double CompactionRatioThreshold { get; init; } = 0.75;
    public int MemoryCheckpointTokenInterval { get; init; } = 512;
    public int OnnxModelLoadTimeoutSeconds { get; init; } = 10;
    public int OnnxLocalFallbackThreshold { get; init; } = 3;
    public int MaxFailuresBeforeCooldown { get; init; } = 3;
    public int CooldownDurationSeconds { get; init; } = 30;
    public int PerProviderTimeoutSeconds { get; init; } = 30;
    public double AutoSelectMinScoreImprovement { get; init; } = 0.15;
    public int AutoSelectRefreshIntervalMinutes { get; init; } = 30;
}
