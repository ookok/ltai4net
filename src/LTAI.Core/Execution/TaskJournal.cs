using System.Collections.Concurrent;
using LTAI.Core.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.Core.Execution;

public sealed class TaskJournal
{
    private readonly ConcurrentBag<JournalEntry> _entries = new();
    private readonly ConcurrentQueue<string> _humanMessages = new();
    private readonly ConcurrentQueue<string> _decisionQueue = new();
    private readonly ILogger<TaskJournal> _logger;
    private readonly object _lock = new();
    private volatile bool _paused;

    public bool IsPaused => _paused;
    public IReadOnlyCollection<JournalEntry> Entries => _entries;
    public IReadOnlyCollection<string> PendingHumanMessages => _humanMessages;

    public TaskJournal(ILogger<TaskJournal> logger)
    {
        _logger = logger;
    }

    public JournalEntry Add(string action, Dictionary<string, object?>? metadata = null)
    {
        var entry = new JournalEntry
        {
            Action = action,
            Status = JournalStatus.Running,
            Metadata = metadata
        };
        _entries.Add(entry);
        _logger.LogInformation("Journal: {Action} started", action);
        return entry;
    }

    public void Complete(JournalEntry entry, string? result = null)
    {
        entry.Status = JournalStatus.Done;
        entry.CompletedAt = DateTime.UtcNow;
        entry.DurationMs = (entry.CompletedAt.Value - entry.StartedAt).TotalMilliseconds;
        entry.Result = result;
        _logger.LogInformation("Journal: {Action} completed in {Duration}ms", entry.Action, entry.DurationMs);
    }

    public void Fail(JournalEntry entry, string error)
    {
        entry.Status = JournalStatus.Failed;
        entry.CompletedAt = DateTime.UtcNow;
        entry.DurationMs = (entry.CompletedAt.Value - entry.StartedAt).TotalMilliseconds;
        entry.Error = error;
        _logger.LogError("Journal: {Action} failed: {Error}", entry.Action, error);
    }

    public void Pause()
    {
        lock (_lock)
        {
            _paused = true;
            _logger.LogInformation("Journal paused");
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            _paused = false;
            _logger.LogInformation("Journal resumed");
        }
    }

    public void InjectMessage(string message)
    {
        _humanMessages.Enqueue(message);
        _logger.LogInformation("Human message injected: {Message}", message[..Math.Min(message.Length, 100)]);
    }

    public bool TryConsumeMessage(out string? message)
    {
        return _humanMessages.TryDequeue(out message);
    }

    public void QueueDecision(string decision)
    {
        _decisionQueue.Enqueue(decision);
    }

    public bool TryGetDecision(out string? decision)
    {
        return _decisionQueue.TryDequeue(out decision);
    }

    public void Clear()
    {
        while (_entries.TryTake(out _)) { }
        while (_humanMessages.TryDequeue(out _)) { }
        while (_decisionQueue.TryDequeue(out _)) { }
        _paused = false;
        _logger.LogInformation("Journal cleared");
    }
}
