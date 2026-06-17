using System.Collections.Concurrent;
using LTAI.Agent.Execution;

namespace LTAI.Agent.DevUI;

public enum AgentStatus
{
    Idle,
    Running,
    Blocked,
    Done
}

public sealed record AgentKanbanItem
{
    public string AgentName { get; init; } = "";
    public AgentStatus Status { get; init; } = AgentStatus.Idle;
    public string? CurrentTask { get; init; }
    public string? TraceId { get; init; }
    public string? SpanId { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public TimeSpan? Duration { get; init; }
    public string? LastError { get; init; }
    public int ToolCallCount { get; init; }
    public int TokenEstimate { get; init; }
}

public sealed class AgentKanbanBoard
{
    private readonly ConcurrentDictionary<string, AgentKanbanItem> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _maxItems;

    public AgentKanbanBoard(int maxItems = 200)
    {
        _maxItems = maxItems;
    }

    public void TrackSpan(ExecutionSpan span)
    {
        var agent = span.AgentName ?? "unknown";
        var status = span.Status switch
        {
            SpanStatus.Success => AgentStatus.Done,
            SpanStatus.Failure => AgentStatus.Blocked,
            _ => AgentStatus.Running
        };

        _items[agent] = new AgentKanbanItem
        {
            AgentName = agent,
            Status = status,
            CurrentTask = span.StepName,
            TraceId = span.TraceId,
            SpanId = span.SpanId,
            StartedAt = span.StartTimeUtc != default ? span.StartTimeUtc : null,
            CompletedAt = span.EndTimeUtc != default ? span.EndTimeUtc : null,
            Duration = span.Duration != default ? span.Duration : null,
            LastError = span.Error,
            ToolCallCount = 0,
            TokenEstimate = span.InputTokens + span.OutputTokens
        };

        while (_items.Count > _maxItems)
        {
            var oldest = _items.Values
                .Where(i => i.Status == AgentStatus.Done)
                .OrderBy(i => i.CompletedAt)
                .FirstOrDefault();
            if (oldest != null)
                _items.TryRemove(oldest.AgentName, out _);
            else
                break;
        }
    }

    public void SetIdle(string agent)
    {
        _items[agent] = _items.GetValueOrDefault(agent) with { Status = AgentStatus.Idle };
    }

    public IReadOnlyList<AgentKanbanItem> GetBoard()
        => _items.Values.OrderBy(i => i.Status).ThenBy(i => i.StartedAt).ToList();

    public IReadOnlyList<AgentKanbanItem> GetByStatus(AgentStatus status)
        => _items.Values.Where(i => i.Status == status).ToList();

    public int RunningCount => _items.Values.Count(i => i.Status == AgentStatus.Running);
    public int BlockedCount => _items.Values.Count(i => i.Status == AgentStatus.Blocked);
    public int IdleCount => _items.Values.Count(i => i.Status == AgentStatus.Idle);
    public int DoneCount => _items.Values.Count(i => i.Status == AgentStatus.Done);

    public (int running, int blocked, int idle, int done) Counts
        => (RunningCount, BlockedCount, IdleCount, DoneCount);
}
