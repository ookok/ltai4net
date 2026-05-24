namespace LTAI.Models;

public enum SystemMode
{
    Normal,
    Safe,
    Learning,
    Offline,
    Degraded
}

public enum AgentType
{
    Chat,
    Code,
    EIA,
    EiaCritic,
    Reasoning,
    Custom
}

public sealed class LTAIAgentCard
{
    public string Name { get; set; } = "agent";
    public AgentType Type { get; set; } = AgentType.Chat;
    public string Model { get; set; } = "";
    public string Instructions { get; set; } = "";
    public List<string> Middleware { get; set; } = new();
    public List<string> Tools { get; set; } = new();
    public Dictionary<string, object?> Options { get; set; } = new();
}

public sealed class AgentConfig
{
    public List<LTAIAgentCard> Agents { get; set; } = new();
    public Dictionary<string, object?> Global { get; set; } = new();
}

public sealed class DNAStatus
{
    public string PersonaMode { get; init; } = "normal";
    public float Energy { get; init; } = 1.0f;
    public float Curiosity { get; init; } = 0.7f;
    public string Mood { get; init; } = "neutral";
    public DateTime LastUpdate { get; init; } = DateTime.UtcNow;
}

public sealed class ToolMetadata
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Category { get; init; } = "general";
    public List<string> Parameters { get; init; } = new();
    public bool Enabled { get; set; } = true;
}

public sealed class ProviderInfo
{
    public string Name { get; init; } = "";
    public string Model { get; init; } = "";
    public string Endpoint { get; init; } = "";
    public int Priority { get; init; } = 1;
    public List<string> Capabilities { get; init; } = new();
}

public sealed class Handshake
{
    public string To { get; init; } = "";
    public string Action { get; init; } = "process";
    public Dictionary<string, object?>? Payload { get; init; }
    public string? ReplyTo { get; init; }
}

public sealed class ProgressEvent
{
    public string Message { get; init; } = "";
    public float Progress { get; init; }
    public string? Stage { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public interface ISandboxExecutor
{
    Task<SandboxExecutionResult> ExecuteCommandAsync(
        string command,
        int timeoutSeconds = 30,
        int memoryMb = 256,
        bool allowNetwork = false,
        CancellationToken cancellationToken = default);
}

public sealed class SandboxExecutionResult
{
    public bool Success { get; init; }
    public string Stdout { get; init; } = "";
    public string Stderr { get; init; } = "";
    public int ExitCode { get; init; }
    public long ExecutionTimeMs { get; init; }
    public long PeakMemoryKb { get; init; }
    public string? Error { get; init; }
    public bool TimedOut { get; init; }
    public bool Sandboxed { get; init; }
}
