using System.Collections.Concurrent;

namespace LTAI.Core.System;

public sealed record VisualReference(
    string ReferenceId,
    int Index,
    string SourceTool,
    string MimeType,
    byte[] Data,
    string Description,
    DateTimeOffset RegisteredAt,
    int AccessCount)
{
    public VisualReference IncrementAccess() => this with { AccessCount = AccessCount + 1 };
}

public enum VisualToolKind
{
    WebSearch,
    ImageSearch,
    ScholarSearch,
    Visit,
    VisualSearch,
    ZoomIn,
    Rotation,
    Flip,
    PythonExec
}

public sealed record ImageBankUsageStats
{
    public int TotalRegistered { get; set; }
    public int TotalAccesses { get; set; }
    public int DistinctImagesReused { get; set; }
    public Dictionary<VisualToolKind, int> RegistrationsByTool { get; set; } = new();
    public Dictionary<VisualToolKind, int> ReuseSourcesByConsumer { get; set; } = new();
    public DateTimeOffset BankOpenedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastRegistrationAt { get; set; }

    public double ReuseRate => TotalRegistered > 0
        ? (double)DistinctImagesReused / TotalRegistered
        : 0;
}

public sealed class VisualReferenceBank
{
    private readonly ConcurrentDictionary<string, VisualReference> _bank = new();
    private readonly ConcurrentDictionary<string, List<string>> _reuseLog = new();
    private int _nextIndex;
    private readonly ImageBankUsageStats _stats = new();

    public string RegisterImage(
        byte[] data,
        VisualToolKind sourceTool,
        string mimeType = "image/png",
        string description = "")
    {
        var index = Interlocked.Increment(ref _nextIndex);
        var refId = $"image:{index}";

        var vref = new VisualReference(
            ReferenceId: refId,
            Index: index,
            SourceTool: sourceTool.ToString(),
            MimeType: mimeType,
            Data: data,
            Description: description,
            RegisteredAt: DateTimeOffset.UtcNow,
            AccessCount: 0);

        _bank[refId] = vref;
        _reuseLog.TryAdd(refId, new List<string>());

        _stats.TotalRegistered++;
        _stats.LastRegistrationAt = DateTimeOffset.UtcNow;

        if (!_stats.RegistrationsByTool.ContainsKey(sourceTool))
            _stats.RegistrationsByTool[sourceTool] = 0;
        _stats.RegistrationsByTool[sourceTool]++;

        return refId;
    }

    public VisualReference? GetImage(string referenceId)
    {
        if (_bank.TryGetValue(referenceId, out var vref))
        {
            var updated = vref.IncrementAccess();
            _bank[referenceId] = updated;
            _stats.TotalAccesses++;
            return updated;
        }
        return null;
    }

    public VisualReference? ResolveReference(string token)
    {
        var trimmed = token.Trim();
        if (trimmed.StartsWith("@"))
            trimmed = trimmed[1..];

        if (trimmed.StartsWith("image:"))
            return GetImage(trimmed);

        if (int.TryParse(trimmed, out var index))
            return GetImage($"image:{index}");

        foreach (var kv in _bank)
        {
            if (kv.Value.Description.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        }

        return null;
    }

    public bool LogReuse(string consumerTool, string referenceId)
    {
        if (!_bank.ContainsKey(referenceId))
            return false;

        if (_reuseLog.TryGetValue(referenceId, out var log))
        {
            lock (log) { log.Add(consumerTool); }
            _stats.DistinctImagesReused++;
        }

        var kind = Enum.TryParse<VisualToolKind>(consumerTool, out var k) ? k : default;
        if (!_stats.ReuseSourcesByConsumer.ContainsKey(kind))
            _stats.ReuseSourcesByConsumer[kind] = 0;
        _stats.ReuseSourcesByConsumer[kind]++;

        return true;
    }

    public List<VisualReference> ListAll()
        => _bank.Values.OrderBy(v => v.Index).ToList();

    public List<VisualReference> QueryByTool(VisualToolKind sourceTool, int maxResults = 50)
        => _bank.Values
            .Where(v => v.SourceTool == sourceTool.ToString())
            .OrderByDescending(v => v.AccessCount)
            .Take(maxResults)
            .ToList();

    public List<VisualReference> SearchByDescription(string keyword, int maxResults = 20)
        => _bank.Values
            .Where(v => v.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(v => v.RegisteredAt)
            .Take(maxResults)
            .ToList();

    public int Count => _bank.Count;

    public ImageBankUsageStats GetStats()
    {
        _stats.DistinctImagesReused = _reuseLog.Count(kv =>
        {
            lock (kv.Value) { return kv.Value.Count > 0; }
        });
        return _stats;
    }

    public void Clear()
    {
        _bank.Clear();
        _reuseLog.Clear();
        _nextIndex = 0;
    }

    public Dictionary<string, string> BuildReferencePrompt()
    {
        var context = new Dictionary<string, string>();
        foreach (var (refId, vref) in _bank)
        {
            var label = $"<{refId}> {vref.Description} (from {vref.SourceTool})";
            context[refId] = label;
        }
        return context;
    }
}
