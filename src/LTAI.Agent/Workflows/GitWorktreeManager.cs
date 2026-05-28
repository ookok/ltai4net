using System.Collections.Concurrent;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Workflows;

public sealed record WorktreeInfo
{
    public string Path { get; init; } = "";
    public string Branch { get; init; } = "";
    public string Hash { get; init; } = "";
    public bool IsBare { get; init; }
    public bool IsLocked { get; init; }
    public bool IsDetached { get; init; }
}

public sealed record WorktreeCreateResult
{
    public bool Success { get; init; }
    public string WorktreePath { get; init; } = "";
    public string Branch { get; init; } = "";
    public string Niche { get; init; } = "";
    public string Error { get; init; } = "";
}

public sealed class GitWorktreeManager : IDisposable
{
    private readonly string _repoRoot;
    private readonly string _worktreesDir;
    private readonly ILogger<GitWorktreeManager> _logger;
    private readonly ConcurrentDictionary<string, DateTime> _worktreeTimestamps = new();
    private readonly Repository _repo;
    private readonly SemaphoreSlim _repoLock = new(1, 1);
    private const int MaxWorktrees = 50;
    private readonly string _timestampFile;

    public GitWorktreeManager(
        string repoRoot,
        string? worktreesDir = null,
        ILogger<GitWorktreeManager>? logger = null)
    {
        _repoRoot = Path.GetFullPath(repoRoot);
        _worktreesDir = Path.GetFullPath(
            worktreesDir ?? Path.Combine(_repoRoot, ".livingtree", "worktrees"));
        _timestampFile = Path.Combine(_worktreesDir, ".timestamps.json");
        _logger = logger ?? NullLogger<GitWorktreeManager>.Instance;
        _repo = new Repository(_repoRoot);

        Directory.CreateDirectory(_worktreesDir);

        // Restore persisted timestamps (survive process restart)
        LoadPersistedTimestamps();
    }

    public string GetWorktreesDir() => _worktreesDir;

    public async Task<WorktreeCreateResult> CreateWorktreeAsync(
        string agentId,
        string baseBranch = "main",
        string? niche = null,
        CancellationToken ct = default)
    {
        var timestamp = DateTime.UtcNow;

        // Enforce max worktrees limit — prevent unbounded disk growth
        if (_worktreeTimestamps.Count >= MaxWorktrees)
        {
            var oldest = _worktreeTimestamps.OrderBy(kv => kv.Value).First();
            _logger.LogWarning("Max worktrees ({Max}) reached — evicting oldest: {Branch}",
                MaxWorktrees, oldest.Key);
            // Find and remove the oldest worktree by path
            var oldestPath = Path.Combine(_worktreesDir, oldest.Key.Replace('/', '_'));
            if (Directory.Exists(oldestPath))
                await RemoveWorktreeAsync(oldestPath, force: true, ct).ConfigureAwait(false);
            else
                _worktreeTimestamps.TryRemove(oldest.Key, out _);
        }

        var branchName = $"worktree/{agentId}/{timestamp:yyyyMMdd-HHmmss}";
        var worktreePath = Path.Combine(_worktreesDir, branchName.Replace('/', '_'));

        try
        {
            if (Directory.Exists(worktreePath))
            {
                _logger.LogWarning("Worktree path {Path} already exists, removing", worktreePath);
                Directory.Delete(worktreePath, true);
            }

            await _repoLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var baseBranchRef = _repo.Branches[baseBranch];
                if (baseBranchRef == null)
                {
                    return new WorktreeCreateResult
                    {
                        Success = false,
                        Niche = niche ?? "",
                        Error = $"Base branch '{baseBranch}' not found. Available: {string.Join(", ", _repo.Branches.Select(b => b.FriendlyName))}"
                    };
                }

                var branch = _repo.CreateBranch(branchName, baseBranchRef.Tip);
                _repo.Worktrees.Add(branchName, worktreePath, branch.CanonicalName, false);
            }
            finally
            {
                _repoLock.Release();
            }

            _worktreeTimestamps[branchName] = timestamp;
            PersistTimestamps();

            _logger.LogInformation("Created worktree for agent {Agent}: branch={Branch}, path={Path}",
                agentId, branchName, worktreePath);

            return new WorktreeCreateResult
            {
                Success = true,
                WorktreePath = worktreePath,
                Branch = branchName,
                Niche = niche ?? ""
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create worktree for agent {Agent}", agentId);
            return new WorktreeCreateResult { Success = false, Niche = niche ?? "", Error = ex.Message };
        }
    }

    public List<WorktreeInfo> ListWorktrees()
    {
        var worktrees = new List<WorktreeInfo>();

        foreach (var wt in _repo.Worktrees)
        {
            var worktreePath = ReadWorktreePath(wt.Name) ?? "";
            var wtRepoPath = GetWorktreeRepoPath(worktreePath);
            string hash = "";
            string branch = "";
            bool isDetached = false;

            if (!string.IsNullOrEmpty(wtRepoPath) && Directory.Exists(wtRepoPath))
            {
                try
                {
                    using var wtRepo = new Repository(wtRepoPath);
                    hash = wtRepo.Head.Tip?.Sha ?? "";
                    branch = wtRepo.Head.FriendlyName ?? "";
                    isDetached = wtRepo.Info.IsHeadDetached;
                }
                catch
                {
                }
            }

            worktrees.Add(new WorktreeInfo
            {
                Path = worktreePath,
                Branch = string.IsNullOrEmpty(branch) ? wt.Name : branch,
                Hash = hash,
                IsBare = false,
                IsLocked = wt.IsLocked,
                IsDetached = isDetached
            });
        }

        return worktrees;
    }

    public Task<List<WorktreeInfo>> ListWorktreesAsync(CancellationToken ct = default)
    {
        return Task.FromResult(ListWorktrees());
    }

    public async Task<bool> RemoveWorktreeAsync(string worktreePath, bool force = false, CancellationToken ct = default)
    {
        try
        {
            var normalizedPath = Path.GetFullPath(worktreePath);
            var matchingName = _repo.Worktrees
                .Select(wt => wt.Name)
                .FirstOrDefault(name =>
                {
                    var path = ReadWorktreePath(name);
                    return path != null && string.Equals(
                        Path.GetFullPath(path), normalizedPath,
                        StringComparison.OrdinalIgnoreCase);
                });

            if (matchingName == null)
            {
                _logger.LogWarning("No git worktree metadata found for {Path}, removing directory directly", worktreePath);
                if (Directory.Exists(worktreePath))
                    Directory.Delete(worktreePath, true);
                return true;
            }

            var matchingWorktree = _repo.Worktrees.FirstOrDefault(wt => wt.Name == matchingName);
            if (matchingWorktree != null && matchingWorktree.IsLocked && !force)
            {
                _logger.LogWarning("Worktree at {Path} is locked, use force to remove", worktreePath);
                return false;
            }

            if (Directory.Exists(worktreePath))
                Directory.Delete(worktreePath, true);

            var wtAdminDir = Path.Combine(_repo.Info.Path, "worktrees", matchingName);
            if (Directory.Exists(wtAdminDir))
                Directory.Delete(wtAdminDir, true);

            _logger.LogInformation("Removed worktree at {Path}", worktreePath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove worktree at {Path}", worktreePath);
            return false;
        }
    }

    public Task<bool> IsWorktreeLockedAsync(string worktreePath, CancellationToken ct = default)
    {
        var normalizedPath = Path.GetFullPath(worktreePath);
        var wt = _repo.Worktrees.FirstOrDefault(w =>
        {
            var path = ReadWorktreePath(w.Name);
            return path != null && string.Equals(
                Path.GetFullPath(path), normalizedPath, StringComparison.OrdinalIgnoreCase);
        });
        return Task.FromResult(wt?.IsLocked ?? false);
    }

    public async Task<bool> CommitAndPushAsync(
        string worktreePath,
        string message,
        string? remote = "origin",
        CancellationToken ct = default)
    {
        try
        {
            var wtRepoPath = GetWorktreeRepoPath(worktreePath);
            if (string.IsNullOrEmpty(wtRepoPath) || !Directory.Exists(wtRepoPath))
            {
                _logger.LogError("Worktree git dir not found at {Path}", wtRepoPath);
                return false;
            }

            using var wtRepo = new Repository(wtRepoPath);

            var status = wtRepo.RetrieveStatus();
            if (!status.IsDirty)
            {
                _logger.LogInformation("No changes to commit in worktree {Path}", worktreePath);
                return false;
            }

            Commands.Stage(wtRepo, "*");

            var author = wtRepo.Config.BuildSignature(DateTimeOffset.UtcNow);
            if (author == null)
            {
                author = new Signature("LTAI Agent", "ltai@agent.local", DateTimeOffset.UtcNow);
            }

            wtRepo.Commit(message, author, author);

            if (!string.IsNullOrEmpty(remote))
            {
                var rem = wtRepo.Network.Remotes[remote];
                if (rem != null && !wtRepo.Head.IsRemote)
                {
                    wtRepo.Network.Push(rem, wtRepo.Head.CanonicalName, new PushOptions());
                }
            }

            return true;
        }
        catch (EmptyCommitException)
        {
            return true;
        }
        catch (NonFastForwardException ex)
        {
            _logger.LogWarning(ex, "Push rejected: non-fast-forward for {Path}", worktreePath);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to commit/push worktree {Path}", worktreePath);
            return false;
        }
    }

    public Task<string> GetCurrentBranchAsync(string worktreePath, CancellationToken ct = default)
    {
        var wtRepoPath = GetWorktreeRepoPath(worktreePath);
        if (string.IsNullOrEmpty(wtRepoPath) || !Directory.Exists(wtRepoPath))
            return Task.FromResult("");

        try
        {
            using var wtRepo = new Repository(wtRepoPath);
            return Task.FromResult(wtRepo.Head.FriendlyName ?? "");
        }
        catch
        {
            return Task.FromResult("");
        }
    }

    public async Task<bool> BranchExistsAsync(string branchName, CancellationToken ct = default)
    {
        await _repoLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _repo.Branches[branchName] != null;
        }
        finally
        {
            _repoLock.Release();
        }
    }

    public async Task<List<string>> ListStaleBranchesAsync(TimeSpan maxAge, CancellationToken ct = default)
    {
        var stale = new List<string>();
        var cutoff = DateTime.UtcNow - maxAge;

        var worktrees = await ListWorktreesAsync(ct).ConfigureAwait(false);
        foreach (var wt in worktrees)
        {
            if (string.IsNullOrEmpty(wt.Branch) || wt.Branch == "main" || wt.Branch == "master")
                continue;

            if (_worktreeTimestamps.TryGetValue(wt.Branch, out var ts) && ts < cutoff)
            {
                stale.Add(wt.Branch);
            }
        }

        return stale;
    }

    public Task<List<string>> GetModifiedFilesAsync(string worktreePath, CancellationToken ct = default)
    {
        var wtRepoPath = GetWorktreeRepoPath(worktreePath);
        if (string.IsNullOrEmpty(wtRepoPath) || !Directory.Exists(wtRepoPath))
            return Task.FromResult(new List<string>());

        try
        {
            using var wtRepo = new Repository(wtRepoPath);
            var status = wtRepo.RetrieveStatus();
            return Task.FromResult(status.Modified
                .Select(e => e.FilePath)
                .Concat(status.Added.Select(e => e.FilePath))
                .Distinct()
                .ToList());
        }
        catch
        {
            return Task.FromResult(new List<string>());
        }
    }

    public Task<string> GetDiffAsync(string worktreePath, CancellationToken ct = default)
    {
        var wtRepoPath = GetWorktreeRepoPath(worktreePath);
        if (string.IsNullOrEmpty(wtRepoPath) || !Directory.Exists(wtRepoPath))
            return Task.FromResult("");

        try
        {
            using var wtRepo = new Repository(wtRepoPath);
            var patch = wtRepo.Diff.Compare<Patch>();
            return Task.FromResult(patch?.Content ?? "");
        }
        catch
        {
            return Task.FromResult("");
        }
    }

    public void Touch(string branchName)
    {
        _worktreeTimestamps[branchName] = DateTime.UtcNow;
        PersistTimestamps();
    }

    public void Dispose()
    {
        PersistTimestamps();
        _repoLock.Dispose();
        _repo.Dispose();
    }

    // ========================================================================
    // Timestamp persistence — survives process restart
    // ========================================================================
    private void LoadPersistedTimestamps()
    {
        try
        {
            if (File.Exists(_timestampFile))
            {
                var json = File.ReadAllText(_timestampFile);
                var entries = System.Text.Json.JsonSerializer
                    .Deserialize<Dictionary<string, DateTime>>(json);
                if (entries != null)
                {
                    foreach (var (branch, time) in entries)
                        _worktreeTimestamps[branch] = time;
                    _logger.LogDebug("Loaded {Count} persisted worktree timestamps", entries.Count);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load persisted worktree timestamps");
        }
    }

    private void PersistTimestamps()
    {
        try
        {
            var dict = _worktreeTimestamps.ToDictionary(kv => kv.Key, kv => kv.Value);
            var json = System.Text.Json.JsonSerializer.Serialize(dict);
            File.WriteAllText(_timestampFile, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist worktree timestamps");
        }
    }

    private string? ReadWorktreePath(string worktreeName)
    {
        var gitdirFile = Path.Combine(_repo.Info.Path, "worktrees", worktreeName, "gitdir");
        if (!File.Exists(gitdirFile)) return null;

        try
        {
            var content = File.ReadAllText(gitdirFile).Trim();
            var gitDirPath = content;
            return Path.GetDirectoryName(gitDirPath);
        }
        catch
        {
            return null;
        }
    }

    private static string? GetWorktreeRepoPath(string worktreePath)
    {
        var gitFile = Path.Combine(worktreePath, ".git");
        if (File.Exists(gitFile))
        {
            var content = File.ReadAllText(gitFile).Trim();
            if (content.StartsWith("gitdir: ", StringComparison.Ordinal))
            {
                return content["gitdir: ".Length..].Trim();
            }
        }

        var gitDir = Path.Combine(worktreePath, ".git");
        if (Directory.Exists(gitDir))
            return gitDir;

        return null;
    }
}
