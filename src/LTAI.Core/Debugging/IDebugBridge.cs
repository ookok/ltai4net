namespace LTAI.Core.Debugging;

public interface IDebugBridge
{
    IDebugSession? CurrentSession { get; }
    IReadOnlyCollection<DebugBreakpoint> GetAllBreakpoints();
    bool HasBreakpoint(string file, int line);
    void ToggleBreakpoint(string file, int line);
    void SetBreakpoints(string file, int[] lines);
    int[] GetBreakpointLines(string file);
    event Action? BreakpointsChanged;
}
