using System.Collections.Concurrent;

namespace LTAI.Tools.Capability.Governance;

public enum ToolLifecycleState { Active, Deprecated, Removed, Experimental }

public sealed class ToolLifecycleEntry
{
    public string Name { get; init; } = "";
    public string Version { get; init; } = "1.0.0";
    public ToolLifecycleState State { get; set; } = ToolLifecycleState.Active;
    public string? DeprecationMessage { get; set; }
    public string? Replacement { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? DeprecatedAt { get; set; }
    public DateTime? RemovedAt { get; set; }
    public int InvocationCount;
    public int ErrorCount;
    public double SuccessRate => InvocationCount > 0 ? 1.0 - (double)ErrorCount / InvocationCount : 1.0;
}

public sealed class ToolLifecycle
{
    private static readonly Lazy<ToolLifecycle> _instance = new(() => new ToolLifecycle());
    public static ToolLifecycle Instance => _instance.Value;

    private readonly ConcurrentDictionary<string, ToolLifecycleEntry> _entries = new();

    public void Register(string name, string version = "1.0.0", ToolLifecycleState state = ToolLifecycleState.Active)
    {
        _entries.TryAdd(name, new ToolLifecycleEntry
        {
            Name = name,
            Version = version,
            State = state
        });
    }

    public void Deprecate(string name, string replacement, string? message = null)
    {
        if (_entries.TryGetValue(name, out var entry))
        {
            entry.State = ToolLifecycleState.Deprecated;
            entry.Replacement = replacement;
            entry.DeprecationMessage = message ?? $"Deprecated. Use {replacement} instead.";
            entry.DeprecatedAt = DateTime.UtcNow;
        }
    }

    public void Remove(string name)
    {
        if (_entries.TryGetValue(name, out var entry))
        {
            entry.State = ToolLifecycleState.Removed;
            entry.RemovedAt = DateTime.UtcNow;
        }
    }

    public void RecordInvocation(string name, bool success)
    {
        if (_entries.TryGetValue(name, out var entry))
        {
            Interlocked.Increment(ref entry.InvocationCount);
            if (!success)
                Interlocked.Increment(ref entry.ErrorCount);
        }
    }

    public bool IsUsable(string name)
    {
        if (!_entries.TryGetValue(name, out var entry))
            return true;
        return entry.State is ToolLifecycleState.Active or ToolLifecycleState.Experimental;
    }

    public string? GetReplacement(string name)
    {
        return _entries.TryGetValue(name, out var entry) ? entry.Replacement : null;
    }

    public ToolLifecycleEntry? Get(string name)
    {
        return _entries.TryGetValue(name, out var entry) ? entry : null;
    }

    public IReadOnlyList<ToolLifecycleEntry> GetAll()
    {
        return _entries.Values.OrderBy(e => e.Name).ToList();
    }

    public IReadOnlyList<ToolLifecycleEntry> GetActive()
    {
        return _entries.Values
            .Where(e => e.State == ToolLifecycleState.Active)
            .OrderBy(e => e.Name)
            .ToList();
    }

    public IReadOnlyList<ToolLifecycleEntry> GetDeprecated()
    {
        return _entries.Values
            .Where(e => e.State == ToolLifecycleState.Deprecated)
            .OrderByDescending(e => e.DeprecatedAt)
            .ToList();
    }

    public IReadOnlyList<ToolLifecycleEntry> GetFailing(double threshold = 0.5, int minInvocations = 10)
    {
        return _entries.Values
            .Where(e => e.InvocationCount >= minInvocations && e.SuccessRate < threshold)
            .OrderBy(e => e.SuccessRate)
            .ToList();
    }
}
