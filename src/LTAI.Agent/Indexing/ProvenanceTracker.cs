using System.Collections.Concurrent;
using LTAI.Agent.Delta;

namespace LTAI.Agent.Indexing;

public sealed class ProvenanceTracker
{
    private readonly ConcurrentDictionary<string, ProvenanceEntry> _store = new();

    public DeltaStore? DeltaStore { get; set; }

    public void Track(string key, string source, string operation)
    {
        _store[key] = new ProvenanceEntry
        {
            Key = key,
            Source = source,
            Operation = operation,
            Timestamp = DateTime.UtcNow
        };
    }

    public async Task TrackDeltaAsync(string filePath, int startLine, int endLine,
        string toolName, string conversationId, string messageId,
        string? agentId = null, bool isNewFile = false)
    {
        Track(filePath, toolName, $"L{startLine}-L{endLine}");

        if (DeltaStore != null)
        {
            await DeltaStore.CreateDeltaForEditAsync(
                filePath, startLine, endLine,
                diffContent: null, toolName,
                conversationId, messageId,
                agentId, isNewFile).ConfigureAwait(false);
        }
    }

    public ProvenanceEntry? Get(string key)
    {
        return _store.TryGetValue(key, out var entry) ? entry : null;
    }

    public List<ProvenanceEntry> List(string? sourceFilter = null)
    {
        return _store.Values
            .Where(e => sourceFilter == null || e.Source == sourceFilter)
            .OrderByDescending(e => e.Timestamp)
            .ToList();
    }

    public void Clear() => _store.Clear();
}

public sealed class ProvenanceEntry
{
    public string Key { get; set; } = "";
    public string Source { get; set; } = "";
    public string Operation { get; set; } = "";
    public DateTime Timestamp { get; set; }

    public override string ToString() =>
        $"[{Timestamp:HH:mm:ss}] {Operation} <- {Source} ({Key})";
}
