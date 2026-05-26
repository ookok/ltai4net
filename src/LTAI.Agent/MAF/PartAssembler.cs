using LTAI.Models;

namespace LTAI.Agent.MAF;

public sealed class PartAssembler
{
    private readonly List<Part> _parts = new();
    private TextPart? _openText;
    private int _seq;

    public IReadOnlyList<Part> Parts => _parts;
    public event Action<Part>? OnPartAppended;
    public event Action<Part>? OnPartUpdated;

    public void FeedText(string textDelta)
    {
        if (_openText == null)
        {
            _openText = new TextPart(NextId(), textDelta) { Seq = _seq++ };
            _parts.Add(_openText);
            OnPartAppended?.Invoke(_openText);
        }
        else
        {
            _openText = _openText with { Text = _openText.Text + textDelta };
            var idx = _parts.FindIndex(p => p.Id == _openText.Id);
            if (idx >= 0) _parts[idx] = _openText;
            OnPartUpdated?.Invoke(_openText);
        }
    }

    public void FeedReasoning(string textDelta)
    {
        CloseOpenText();
        var last = _parts.LastOrDefault() as ReasoningPart;
        if (last != null)
        {
            var updated = last with { Text = last.Text + textDelta };
            var idx = _parts.FindIndex(p => p.Id == last.Id);
            if (idx >= 0) _parts[idx] = updated;
            OnPartUpdated?.Invoke(updated);
        }
        else
        {
            var rp = new ReasoningPart(NextId(), textDelta) { Seq = _seq++ };
            _parts.Add(rp);
            OnPartAppended?.Invoke(rp);
        }
    }

    public ToolInvocationPart StartToolInvocation(string toolName, object? input)
    {
        CloseOpenText();
        var tp = new ToolInvocationPart(NextId(), toolName, input, ToolState.Pending) { Seq = _seq++ };
        _parts.Add(tp);
        OnPartAppended?.Invoke(tp);
        return tp;
    }

    public void StartToolInvocation(ToolInvocationPart part)
    {
        CloseOpenText();
        _parts.Add(part);
        OnPartAppended?.Invoke(part);
    }

    public void UpdateToolState(string partId, ToolState state, object? output = null, string? error = null)
    {
        var idx = _parts.FindIndex(p => p.Id == partId);
        if (idx < 0) return;
        var tp = (ToolInvocationPart)_parts[idx];
        var updated = tp with { State = state, Output = output ?? tp.Output, Error = error ?? tp.Error };
        _parts[idx] = updated;
        OnPartUpdated?.Invoke(updated);
    }

    public void AddFilePart(string path, string? content = null, DiagnosticInfo[]? diagnostics = null)
    {
        CloseOpenText();
        var fp = new FilePart(NextId(), path, content, null, diagnostics) { Seq = _seq++ };
        _parts.Add(fp);
        OnPartAppended?.Invoke(fp);
    }

    public void AddAgentPart(string agentName, string sessionId)
    {
        CloseOpenText();
        var ap = new AgentPart(NextId(), agentName, sessionId) { Seq = _seq++ };
        _parts.Add(ap);
        OnPartAppended?.Invoke(ap);
    }

    public void CloseOpenText() { _openText = null; }

    public Part[] Snapshot() => _parts.ToArray();

    private string NextId() => $"p_{Guid.NewGuid():N}"[..12];
}
