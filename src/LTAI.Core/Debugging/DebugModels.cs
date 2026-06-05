namespace LTAI.Core.Debugging;

public enum DebugState { Idle, Launching, Running, Paused, Terminating, Terminated }

public sealed record DebugStackFrame(int Id, string Name, string? File, int Line, int Column);
public sealed record DebugVariable(string Name, string Value, string Type, int VariablesReference);

public sealed record DebugThreadInfo(int Id, string? Name, bool IsPaused);
public sealed record DebugBreakpoint(string File, int Line, bool IsEnabled = true, string? Condition = null);
