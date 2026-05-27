using LibGit2Sharp;
using LTAI.Core.Governors;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Workflows;

public sealed class WorktreeCleanupService : BackgroundService
{
    private readonly GitWorktreeManager _worktreeManager;
    private readonly TimeSpan _staleThreshold;
    private readonly TimeSpan _checkInterval;
    private readonly ILogger<WorktreeCleanupService> _logger;

    public WorktreeCleanupService(
        GitWorktreeManager worktreeManager,
        TimeSpan? staleThreshold = null,
        TimeSpan? checkInterval = null,
        ILogger<WorktreeCleanupService>? logger = null)
    {
        _worktreeManager = worktreeManager;
        _staleThreshold = staleThreshold ?? TimeSpan.FromHours(24);
        _checkInterval = checkInterval ?? TimeSpan.FromMinutes(30);
        _logger = logger ?? NullLogger<WorktreeCleanupService>.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("WorktreeCleanupService started. " +
            "Stale threshold={Threshold}, Check interval={Interval}",
            _staleThreshold, _checkInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_checkInterval, stoppingToken).ConfigureAwait(false);
                await CleanupLoopAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WorktreeCleanupService iteration failed");
            }
        }

        _logger.LogInformation("WorktreeCleanupService stopped");
    }

    private async Task CleanupLoopAsync(CancellationToken ct)
    {
        var worktrees = await _worktreeManager.ListWorktreesAsync(ct).ConfigureAwait(false);
        var stale = await _worktreeManager.ListStaleBranchesAsync(_staleThreshold, ct).ConfigureAwait(false);

        var staleSet = new HashSet<string>(stale);

        foreach (var wt in worktrees)
        {
            if (ct.IsCancellationRequested) break;

            if (string.IsNullOrEmpty(wt.Branch) || wt.Branch == "main" || wt.Branch == "master")
                continue;

            var isStale = staleSet.Contains(wt.Branch);

            if (!string.IsNullOrEmpty(wt.Path) && Directory.Exists(wt.Path))
            {
                var dirInfo = new DirectoryInfo(wt.Path);
                var age = DateTime.UtcNow - dirInfo.LastWriteTimeUtc;

                if (isStale || age > _staleThreshold)
                {
                    _logger.LogInformation("Cleaning stale worktree: branch={Branch}, path={Path}, age={Age}",
                        wt.Branch, wt.Path, age);

                    var removed = await _worktreeManager.RemoveWorktreeAsync(wt.Path, true, ct)
                        .ConfigureAwait(false);

                    if (removed)
                    {
                        _logger.LogInformation("Successfully cleaned worktree: branch={Branch}", wt.Branch);
                    }
                    else
                    {
                        _logger.LogWarning("Failed to clean worktree: branch={Branch}, path={Path}",
                            wt.Branch, wt.Path);
                    }
                }
            }
        }
    }

    public async Task ForceCleanupAllAsync(CancellationToken ct = default)
    {
        var worktrees = await _worktreeManager.ListWorktreesAsync(ct).ConfigureAwait(false);

        var cleaned = 0;
        foreach (var wt in worktrees)
        {
            if (ct.IsCancellationRequested) break;

            if (string.IsNullOrEmpty(wt.Branch) || wt.Branch == "main" || wt.Branch == "master")
                continue;

            if (!string.IsNullOrEmpty(wt.Path))
            {
                if (Directory.Exists(wt.Path))
                    await TryRebaseWorktreeAsync(wt, ct).ConfigureAwait(false);

                var removed = await _worktreeManager.RemoveWorktreeAsync(wt.Path, true, ct)
                    .ConfigureAwait(false);
                if (removed) cleaned++;
            }
        }

        _logger.LogInformation("Force cleanup completed: {Count} worktrees removed", cleaned);
    }

    public async Task<bool> TryRebaseWorktreeAsync(WorktreeInfo worktreeInfo, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(worktreeInfo.Path) || !Directory.Exists(worktreeInfo.Path))
            return false;

        try
        {
            var repo = OpenWorktreeRepo(worktreeInfo.Path);
            if (repo == null) return false;

            using (repo)
            {
                var mainBranch = repo.Branches["main"] ?? repo.Branches["master"];
                if (mainBranch == null || repo.Head.Tip == null) return false;

                var sig = repo.Config.BuildSignature(DateTimeOffset.UtcNow)
                    ?? new Signature("LTAI Agent", "ltai@agent.local", DateTimeOffset.UtcNow);
                var identity = new Identity("LTAI Agent", "ltai@agent.local");

                try
                {
                    var result = repo.Rebase.Start(
                        repo.Head, repo.Head.TrackedBranch, mainBranch, identity, new RebaseOptions());

                    while (result.Status != RebaseStatus.Complete)
                    {
                        if (result.Status == RebaseStatus.Conflicts || result.Status == RebaseStatus.Stop)
                            break;
                        result = repo.Rebase.Continue(identity, new RebaseOptions());
                    }

                    if (result.Status != RebaseStatus.Complete)
                        throw new Exception("Rebase did not complete");
                }
                catch (Exception)
                {
                    try { repo.Rebase.Abort(); } catch { }
                    _logger.LogInformation("Rebase conflicts for {Branch}, skipping rebase consolidation",
                        worktreeInfo.Branch);
                }

                _logger.LogInformation("Rebased worktree {Branch} onto {Main}",
                    worktreeInfo.Branch, mainBranch.FriendlyName);
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Rebase failed for worktree {Branch}", worktreeInfo.Branch);
            return false;
        }
    }

    public async Task<WorktreeHealthReport> GetHealthReportAsync(CancellationToken ct = default)
    {
        var worktrees = await _worktreeManager.ListWorktreesAsync(ct).ConfigureAwait(false);
        var stale = await _worktreeManager.ListStaleBranchesAsync(_staleThreshold, ct).ConfigureAwait(false);

        var nonDefault = worktrees
            .Where(w => !string.IsNullOrEmpty(w.Branch) && w.Branch != "main" && w.Branch != "master")
            .ToList();

        return new WorktreeHealthReport
        {
            TotalWorktrees = worktrees.Count,
            ActiveAgentWorktrees = nonDefault.Count,
            StaleWorktrees = stale.Count,
            LockedWorktrees = worktrees.Count(w => w.IsLocked),
            DetachedWorktrees = worktrees.Count(w => w.IsDetached),
            StaleThreshold = _staleThreshold,
            CheckInterval = _checkInterval
        };
    }

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
}

public sealed class WorktreeHealthReport
{
    public int TotalWorktrees { get; init; }
    public int ActiveAgentWorktrees { get; init; }
    public int StaleWorktrees { get; init; }
    public int LockedWorktrees { get; init; }
    public int DetachedWorktrees { get; init; }
    public TimeSpan StaleThreshold { get; init; }
    public TimeSpan CheckInterval { get; init; }
}
