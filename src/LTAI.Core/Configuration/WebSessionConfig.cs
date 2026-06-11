namespace LTAI.Core.Configuration;

public sealed class WebConfig
{
    public int Port { get; init; } = 5100;
    public string[] CorsOrigins { get; init; } = Array.Empty<string>();
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
