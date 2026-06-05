namespace LTAI.Core.Debugging;

public interface IDebugSession
{
    DebugState State { get; }
    DebugStackFrame[] CurrentStack { get; }
    DebugVariable[] CurrentScope { get; }
    int CurrentLine { get; }
    string? CurrentFile { get; }

    event Action<DebugState>? StateChanged;
    event Action<int, string?>? Stopped;
    event Action<string>? OutputReceived;

    Task SetBreakpointsAsync(string file, int[] lines);
    Task ContinueAsync();
    Task StepOverAsync();
    Task StepIntoAsync();
    Task StepOutAsync();
    Task TerminateAsync();
    Task<DebugVariable[]> ExpandVariableAsync(int varsRef);
    Task<string?> EvaluateAsync(string expression);
    Task<DebugThreadInfo[]> GetThreadsAsync();
    Task SwitchThreadAsync(int threadId);
}
