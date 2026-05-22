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
    Reasoning,
    Custom
}

public sealed class AgentCard
{
    public string Name { get; init; } = "agent";
    public AgentType Type { get; init; } = AgentType.Chat;
    public string Model { get; init; } = "";
    public string Instructions { get; init; } = "";
    public List<string> Middleware { get; init; } = new();
    public List<string> Tools { get; init; } = new();
    public Dictionary<string, object?> Options { get; init; } = new();
}

public sealed class AgentConfig
{
    public List<AgentCard> Agents { get; init; } = new();
    public Dictionary<string, object?> Global { get; init; } = new();
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
