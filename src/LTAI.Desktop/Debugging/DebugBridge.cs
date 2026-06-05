using LTAI.Core.Debugging;

namespace LTAI.Desktop.Debugging;

public sealed class DebugBridge : IDebugBridge
{
    private DapSession? _session;
    private BreakpointManager? _bp;

    public IDebugSession? CurrentSession => _session;

    public void SetSession(DapSession session, BreakpointManager bp)
    {
        _session = session;
        _bp = bp;
        bp.BreakpointsChanged += () => BreakpointsChanged?.Invoke();
    }

    public void ClearSession()
    {
        _session = null;
        _bp = null;
    }

    public IReadOnlyCollection<DebugBreakpoint> GetAllBreakpoints()
        => _bp?.All.Select(b => new DebugBreakpoint(b.File, b.Line, b.IsEnabled, b.Condition)).ToList()
           ?? [];

    public bool HasBreakpoint(string file, int line)
        => _bp?.HasBreakpoint(file, line) ?? false;

    public void ToggleBreakpoint(string file, int line)
        => _bp?.Toggle(file, line);

    public void SetBreakpoints(string file, int[] lines)
        => _bp?.Set(file, lines);

    public int[] GetBreakpointLines(string file)
        => _bp?.GetLines(file) ?? [];

    public event Action? BreakpointsChanged;
}
