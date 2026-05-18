namespace LTAI.Core.Models;

public enum HandshakePriority
{
    Low,
    Normal,
    High,
    Critical
}

public enum GovernorLevel
{
    Embedding,
    L1,
    L2,
    Auto
}

public enum JournalStatus
{
    Pending,
    Running,
    Done,
    Failed,
    Blocked,
    Skipped,
    Paused
}

public enum SystemMode
{
    Normal,
    Degraded,
    LifeSupport
}
