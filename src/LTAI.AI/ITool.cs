using Microsoft.Extensions.AI;

namespace LTAI.AI;

/// <summary>
/// Standard interface for tool implementations.
/// Opt-in contract — existing tools continue to work via method annotations.
/// New tools should implement ITool for structured error propagation,
/// permission introspection, and cancellation token support.
/// </summary>
public interface ITool
{
    /// <summary>Tool name (unique within agent).</summary>
    string Name { get; }

    /// <summary>Human-readable description for LLM selection.</summary>
    string Description { get; }

    /// <summary>Domain category for ToolRegistry semantic retrieval.</summary>
    string Domain { get; }

    /// <summary>Required permissions to execute this tool.</summary>
    ToolPermission RequiredPermissions { get; }

    /// <summary>Execute the tool with structured context and return typed result.</summary>
    Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct);
}

/// <summary>Required permissions for a tool.</summary>
[Flags]
public enum ToolPermission
{
    None = 0,
    Read = 1 << 0,
    Write = 1 << 1,
    Execute = 1 << 2,
    Network = 1 << 3,
    All = Read | Write | Execute,
}

/// <summary>Structured execution context passed to ITool.ExecuteAsync.</summary>
public sealed class ToolExecutionContext
{
    /// <summary>Workspace root path.</summary>
    public required string Workspace { get; init; }

    /// <summary>Granted permissions for this execution.</summary>
    public required ToolPermission GrantedPermissions { get; init; }

    /// <summary>Additional metadata (session ID, user ID, etc.).</summary>
    public Dictionary<string, object?> Metadata { get; } = new();
}

/// <summary>Structured tool execution result.</summary>
public sealed record ToolExecutionResult
{
    public bool Success { get; init; }
    public string? Output { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ErrorCode { get; init; }

    public static ToolExecutionResult Ok(string output) => new()
    {
        Success = true,
        Output = output
    };

    public static ToolExecutionResult Fail(string error, string? code = null) => new()
    {
        Success = false,
        ErrorMessage = error,
        ErrorCode = code
    };

}
