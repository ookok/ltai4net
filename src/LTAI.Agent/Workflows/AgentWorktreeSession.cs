using System.Collections.Concurrent;
using LTAI.AI.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Workflows;

public sealed record WorktreeSessionInfo
{
    public string SessionId { get; init; } = "";
    public string AgentId { get; init; } = "";
    public string AgentName { get; init; } = "";
    public string Role { get; init; } = "";
    public string WorktreePath { get; set; } = "";
    public string Branch { get; set; } = "";
    public string BaseBranch { get; init; } = "main";
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public WorktreeSessionState State { get; set; } = WorktreeSessionState.Pending;
    public string? Result { get; set; }
    public string? Error { get; set; }
    public List<string> ModifiedFiles { get; init; } = new();
    public Dictionary<string, object> Metadata { get; init; } = new();
}

public enum WorktreeSessionState
{
    Pending,
    Creating,
    Active,
    Committing,
    Merging,
    ResolvingConflicts,
    Completed,
    Failed,
    Pruned
}

public sealed record AgentWorktreeResult
{
    public bool Success { get; init; }
    public string Output { get; init; } = "";
    public string Branch { get; init; } = "";
    public string WorktreePath { get; init; } = "";
    public List<string> ModifiedFiles { get; init; } = new();
    public string? MergeConflict { get; set; }
    public bool WasMerged { get; set; }
    public string? Error { get; init; }
}

public sealed class AgentWorktreeSession : IAsyncDisposable
{
    private readonly GitWorktreeManager _worktreeManager;
    private readonly ILivingTreeSystem _lts;
    private readonly ILogger<AgentWorktreeSession> _logger;
    private readonly ConcurrentDictionary<string, WorktreeSessionInfo> _sessions = new();
    private bool _disposed;

    public AgentWorktreeSession(
        GitWorktreeManager worktreeManager,
        ILivingTreeSystem lts,
        ILogger<AgentWorktreeSession>? logger = null)
    {
        _worktreeManager = worktreeManager;
        _lts = lts;
        _logger = logger ?? NullLogger<AgentWorktreeSession>.Instance;
    }

    public IReadOnlyDictionary<string, WorktreeSessionInfo> ActiveSessions
    {
        get
        {
            return _sessions.Where(kv => kv.Value.State is WorktreeSessionState.Active
                or WorktreeSessionState.Creating
                or WorktreeSessionState.Committing
                or WorktreeSessionState.Merging
                or WorktreeSessionState.ResolvingConflicts)
                .ToDictionary(kv => kv.Key, kv => kv.Value);
        }
    }

    public async Task<AgentWorktreeResult> RunInWorktreeAsync(
        string agentId,
        string agentName,
        string role,
        string goal,
        Func<string, string, CancellationToken, Task<string>> executeInWorktree,
        bool autoCommit = true,
        bool autoMergeToBase = false,
        string baseBranch = "main",
        CancellationToken ct = default)
    {
        var sessionId = $"wts_{Guid.NewGuid():N}"[..20];
        var session = new WorktreeSessionInfo
        {
            SessionId = sessionId,
            AgentId = agentId,
            AgentName = agentName,
            Role = role,
            BaseBranch = baseBranch,
            State = WorktreeSessionState.Creating
        };
        _sessions[sessionId] = session;

        try
        {
            var createResult = await _worktreeManager.CreateWorktreeAsync(
                agentId, baseBranch, ct).ConfigureAwait(false);

            if (!createResult.Success)
            {
                session.State = WorktreeSessionState.Failed;
                session.Error = createResult.Error;
                return new AgentWorktreeResult
                {
                    Success = false,
                    Error = $"Failed to create worktree: {createResult.Error}"
                };
            }

            session.WorktreePath = createResult.WorktreePath;
            session.Branch = createResult.Branch;
            session.State = WorktreeSessionState.Active;

            _logger.LogInformation("Agent {Agent} running in worktree {Path} on branch {Branch}",
                agentId, createResult.WorktreePath, createResult.Branch);

            var output = await executeInWorktree(goal, createResult.WorktreePath, ct).ConfigureAwait(false);

            session.Result = output;

            var modifiedFiles = await _worktreeManager.GetModifiedFilesAsync(
                createResult.WorktreePath, ct).ConfigureAwait(false);
            session.ModifiedFiles.AddRange(modifiedFiles);

            if (autoCommit && modifiedFiles.Count > 0)
            {
                session.State = WorktreeSessionState.Committing;

                var commitMsg = $"feat: agent {agentName} ({agentId}): {Truncate(goal, 50)}";
                var committed = await _worktreeManager.CommitAndPushAsync(
                    createResult.WorktreePath, commitMsg, "origin", ct).ConfigureAwait(false);

                _logger.LogInformation("Agent {Agent} changes committed={Committed} ({Count} files)",
                    agentId, committed, modifiedFiles.Count);
            }

            session.State = WorktreeSessionState.Completed;
            session.CompletedAt = DateTime.UtcNow;

            return new AgentWorktreeResult
            {
                Success = true,
                Output = output,
                Branch = createResult.Branch,
                WorktreePath = createResult.WorktreePath,
                ModifiedFiles = modifiedFiles
            };
        }
        catch (Exception ex)
        {
            session.State = WorktreeSessionState.Failed;
            session.Error = ex.Message;
            _logger.LogError(ex, "Agent {Agent} worktree session {Session} failed", agentId, sessionId);

            return new AgentWorktreeResult
            {
                Success = false,
                Error = ex.Message,
                Branch = session.Branch,
                WorktreePath = session.WorktreePath
            };
        }
    }

    public async Task<AgentWorktreeResult> RunMultipleInWorktreesAsync(
        IReadOnlyList<(string AgentId, string AgentName, string Role, string Goal)> agents,
        Func<string, string, string, CancellationToken, Task<string>> executeInWorktree,
        bool autoCommit = true,
        string baseBranch = "main",
        int maxConcurrency = 4,
        CancellationToken ct = default)
    {
        var semaphore = new SemaphoreSlim(maxConcurrency);
        var results = new ConcurrentBag<AgentWorktreeResult>();

        var tasks = agents.Select(async agent =>
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var result = await RunInWorktreeAsync(
                    agent.AgentId, agent.AgentName, agent.Role, agent.Goal,
                    (goal, path, c) => executeInWorktree(goal, agent.AgentId, path, c),
                    autoCommit, false, baseBranch, ct).ConfigureAwait(false);
                results.Add(result);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        var allOutputs = string.Join("\n\n---\n\n",
            results.Select(r => $"[Agent: {r.Branch}]\n{r.Output}"));

        var successCount = results.Count(r => r.Success);
        var failCount = results.Count(r => !r.Success);

        return new AgentWorktreeResult
        {
            Success = failCount == 0,
            Output = allOutputs,
            ModifiedFiles = results.SelectMany(r => r.ModifiedFiles).Distinct().ToList(),
            Error = failCount > 0 ? $"{failCount} agent(s) failed" : null
        };
    }

    public async Task<bool> CleanupSessionAsync(string sessionId, bool removeWorktree = true, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return false;

        if (removeWorktree && !string.IsNullOrEmpty(session.WorktreePath))
        {
            await _worktreeManager.RemoveWorktreeAsync(session.WorktreePath, true, ct).ConfigureAwait(false);
            session.State = WorktreeSessionState.Pruned;
        }

        return true;
    }

    public WorktreeSessionInfo? GetSession(string sessionId)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return session;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var kv in _sessions)
        {
            if (kv.Value.State == WorktreeSessionState.Active && !string.IsNullOrEmpty(kv.Value.WorktreePath))
            {
                try
                {
                    await _worktreeManager.RemoveWorktreeAsync(kv.Value.WorktreePath, true).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to cleanup worktree {Path} during dispose", kv.Value.WorktreePath);
                }
            }
        }

        _sessions.Clear();
    }

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..maxLen] + "...";
}
