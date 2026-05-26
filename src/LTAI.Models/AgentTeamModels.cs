namespace LTAI.Models;

public enum CoordinatorTaskStatus
{
    Pending,
    Ready,
    Running,
    Completed,
    Failed,
    Cancelled
}

public enum CoordinatorEventType
{
    Decomposing,
    TaskStarted,
    TaskCompleted,
    TaskFailed,
    TaskRetrying,
    Synthesizing,
    Completed
}

public sealed record TeamMember
{
    public string Name { get; init; } = "";
    public string Role { get; init; } = "";
    public string SystemPrompt { get; init; } = "";
    public string? Model { get; init; }
    public List<string>? Tools { get; init; }
}

public sealed record AgentTeam
{
    public string Name { get; init; } = "";
    public string Goal { get; init; } = "";
    public List<TeamMember> Members { get; init; } = new();
    public int MaxConcurrency { get; init; } = 3;
}

public sealed record CoordinatorTask
{
    public string Id { get; init; } = "";
    public string Goal { get; init; } = "";
    public string Assignee { get; init; } = "";
    public List<string> DependsOn { get; init; } = new();
    public CoordinatorTaskStatus Status { get; set; } = CoordinatorTaskStatus.Pending;
    public string? Result { get; set; }
    public string? Error { get; set; }
    public int Attempt { get; set; }
    public int MaxRetries { get; init; } = 2;
}

public sealed record CoordinatorEvent(
    CoordinatorEventType Type,
    string? TaskId = null,
    string? Agent = null,
    string? Data = null
);

public sealed record TeamResult
{
    public bool Success { get; init; }
    public string? FinalOutput { get; init; }
    public string? Error { get; init; }
    public List<CoordinatorEvent> Events { get; init; } = new();
    public IReadOnlyList<CoordinatorTask> TaskGraph { get; init; } = Array.Empty<CoordinatorTask>();
    public int CompletedTasks { get; init; }
    public int FailedTasks { get; init; }
    public int TotalTasks { get; init; }
    public long TotalMs { get; init; }
}
