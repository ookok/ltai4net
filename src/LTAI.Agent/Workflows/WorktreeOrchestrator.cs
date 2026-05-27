using System.Collections.Concurrent;
using LibGit2Sharp;
using LTAI.AI.Interfaces;
using LTAI.Knowledge.Core;
using LTAI.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Workflows;

public sealed record WorktreeOrchestratorConfig
{
    public bool EnableWorktreeIsolation { get; init; } = true;
    public string BaseBranch { get; init; } = "main";
    public int MaxConcurrency { get; init; } = 4;
    public bool AutoCommit { get; init; } = true;
    public bool AutoPruneOnCompletion { get; init; } = true;
    public TimeSpan StaleThreshold { get; init; } = TimeSpan.FromHours(24);
}

public sealed class WorktreeOrchestrator : IAsyncDisposable
{
    private readonly LTAICoordinator _coordinator;
    private readonly GitWorktreeManager _worktreeManager;
    private readonly AgentWorktreeSession _sessionManager;
    private readonly ILivingTreeSystem _lts;
    private readonly PromptService? _promptService;
    private readonly BackpressurePipeline? _backpressure;
    private readonly WorktreeOrchestratorConfig _config;
    private readonly ILogger<WorktreeOrchestrator> _logger;
    private readonly ConcurrentDictionary<string, AgentWorktreeResult> _completedResults = new();
    private bool _disposed;

    public WorktreeOrchestrator(
        LTAICoordinator coordinator,
        GitWorktreeManager worktreeManager,
        AgentWorktreeSession sessionManager,
        ILivingTreeSystem lts,
        PromptService? promptService = null,
        WorktreeOrchestratorConfig? config = null,
        BackpressurePipeline? backpressure = null,
        ILogger<WorktreeOrchestrator>? logger = null)
    {
        _coordinator = coordinator;
        _worktreeManager = worktreeManager;
        _sessionManager = sessionManager;
        _lts = lts;
        _promptService = promptService;
        _backpressure = backpressure;
        _config = config ?? new WorktreeOrchestratorConfig();
        _logger = logger ?? NullLogger<WorktreeOrchestrator>.Instance;
    }

    public async Task<AgentWorktreeResult> SpawnWorktreeAgentAsync(
        string agentName,
        string goal,
        string role = "subagent",
        List<string>? allowedTools = null,
        string? baseBranch = null,
        CancellationToken ct = default)
    {
        if (!_config.EnableWorktreeIsolation)
        {
            _logger.LogInformation("Worktree isolation disabled, falling back to direct subagent spawn");
            var subSession = await _coordinator.SpawnSubagentAsync(agentName, goal, role, allowedTools, ct)
                .ConfigureAwait(false);
            return new AgentWorktreeResult
            {
                Success = subSession.Result != null && !subSession.Result.StartsWith("Subagent error"),
                Output = subSession.Result ?? "",
                Branch = "",
                WorktreePath = ""
            };
        }

        var agentId = $"agent_{agentName}_{Guid.NewGuid():N}"[..32];
        var branch = baseBranch ?? _config.BaseBranch;

        var result = await _sessionManager.RunInWorktreeAsync(
            agentId, agentName, role, goal,
            async (g, worktreePath, c) =>
            {
                var originalWorkspace = OptionService.Get("LTAI_WORKSPACE");
                try
                {
                    Environment.SetEnvironmentVariable("LTAI_WORKSPACE", worktreePath);

                    _logger.LogDebug("Workspace switched to worktree: {Path}", worktreePath);

                    var subSession = await _coordinator.SpawnSubagentAsync(
                        agentName, g, role, allowedTools, c).ConfigureAwait(false);

                    return subSession.Result ?? "";
                }
                finally
                {
                    if (originalWorkspace != null)
                    {
                        Environment.SetEnvironmentVariable("LTAI_WORKSPACE", originalWorkspace);
                    }
                }
            },
            autoCommit: _config.AutoCommit,
            baseBranch: branch,
            ct: ct).ConfigureAwait(false);

        _completedResults[agentId] = result;
        return result;
    }

    public async Task<IReadOnlyList<AgentWorktreeResult>> SpawnWorktreeAgentsAsync(
        IReadOnlyList<(string AgentName, string Goal, string Role)> agents,
        string? baseBranch = null,
        CancellationToken ct = default)
    {
        if (!_config.EnableWorktreeIsolation)
        {
            _logger.LogInformation("Worktree isolation disabled, spawning agents directly");

            var agentRoles = agents.ToDictionary(a => a.AgentName, a => a.Role);
            var sharedGoal = agents.Count == 1
                ? agents[0].Goal
                : string.Join(" | ", agents.Select(a => a.Goal));

            var synthesis = await _coordinator.SpawnSubagentsParallelAsync(agentRoles, sharedGoal, ct)
                .ConfigureAwait(false);

            return new List<AgentWorktreeResult>
            {
                new()
                {
                    Success = true,
                    Output = synthesis,
                    Branch = "",
                    WorktreePath = ""
                }
            };
        }

        var batch = agents.Select((a, i) =>
            (
                AgentId: $"agent_{a.AgentName}_{Guid.NewGuid():N}"[..32],
                AgentName: a.AgentName,
                Role: a.Role,
                Goal: a.Goal
            )).ToList();

        var branch = baseBranch ?? _config.BaseBranch;

        var result = await _sessionManager.RunMultipleInWorktreesAsync(
            batch,
            async (goal, agentId, worktreePath, c) =>
            {
                var originalWorkspace = OptionService.Get("LTAI_WORKSPACE");
                try
                {
                    Environment.SetEnvironmentVariable("LTAI_WORKSPACE", worktreePath);

                    var agent = agents.First(a => a.AgentName == batch
                        .First(b => b.AgentId == agentId).AgentName);

                    var subSession = await _coordinator.SpawnSubagentAsync(
                        agent.AgentName, goal, agent.Role, ct: c).ConfigureAwait(false);

                    return subSession.Result ?? "";
                }
                finally
                {
                    if (originalWorkspace != null)
                    {
                        Environment.SetEnvironmentVariable("LTAI_WORKSPACE", originalWorkspace);
                    }
                }
            },
            autoCommit: _config.AutoCommit,
            baseBranch: branch,
            maxConcurrency: _config.MaxConcurrency,
            ct: ct).ConfigureAwait(false);

        return new List<AgentWorktreeResult> { result };
    }

    public async Task<TeamResult> RunTeamInWorktreesAsync(
        AgentTeam team,
        string goal,
        string? baseBranch = null,
        CancellationToken ct = default)
    {
        if (!_config.EnableWorktreeIsolation)
        {
            return await _coordinator.RunTeamAsync(team, goal, ct).ConfigureAwait(false);
        }

        var batch = team.Members.Select(m =>
            (m.Name, goal, m.Role)).ToList();

        var results = await SpawnWorktreeAgentsAsync(batch, baseBranch, ct).ConfigureAwait(false);

        if (_backpressure != null)
        {
            foreach (var result in results)
            {
                if (!string.IsNullOrEmpty(result.WorktreePath) && result.Success)
                {
                    var backpressureResult = await _backpressure.CheckAsync(
                        result.WorktreePath, agentId: batch.First().Name,
                        taskDescription: goal, ct: ct).ConfigureAwait(false);

                    if (!backpressureResult.AllPassed)
                    {
                        _logger.LogWarning("Backpressure rejected worktree {Path}: {Summary}",
                            result.WorktreePath, backpressureResult.RejectSummary());
                        return new TeamResult
                        {
                            Success = false,
                            FinalOutput = $"QUALITY GATE FAILED:\n{backpressureResult.RejectSummary()}",
                            Events = new List<CoordinatorEvent>
                            {
                                new(CoordinatorEventType.TaskFailed, Data: backpressureResult.RejectSummary())
                            },
                            TaskGraph = new List<CoordinatorTask>(),
                            CompletedTasks = 0,
                            FailedTasks = batch.Count,
                            TotalTasks = batch.Count
                        };
                    }

                    _logger.LogInformation("Backpressure passed for {Path}: {Time}ms",
                        result.WorktreePath, backpressureResult.TotalTime.TotalMilliseconds);
                }
            }
        }

        var output = results.FirstOrDefault()?.Output ?? "";
        var success = results.FirstOrDefault()?.Success ?? false;

        var events = new List<CoordinatorEvent>
        {
            new(CoordinatorEventType.Completed, Data: output)
        };

        return new TeamResult
        {
            Success = success,
            FinalOutput = output,
            Events = events,
            TaskGraph = new List<CoordinatorTask>(),
            CompletedTasks = success ? batch.Count : 0,
            FailedTasks = success ? 0 : batch.Count,
            TotalTasks = batch.Count
        };
    }

    public async Task<IReadOnlyList<WorktreeInfo>> ListActiveWorktreesAsync(CancellationToken ct = default)
    {
        return await _worktreeManager.ListWorktreesAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> CleanupAgentWorktreeAsync(string agentId, CancellationToken ct = default)
    {
        if (_completedResults.TryRemove(agentId, out var result) && !string.IsNullOrEmpty(result.WorktreePath))
        {
            return await _worktreeManager.RemoveWorktreeAsync(result.WorktreePath, true, ct)
                .ConfigureAwait(false);
        }

        var sessions = _sessionManager.ActiveSessions;
        foreach (var kv in sessions)
        {
            if (kv.Value.AgentId == agentId)
            {
                return await _sessionManager.CleanupSessionAsync(kv.Key, true, ct).ConfigureAwait(false);
            }
        }

        return false;
    }

    public async Task<List<string>> CleanupStaleWorktreesAsync(CancellationToken ct = default)
    {
        var stale = await _worktreeManager.ListStaleBranchesAsync(_config.StaleThreshold, ct)
            .ConfigureAwait(false);
        var cleaned = new List<string>();

        var worktrees = await _worktreeManager.ListWorktreesAsync(ct).ConfigureAwait(false);

        foreach (var branch in stale)
        {
            var wt = worktrees.FirstOrDefault(w => w.Branch == branch);
            if (wt == null) continue;

            var removed = await _worktreeManager.RemoveWorktreeAsync(wt.Path, true, ct)
                .ConfigureAwait(false);
            if (removed)
            {
                cleaned.Add(branch);
                _logger.LogInformation("Cleaned stale worktree: branch={Branch}", branch);
            }
        }

        return cleaned;
    }

    public int ActiveCount => _sessionManager.ActiveSessions.Count;
    public int CompletedCount => _completedResults.Count;

    public async Task<bool> CherryPickBetweenWorktreesAsync(
        string sourceWorktreePath,
        string targetWorktreePath,
        string? specificFile = null,
        CancellationToken ct = default)
    {
        try
        {
            var sourceRepo = OpenWorktreeRepo(sourceWorktreePath);
            var targetRepo = OpenWorktreeRepo(targetWorktreePath);
            if (sourceRepo == null || targetRepo == null) return false;

            using (sourceRepo)
            using (targetRepo)
            {
                var sourceCommit = sourceRepo.Head.Tip;
                if (sourceCommit == null) return false;

                var sig = new Signature("LTAI Agent", "ltai@agent.local", DateTimeOffset.UtcNow);
                var result = targetRepo.CherryPick(sourceCommit, sig,
                    new CherryPickOptions { CommitOnSuccess = true });

                if (result.Status == CherryPickStatus.CherryPicked)
                {
                    _logger.LogInformation("Cherry-picked {Commit} from {Source} to {Target}",
                        sourceCommit.Sha[..8], sourceWorktreePath, targetWorktreePath);
                    return true;
                }

                if (result.Status == CherryPickStatus.Conflicts)
                {
                    _logger.LogWarning("Cherry-pick conflicts: {Source} -> {Target}",
                        sourceWorktreePath, targetWorktreePath);
                    return false;
                }

                _logger.LogWarning("Cherry-pick failed with status: {Status}", result.Status);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cherry-pick failed: {Source} -> {Target}",
                sourceWorktreePath, targetWorktreePath);
            return false;
        }
    }

    public WorktreeOrchestratorConfig GetConfig() => _config;

    private static Repository? OpenWorktreeRepo(string worktreePath)
    {
        var gitFile = Path.Combine(worktreePath, ".git");
        if (File.Exists(gitFile))
        {
            var content = File.ReadAllText(gitFile).Trim();
            if (content.StartsWith("gitdir: ", StringComparison.Ordinal))
            {
                var gitDir = content["gitdir: ".Length..].Trim();
                if (Directory.Exists(gitDir))
                    return new Repository(gitDir);
            }
        }

        var repoPath = Path.Combine(worktreePath, ".git");
        if (Directory.Exists(repoPath))
            return new Repository(repoPath);

        if (Repository.IsValid(worktreePath))
            return new Repository(worktreePath);

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_config.AutoPruneOnCompletion)
        {
            await CleanupStaleWorktreesAsync().ConfigureAwait(false);
        }

        await _sessionManager.DisposeAsync().ConfigureAwait(false);
        await _coordinator.DisposeAsync().ConfigureAwait(false);
    }
}
