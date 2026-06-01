using Microsoft.Agents.AI;

namespace LTAI.Desktop;

public sealed class DesktopSession : AgentSession
{
    public string Name { get; set; } = "";
    public string? ParentId { get; set; }
}
