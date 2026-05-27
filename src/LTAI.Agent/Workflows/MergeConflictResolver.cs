using System.Collections.Concurrent;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Workflows;

public sealed record MergeConflictInfo
{
    public string SourceBranch { get; init; } = "";
    public string TargetBranch { get; init; } = "";
    public List<string> ConflictingFiles { get; init; } = new();
    public string DiffOutput { get; init; } = "";
    public DateTime DetectedAt { get; init; } = DateTime.UtcNow;
    public int AttemptCount { get; set; }
    public MergeResolutionStatus Status { get; set; } = MergeResolutionStatus.Detected;
}

public enum MergeResolutionStatus
{
    Detected,
    Attempting,
    AutoResolved,
    RequiresManual,
    Resolved,
    Failed,
    Abandoned
}

public sealed record MergeResolutionResult
{
    public bool Success { get; init; }
    public MergeResolutionStatus Status { get; init; }
    public List<string> ResolvedFiles { get; init; } = new();
    public List<string> UnresolvedFiles { get; init; } = new();
    public string ResolutionStrategy { get; init; } = "";
    public string? Error { get; init; }
    public string? SuggestedNextAction { get; init; }
}

public sealed class MergeConflictResolver
{
    private readonly GitWorktreeManager _worktreeManager;
    private readonly ILogger<MergeConflictResolver> _logger;
    private readonly ConcurrentDictionary<string, MergeConflictInfo> _activeConflicts = new();
    private const int MaxAutoAttempts = 3;

    public MergeConflictResolver(
        GitWorktreeManager worktreeManager,
        ILogger<MergeConflictResolver>? logger = null)
    {
        _worktreeManager = worktreeManager;
        _logger = logger ?? NullLogger<MergeConflictResolver>.Instance;
    }

    public IReadOnlyDictionary<string, MergeConflictInfo> ActiveConflicts =>
        new Dictionary<string, MergeConflictInfo>(_activeConflicts);

    public async Task<MergeConflictInfo?> DetectConflictsAsync(
        string sourceWorktreePath,
        string targetBranch = "main",
        CancellationToken ct = default)
    {
        try
        {
            var sourceBranch = await _worktreeManager.GetCurrentBranchAsync(sourceWorktreePath, ct)
                .ConfigureAwait(false);
            if (string.IsNullOrEmpty(sourceBranch)) return null;

            var conflictKey = $"{sourceBranch}->{targetBranch}";

            using var repo = OpenWorktreeRepo(sourceWorktreePath);
            if (repo == null) return null;

            FetchTargetBranch(repo, targetBranch);

            var targetCommit = ResolveTargetCommit(repo, targetBranch);
            if (targetCommit == null)
            {
                _logger.LogWarning("Target branch {Target} not found", targetBranch);
                return null;
            }

            var sourceCommit = repo.Head.Tip;
            if (sourceCommit == null || sourceCommit == targetCommit) return null;

            if (repo.ObjectDatabase.CanMergeWithoutConflict(sourceCommit, targetCommit))
            {
                _logger.LogDebug("No conflicts between {Source} and {Target}", sourceBranch, targetBranch);
                return null;
            }

            var conflictFiles = GetPotentialConflictFiles(repo, sourceCommit, targetCommit);

            var info = new MergeConflictInfo
            {
                SourceBranch = sourceBranch,
                TargetBranch = targetBranch,
                ConflictingFiles = conflictFiles,
                DiffOutput = $"Potential conflicts between {sourceBranch} and {targetBranch}"
            };

            _activeConflicts[conflictKey] = info;

            _logger.LogInformation("Detected {Count} conflicting files between {Source} and {Target}: {Files}",
                conflictFiles.Count, sourceBranch, targetBranch,
                string.Join(", ", conflictFiles));

            return info;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to detect conflicts for worktree {Path}", sourceWorktreePath);
            return null;
        }
    }

    public async Task<MergeResolutionResult> AttemptAutoResolveAsync(
        string sourceWorktreePath,
        string targetBranch = "main",
        CancellationToken ct = default)
    {
        var conflictInfo = await DetectConflictsAsync(sourceWorktreePath, targetBranch, ct)
            .ConfigureAwait(false);

        if (conflictInfo == null)
        {
            return new MergeResolutionResult
            {
                Success = true,
                Status = MergeResolutionStatus.Resolved,
                ResolutionStrategy = "no_conflict"
            };
        }

        if (conflictInfo.AttemptCount >= MaxAutoAttempts)
        {
            return new MergeResolutionResult
            {
                Success = false,
                Status = MergeResolutionStatus.RequiresManual,
                UnresolvedFiles = conflictInfo.ConflictingFiles,
                ResolutionStrategy = "exhausted_attempts",
                SuggestedNextAction = $"Manual resolution required for: {string.Join(", ", conflictInfo.ConflictingFiles)}"
            };
        }

        conflictInfo.Status = MergeResolutionStatus.Attempting;
        conflictInfo.AttemptCount++;

        try
        {
            using var repo = OpenWorktreeRepo(sourceWorktreePath);
            if (repo == null)
            {
                return new MergeResolutionResult
                {
                    Success = false,
                    Status = MergeResolutionStatus.Failed,
                    Error = "Could not open worktree repo",
                    ResolutionStrategy = "open_failed"
                };
            }

            FetchTargetBranch(repo, targetBranch);

            var targetCommit = ResolveTargetCommit(repo, targetBranch);
            if (targetCommit == null || repo.Head.Tip == null)
            {
                return new MergeResolutionResult
                {
                    Success = false,
                    Status = MergeResolutionStatus.Failed,
                    Error = $"Cannot resolve target branch {targetBranch}",
                    ResolutionStrategy = "target_missing"
                };
            }

            var sourceCommit = repo.Head.Tip;
            if (sourceCommit == targetCommit)
            {
                return new MergeResolutionResult
                {
                    Success = true,
                    Status = MergeResolutionStatus.Resolved,
                    ResolutionStrategy = "same_commit"
                };
            }

            var signature = repo.Config.BuildSignature(DateTimeOffset.UtcNow)
                ?? new Signature("LTAI Agent", "ltai@agent.local", DateTimeOffset.UtcNow);

            var mergeResult = repo.Merge(targetCommit, signature, new MergeOptions
            {
                FastForwardStrategy = FastForwardStrategy.Default,
                FileConflictStrategy = CheckoutFileConflictStrategy.Normal
            });

            if (mergeResult.Status == MergeStatus.FastForward)
            {
                conflictInfo.Status = MergeResolutionStatus.AutoResolved;
                return new MergeResolutionResult
                {
                    Success = true,
                    Status = MergeResolutionStatus.Resolved,
                    ResolutionStrategy = "fast_forward"
                };
            }

            if (mergeResult.Status == MergeStatus.NonFastForward)
            {
                var conflicts = repo.Index.Conflicts.ToList();
                if (conflicts.Count == 0)
                {
                    repo.Commit(
                        $"merge: auto-resolved {conflictInfo.SourceBranch} into {targetBranch}",
                        signature, signature);

                    conflictInfo.Status = MergeResolutionStatus.AutoResolved;
                    return new MergeResolutionResult
                    {
                        Success = true,
                        Status = MergeResolutionStatus.Resolved,
                        ResolutionStrategy = "auto_merge"
                    };
                }

                var resolvedFiles = new List<string>();
                var unresolvedFiles = new List<string>();

                foreach (var conflict in conflicts)
                {
                    var file = conflict.Ours?.Path ?? conflict.Theirs?.Path ?? "";
                    if (string.IsNullOrEmpty(file)) continue;

                    var resolved = TryResolveSingleFile(repo, file, conflict);
                    if (resolved)
                        resolvedFiles.Add(file);
                    else
                        unresolvedFiles.Add(file);
                }

                if (unresolvedFiles.Count == 0)
                {
                    conflictInfo.Status = MergeResolutionStatus.AutoResolved;
                    repo.Commit(
                        $"merge: resolved {string.Join(", ", resolvedFiles)} from {conflictInfo.SourceBranch}",
                        signature, signature);

                    return new MergeResolutionResult
                    {
                        Success = true,
                        Status = MergeResolutionStatus.Resolved,
                        ResolvedFiles = resolvedFiles,
                        ResolutionStrategy = "per_file_resolution"
                    };
                }

                conflictInfo.Status = MergeResolutionStatus.RequiresManual;
                return new MergeResolutionResult
                {
                    Success = false,
                    Status = MergeResolutionStatus.RequiresManual,
                    ResolvedFiles = resolvedFiles,
                    UnresolvedFiles = unresolvedFiles,
                    ResolutionStrategy = "partial_with_fallback",
                    SuggestedNextAction = $"Unresolved files: {string.Join(", ", unresolvedFiles)}. Manual resolution required."
                };
            }

            return new MergeResolutionResult
            {
                Success = true,
                Status = MergeResolutionStatus.Resolved,
                ResolutionStrategy = "clean_merge"
            };
        }
        catch (Exception ex)
        {
            conflictInfo.Status = MergeResolutionStatus.Failed;
            _logger.LogError(ex, "Auto-resolve failed for {Path}", sourceWorktreePath);

            return new MergeResolutionResult
            {
                Success = false,
                Status = MergeResolutionStatus.Failed,
                Error = ex.Message,
                ResolutionStrategy = "exception"
            };
        }
    }

    public Task<bool> AbandonConflictAsync(string sourceBranch, string targetBranch, CancellationToken ct = default)
    {
        var conflictKey = $"{sourceBranch}->{targetBranch}";
        if (_activeConflicts.TryRemove(conflictKey, out var info))
        {
            info.Status = MergeResolutionStatus.Abandoned;
            _logger.LogInformation("Abandoned merge conflict: {Source} -> {Target}", sourceBranch, targetBranch);
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    private static bool TryResolveSingleFile(Repository repo, string filePath, Conflict conflict)
    {
        try
        {
            var indexEntry = conflict.Theirs ?? conflict.Ours;
            if (indexEntry == null) return false;

            var blob = repo.Lookup<Blob>(indexEntry.Id);
            if (blob == null) return false;

            var fullPath = Path.Combine(repo.Info.WorkingDirectory, filePath);
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            using var stream = blob.GetContentStream();
            using var fileStream = File.Create(fullPath);
            stream.CopyTo(fileStream);

            repo.Index.Add(filePath);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static List<string> GetPotentialConflictFiles(Repository repo, Commit ours, Commit theirs)
    {
        var files = new HashSet<string>();

        try
        {
            var mergeBase = repo.ObjectDatabase.FindMergeBase(ours, theirs);
            var baseTree = mergeBase?.Tree;
            var oursTree = ours.Tree;
            var theirsTree = theirs.Tree;

            var theirsDiff = repo.Diff.Compare<TreeChanges>(
                baseTree ?? oursTree, theirsTree);
            var oursDiff = repo.Diff.Compare<TreeChanges>(
                baseTree ?? oursTree, oursTree);

            var changedInOurs = new HashSet<string>();
            foreach (var change in oursDiff)
                changedInOurs.Add(change.Path);

            foreach (var change in theirsDiff)
            {
                if (changedInOurs.Contains(change.Path))
                    files.Add(change.Path);
            }
        }
        catch
        {
        }

        return files.ToList();
    }

    private static void FetchTargetBranch(Repository repo, string targetBranch)
    {
        var remote = repo.Network.Remotes["origin"];
        if (remote == null) return;

        try
        {
            var refSpec = $"+refs/heads/{targetBranch}:refs/remotes/{remote.Name}/{targetBranch}";
            repo.Network.Fetch(remote.Name, new[] { refSpec }, new FetchOptions(), "LTAI MergeConflictResolver");
        }
        catch (Exception)
        {
        }
    }

    private static Commit? ResolveTargetCommit(Repository repo, string targetBranch)
    {
        var remote = repo.Network.Remotes["origin"];
        var remoteBranchName = remote != null ? $"{remote.Name}/{targetBranch}" : targetBranch;

        var targetRef = repo.Branches[remoteBranchName] ?? repo.Branches[targetBranch];

        if (targetRef == null)
        {
            try
            {
                return repo.Lookup<Commit>($"refs/remotes/{remoteBranchName}")
                    ?? repo.Lookup<Commit>($"refs/heads/{targetBranch}");
            }
            catch
            {
                return null;
            }
        }

        return targetRef.Tip;
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
