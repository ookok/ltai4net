using LTAI.Agent.Workflows;

namespace LTAI.TUI.DevUI;

public sealed class WorkflowHealthTracker : IWorkflowSubscriber, IDisposable
{
    private readonly WorkflowHotReloadNotifier _notifier;
    private readonly Guid _subscriptionToken;

    private volatile int _successCount;
    private volatile int _failureCount;
    private string _lastReloadedName = "";
    private DateTime _lastReloadedAtUtc;
    private string _lastFailedName = "";
    private string _lastFailureReason = "";
    private DateTime _lastFailedAtUtc;
    private readonly object _lock = new();

    public int SuccessCount => _successCount;
    public int FailureCount => _failureCount;
    public (string name, DateTime utc) LastReloaded => _lastReloadedName != null
        ? (_lastReloadedName, _lastReloadedAtUtc) : ("", default);
    public (string name, string reason, DateTime utc) LastFailure => _lastFailedName != null
        ? (_lastFailedName, _lastFailureReason, _lastFailedAtUtc) : ("", "", default);

    public WorkflowHealthTracker(WorkflowHotReloadNotifier notifier)
    {
        _notifier = notifier;
        _subscriptionToken = _notifier.Subscribe(this);
    }

    public void OnReloaded(WorkflowReloadEvent evt)
    {
        Interlocked.Increment(ref _successCount);
        lock (_lock)
        {
            _lastReloadedName = evt.Name;
            _lastReloadedAtUtc = evt.ReloadedAtUtc;
        }
    }

    public void OnLoadFailed(WorkflowLoadFailedEvent evt)
    {
        Interlocked.Increment(ref _failureCount);
        lock (_lock)
        {
            _lastFailedName = evt.Name;
            _lastFailureReason = evt.Reason;
            _lastFailedAtUtc = evt.FailedAtUtc;
        }
    }

    public void Dispose()
    {
        _notifier.Unsubscribe(_subscriptionToken);
    }
}
