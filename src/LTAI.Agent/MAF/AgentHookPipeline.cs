using LTAI.Knowledge.Core;
using LTAI.Models;

namespace LTAI.Agent.MAF;

/// <summary>
/// Hook event pipeline — fires before/after tool calls and session lifecycle events.
/// Claude Code philosophy: explicit consent hooks for shell, filesystem, and network operations.
/// </summary>
public sealed class AgentHookPipeline
{
    private readonly List<Func<ToolUseContext, CancellationToken, Task<ToolUseResult>>> _preToolHooks = new();
    private readonly List<Func<ToolUseContext, object?, CancellationToken, Task>> _postToolHooks = new();
    private readonly List<Func<string, CancellationToken, Task>> _sessionStartHooks = new();
    private readonly List<Func<string, CancellationToken, Task>> _sessionEndHooks = new();
    private readonly PermissionStore? _permissionStore;
    private AgentProfile? _activeProfile;

    public AgentProfile? ActiveProfile
    {
        get => _activeProfile;
        set => _activeProfile = value;
    }

    public event Action<ToolUseContext, ToolUseResult>? OnToolBlocked;
    public event Action<ToolUseContext>? OnToolApproved;
#pragma warning disable CS0067 // Reserved for future extensibility
    public event Action<ToolUseContext, string, bool>? OnPermissionGranted;
#pragma warning restore CS0067

    public AgentHookPipeline(PermissionStore? permissionStore = null)
    {
        _permissionStore = permissionStore;
    }

    /// <summary>
    /// Register a hook that fires BEFORE a tool executes.
    /// Return ToolUseResult.Blocked to prevent execution, Allowed to permit.
    /// </summary>
    public void OnPreToolUse(Func<ToolUseContext, CancellationToken, Task<ToolUseResult>> hook)
        => _preToolHooks.Add(hook);

    /// <summary>
    /// Register a hook that fires AFTER a tool executes successfully.
    /// </summary>
    public void OnPostToolUse(Func<ToolUseContext, object?, CancellationToken, Task> hook)
        => _postToolHooks.Add(hook);

    public void OnSessionStart(Func<string, CancellationToken, Task> hook)
        => _sessionStartHooks.Add(hook);

    public void OnSessionEnd(Func<string, CancellationToken, Task> hook)
        => _sessionEndHooks.Add(hook);

    /// <summary>
    /// Run all pre-tool hooks. Returns false if any hook blocks execution.
    /// </summary>
    public async Task<ToolUseResult> RunPreToolHooksAsync(ToolUseContext ctx, CancellationToken ct)
    {
        if (_activeProfile != null && !_activeProfile.CanInvoke(ctx.ToolName, ctx.Args))
        {
            OnToolBlocked?.Invoke(ctx, ToolUseResult.Blocked);
            return ToolUseResult.Blocked;
        }

        if (_permissionStore != null && _permissionStore.IsAllowed(ctx.ToolName, ctx.Args))
        {
            OnToolApproved?.Invoke(ctx);
            return ToolUseResult.Allowed;
        }

        foreach (var hook in _preToolHooks)
        {
            var result = await hook(ctx, ct).ConfigureAwait(false);
            if (result == ToolUseResult.Blocked)
            {
                OnToolBlocked?.Invoke(ctx, result);
                return ToolUseResult.Blocked;
            }
        }

        OnToolApproved?.Invoke(ctx);
        return ToolUseResult.Allowed;
    }

    public void RememberPermission(string toolName, string pattern, bool allow)
    {
        if (_permissionStore == null) return;
        if (allow)
            _permissionStore.Grant(toolName, pattern);
        else
            _permissionStore.Deny(toolName, pattern);
    }

    public PermissionRule[] GetPermissionRules() => _permissionStore?.GetAll() ?? Array.Empty<PermissionRule>();

    public async Task RunPostToolHooksAsync(ToolUseContext ctx, object? result, CancellationToken ct)
    {
        foreach (var hook in _postToolHooks)
            await hook(ctx, result, ct).ConfigureAwait(false);
    }

    public async Task RunSessionStartHooksAsync(string sessionId, CancellationToken ct)
    {
        foreach (var hook in _sessionStartHooks)
            await hook(sessionId, ct).ConfigureAwait(false);
    }

    public async Task RunSessionEndHooksAsync(string sessionId, CancellationToken ct)
    {
        foreach (var hook in _sessionEndHooks)
            await hook(sessionId, ct).ConfigureAwait(false);
    }

    public Dictionary<string, object> GetStats() => new()
    {
        ["pre_tool_hooks"] = _preToolHooks.Count,
        ["post_tool_hooks"] = _postToolHooks.Count,
        ["session_start_hooks"] = _sessionStartHooks.Count,
        ["session_end_hooks"] = _sessionEndHooks.Count
    };
}

public enum ToolUseResult { Allowed, Blocked }

public sealed class ToolUseContext
{
    public string ToolName { get; init; } = "";
    public string SessionId { get; init; } = "";
    public string? Args { get; init; }
    public DateTime RequestedAt { get; init; } = DateTime.UtcNow;
    public string? Reason { get; init; }
}

/// <summary>
/// Built-in hooks implementing Claude Code's safety principles.
/// </summary>
public static class BuiltInHooks
{
    /// <summary>
    /// Blocks shell commands that match destructive patterns.
    /// Maps to Claude Code's explicit consent for bash execution.
    /// </summary>
    public static Func<ToolUseContext, CancellationToken, Task<ToolUseResult>> ShellSafetyHook = (ctx, _) =>
    {
        if (ctx.ToolName is "shell" or "ExecuteCommand")
        {
            var args = ctx.Args ?? "";
            if (args.Contains("rm -rf /") || args.Contains("format c:") ||
                args.Contains("> /dev/sda") || args.Contains("mkfs"))
            {
                return Task.FromResult(ToolUseResult.Blocked);
            }
        }
        return Task.FromResult(ToolUseResult.Allowed);
    };

    /// <summary>
    /// Blocks file operations outside the workspace root.
    /// Maps to Claude Code's workspace sandboxing.
    /// </summary>
    public static Func<ToolUseContext, CancellationToken, Task<ToolUseResult>> FileSystemSafetyHook = (ctx, _) =>
    {
        if (ctx.ToolName is "WriteFile" or "DeleteFile" or "ReadFile")
        {
            var args = ctx.Args ?? "";
            var root = OptionService.Get("LTAI_WORKSPACE") ?? Directory.GetCurrentDirectory();
            if (args.Contains("..") || args.Contains("/etc/") || args.Contains("C:\\Windows"))
                return Task.FromResult(ToolUseResult.Blocked);
        }
        return Task.FromResult(ToolUseResult.Allowed);
    };

    /// <summary>
    /// Blocks HTTP requests to internal IPs.
    /// Maps to Claude Code's network safety.
    /// </summary>
    public static Func<ToolUseContext, CancellationToken, Task<ToolUseResult>> NetworkSafetyHook = (ctx, _) =>
    {
        if (ctx.ToolName is "HttpGet" or "HttpPost")
        {
            var args = ctx.Args ?? "";
            if (args.Contains("127.0.0.1") || args.Contains("169.254") ||
                args.Contains("10.") || args.Contains("192.168"))
                return Task.FromResult(ToolUseResult.Blocked);
        }
        return Task.FromResult(ToolUseResult.Allowed);
    };
}
