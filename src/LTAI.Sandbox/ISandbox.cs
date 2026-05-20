namespace LTAI.Sandbox;

public interface ISandbox : IAsyncDisposable
{
    string Name { get; }
    SandboxCapability Capability { get; }
    Task<SandboxResult> ExecuteAsync(SandboxRequest request, CancellationToken cancellationToken = default);
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
}

[Flags]
public enum SandboxCapability
{
    None = 0,
    Python = 1 << 0,
    JavaScript = 1 << 1,
    CSharp = 1 << 2,
    Shell = 1 << 3,
    NetworkIsolation = 1 << 4,
    FilesystemIsolation = 1 << 5,
    MemoryLimit = 1 << 6,
    Timeout = 1 << 7,
    All = Python | JavaScript | CSharp | Shell | NetworkIsolation | FilesystemIsolation | MemoryLimit | Timeout
}

public sealed class SandboxRequest
{
    public string Code { get; init; } = "";
    public SandboxLanguage Language { get; init; } = SandboxLanguage.Python;
    public int TimeoutSeconds { get; init; } = 30;
    public int MemoryLimitMb { get; init; } = 256;
    public string? WorkingDirectory { get; init; }
    public Dictionary<string, string>? EnvironmentVariables { get; init; }
    public string? Stdin { get; init; }
    public bool NetworkEnabled { get; init; }
    public bool ReadOnlyFilesystem { get; init; } = true;
}

public sealed class SandboxResult
{
    public bool Success { get; init; }
    public string Stdout { get; init; } = "";
    public string Stderr { get; init; } = "";
    public int ExitCode { get; init; }
    public long ExecutionTimeMs { get; init; }
    public long PeakMemoryKb { get; init; }
    public string? Error { get; init; }
    public bool TimedOut { get; init; }
}

public enum SandboxLanguage { Python, JavaScript, CSharp, Shell }

public enum SandboxBackend { Auto, Process, Docker, Remote }
