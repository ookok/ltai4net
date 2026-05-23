using System.Collections.Concurrent;

namespace LTAI.Tools.Capability.Governance;

public sealed class ToolGate
{
    private static readonly Lazy<ToolGate> _instance = new(() => new ToolGate());
    public static ToolGate Instance => _instance.Value;

    private readonly ConcurrentDictionary<string, bool> _disabled = new();

    public int DisabledCount => _disabled.Count;

    public void Enable(string toolName)
    {
        _disabled.TryRemove(toolName, out _);
    }

    public void Disable(string toolName)
    {
        _disabled[toolName] = true;
    }

    public bool IsEnabled(string toolName)
    {
        return !_disabled.ContainsKey(toolName);
    }

    public IReadOnlyCollection<string> DisabledTools => _disabled.Keys.ToList().AsReadOnly();

    public bool Reset()
    {
        _disabled.Clear();
        return true;
    }
}
