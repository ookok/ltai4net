using System.Collections.Concurrent;

namespace LTAI.Agent.Concurrency;

public sealed class AgentWIPManager
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _limits = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _defaultLimit;
    private readonly int _proLimit;

    public AgentWIPManager(int defaultLimit = 4, int proLimit = 2)
    {
        _defaultLimit = defaultLimit;
        _proLimit = proLimit;
    }

    public int GetLimit(string agentName)
    {
        if (_limits.TryGetValue(agentName, out var limit))
            return limit;
        limit = agentName.Contains("Pro", StringComparison.OrdinalIgnoreCase) ? _proLimit : _defaultLimit;
        _limits[agentName] = limit;
        return limit;
    }

    public SemaphoreSlim GetGate(string agentName)
        => _gates.GetOrAdd(agentName, name =>
        {
            var limit = GetLimit(name);
            return new SemaphoreSlim(limit, limit);
        });

    public bool TryEnter(string agentName, int timeoutMs = 0)
    {
        var gate = GetGate(agentName);
        return timeoutMs > 0 ? gate.Wait(timeoutMs) : gate.Wait(0);
    }

    public async Task<bool> TryEnterAsync(string agentName, int timeoutMs = 0, CancellationToken ct = default)
    {
        var gate = GetGate(agentName);
        if (timeoutMs > 0)
            return await gate.WaitAsync(timeoutMs, ct).ConfigureAwait(false);
        return await gate.WaitAsync(0, ct).ConfigureAwait(false);
    }

    public void Release(string agentName)
    {
        if (_gates.TryGetValue(agentName, out var gate))
            gate.Release();
    }

    public int CurrentCount(string agentName)
        => _gates.TryGetValue(agentName, out var gate) ? gate.CurrentCount : 0;

    public int UsedCount(string agentName)
        => GetLimit(agentName) - CurrentCount(agentName);

    public IReadOnlyDictionary<string, int> Snapshot()
        => _gates.Keys.ToDictionary(k => k, k => UsedCount(k));
}
