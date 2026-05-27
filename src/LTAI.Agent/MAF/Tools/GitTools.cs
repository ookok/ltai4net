using System.ComponentModel;
using System.Text.Json;
using LibGit2Sharp;

namespace LTAI.Agent.Tools;

[Description("Git repository operations powered by libgit2sharp — zero CLI dependency")]
public sealed class GitTools
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private static Repository OpenRepo(string? repoPath)
    {
        if (!string.IsNullOrWhiteSpace(repoPath))
        {
            if (Repository.IsValid(repoPath))
                return new Repository(repoPath);

            var gitFile = Path.Combine(repoPath, ".git");
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
        }

        var cwd = Directory.GetCurrentDirectory();
        if (Repository.IsValid(cwd))
            return new Repository(cwd);

        var discovered = Repository.Discover(cwd);
        if (!string.IsNullOrEmpty(discovered))
            return new Repository(discovered);

        throw new InvalidOperationException($"No git repository found at {(repoPath ?? cwd)}");
    }

    [Description("Show git diff for the current repository. Returns changed files and their diffs.")]
    public static async Task<string> GitDiff(
        [Description("Path to the git repository")] string? repoPath = null,
        [Description("Files to diff, space-separated or empty for all")] string? files = null,
        [Description("Use --staged for staged changes")] bool staged = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var repo = OpenRepo(repoPath);
            var fileList = string.IsNullOrWhiteSpace(files)
                ? null
                : files.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (staged)
            {
                var stagedPatch = repo.Diff.Compare<Patch>(repo.Head.Tip?.Tree, DiffTargets.Index);
                return await Task.FromResult(stagedPatch?.Content ?? "(no staged changes)");
            }

            var workingPatch = repo.Diff.Compare<Patch>();
            var entries = repo.Diff.Compare<TreeChanges>()
                .Where(c => fileList == null || fileList.Contains(c.Path))
                .ToList();

            return await Task.FromResult(JsonSerializer.Serialize(new
            {
                count = entries.Count,
                patch = workingPatch?.Content.Length > 5000 ? workingPatch.Content[..5000] + "\n... (truncated)" : workingPatch?.Content,
                changes = entries.Select(c => new
                {
                    path = c.Path,
                    oldPath = c.OldPath,
                    status = c.Status.ToString()
                })
            }, JsonOpts));
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    [Description("Show git commit log. Returns recent commits with hash, author, date, and message.")]
    public static async Task<string> GitLog(
        [Description("Path to the git repository")] string? repoPath = null,
        [Description("Max number of commits")] int maxCount = 20,
        [Description("Format: oneline, short, medium, full")] string format = "oneline",
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var repo = OpenRepo(repoPath);
            var commits = repo.Commits.Take(maxCount).Select(c => new
            {
                hash = c.Sha[..8],
                fullHash = c.Sha,
                author = c.Author.Name,
                email = c.Author.Email,
                date = c.Author.When.ToString("yyyy-MM-dd"),
                message = c.MessageShort,
                parents = c.Parents.Select(p => p.Sha[..8]).ToList()
            }).ToList();

            return await Task.FromResult(JsonSerializer.Serialize(new
            {
                repoPath,
                format,
                count = commits.Count,
                currentBranch = repo.Head.FriendlyName,
                commits
            }, JsonOpts));
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    [Description("Show git blame for a file with full author/commit attribution.")]
    public static async Task<string> GitBlame(
        [Description("Path to the file relative to repo root")] string filePath,
        [Description("Path to the git repository")] string? repoPath = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var repo = OpenRepo(repoPath);
            var blame = repo.Blame(filePath);
            var hunks = blame.Select(h => new
            {
                lineNumber = h.FinalStartLineNumber,
                lineCount = h.LineCount,
                commitSha = h.FinalCommit.Sha[..8],
                author = h.FinalCommit.Author.Name,
                date = h.FinalCommit.Author.When.ToString("yyyy-MM-dd"),
                summary = h.FinalCommit.MessageShort
            }).ToList();

            return await Task.FromResult(JsonSerializer.Serialize(new
            {
                filePath,
                totalHunks = hunks.Count,
                blame = hunks.Take(200)
            }, JsonOpts));
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    [Description("Show git working tree status.")]
    public static async Task<string> GitStatus(
        [Description("Path to the git repository")] string? repoPath = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var repo = OpenRepo(repoPath);
            var status = repo.RetrieveStatus();
            return await Task.FromResult(JsonSerializer.Serialize(new
            {
                branch = repo.Head.FriendlyName,
                isDetached = repo.Info.IsHeadDetached,
                isDirty = status.IsDirty,
                added = status.Added.Select(e => e.FilePath).ToList(),
                modified = status.Modified.Select(e => e.FilePath).ToList(),
                deleted = status.Removed.Select(e => e.FilePath).ToList(),
                untracked = status.Untracked.Select(e => e.FilePath).ToList(),
                staged = status.Staged.Select(e => e.FilePath).ToList(),
                renamed = status.RenamedInIndex.Select(e => $"{e.FilePath} (was {e.HeadToIndexRenameDetails?.OldFilePath ?? "?"})").ToList()
            }, JsonOpts));
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    [Description("List, create, or delete git branches.")]
    public static async Task<string> GitBranch(
        [Description("Operation: list, create, delete")] string operation = "list",
        [Description("Branch name for create/delete")] string? name = null,
        [Description("Base branch for create (default: current)")] string? baseBranch = null,
        [Description("Path to the git repository")] string? repoPath = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var repo = OpenRepo(repoPath);

            switch (operation.ToLowerInvariant())
            {
                case "create":
                    if (string.IsNullOrWhiteSpace(name))
                        return JsonSerializer.Serialize(new { error = "name is required for create" });
                    var baseRef = baseBranch != null ? repo.Branches[baseBranch] : repo.Head;
                    if (baseRef == null)
                        return JsonSerializer.Serialize(new { error = $"Base {baseBranch ?? "HEAD"} not found" });
                    repo.CreateBranch(name, baseRef.Tip);
                    return JsonSerializer.Serialize(new { created = name, from = baseRef.FriendlyName });

                case "delete":
                    if (string.IsNullOrWhiteSpace(name))
                        return JsonSerializer.Serialize(new { error = "name is required for delete" });
                    var branchToDelete = repo.Branches[name];
                    if (branchToDelete == null)
                        return JsonSerializer.Serialize(new { error = $"Branch '{name}' not found" });
                    if (branchToDelete.IsCurrentRepositoryHead)
                        return JsonSerializer.Serialize(new { error = "Cannot delete current branch" });
                    repo.Branches.Remove(branchToDelete);
                    return JsonSerializer.Serialize(new { deleted = name });

                default:
                    var branches = repo.Branches.Select(b => new
                    {
                        name = b.FriendlyName,
                        isRemote = b.IsRemote,
                        isCurrent = b.IsCurrentRepositoryHead,
                        tip = b.Tip?.Sha[..8],
                        tracked = b.TrackedBranch?.FriendlyName,
                        aheadBy = b.IsTracking ? b.TrackingDetails.AheadBy : (int?)null,
                        behindBy = b.IsTracking ? b.TrackingDetails.BehindBy : (int?)null
                    }).ToList();
                    return JsonSerializer.Serialize(new { current = repo.Head.FriendlyName, branches });
            }
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    [Description("Manage git stash — push, pop, list, apply, drop.")]
    public static async Task<string> GitStash(
        [Description("Operation: push, pop, list, apply, drop")] string operation = "push",
        [Description("Stash message for push")] string? message = null,
        [Description("Stash index for pop/apply/drop")] int index = 0,
        [Description("Path to the git repository")] string? repoPath = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var repo = OpenRepo(repoPath);
            var signature = repo.Config.BuildSignature(DateTimeOffset.UtcNow)
                ?? new Signature("LTAI Agent", "ltai@agent.local", DateTimeOffset.UtcNow);

            switch (operation.ToLowerInvariant())
            {
                case "push":
                    var stashMsg = message ?? $"LTAI stash {DateTime.UtcNow:yyyyMMdd-HHmmss}";
                    var stash = repo.Stashes.Add(signature, stashMsg, StashModifiers.Default);
                    return JsonSerializer.Serialize(new
                    {
                        stashed = true,
                        index = stash.Index,
                        message = stashMsg
                    });

                case "pop":
                    repo.Stashes.Pop(index, new StashApplyOptions());
                    return JsonSerializer.Serialize(new { popped = true, index });

                case "list":
                    var stashes = repo.Stashes.Select(s => new
                    {
                        index = s.Index,
                        message = s.Message
                    }).ToList();
                    return JsonSerializer.Serialize(new { count = stashes.Count, stashes });

                case "apply":
                    repo.Stashes.Apply(index, new StashApplyOptions());
                    return JsonSerializer.Serialize(new { applied = true, index });

                case "drop":
                    repo.Stashes.Remove(index);
                    return JsonSerializer.Serialize(new { dropped = true, index });

                default:
                    return JsonSerializer.Serialize(new { error = $"Unknown operation: {operation}. Use push/pop/list/apply/drop" });
            }
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    [Description("Create a git commit with all staged changes.")]
    public static async Task<string> GitCommit(
        [Description("Commit message")] string message,
        [Description("Stage all changes before commit")] bool stageAll = true,
        [Description("Path to the git repository")] string? repoPath = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var repo = OpenRepo(repoPath);
            var signature = repo.Config.BuildSignature(DateTimeOffset.UtcNow)
                ?? new Signature("LTAI Agent", "ltai@agent.local", DateTimeOffset.UtcNow);

            if (stageAll)
                Commands.Stage(repo, "*");

            var status = repo.RetrieveStatus();
            if (!status.IsDirty && !status.Staged.Any())
                return JsonSerializer.Serialize(new { error = "Nothing to commit", status = "clean" });

            var commit = repo.Commit(message, signature, signature);
            return JsonSerializer.Serialize(new
            {
                committed = true,
                sha = commit.Sha[..8],
                message
            });
        }
        catch (EmptyCommitException)
        {
            return JsonSerializer.Serialize(new { committed = false, error = "Nothing to commit" });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    [Description("Fetch and optionally merge from a remote.")]
    public static async Task<string> GitPull(
        [Description("Remote name")] string remote = "origin",
        [Description("Branch name")] string? branch = null,
        [Description("Path to the git repository")] string? repoPath = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var repo = OpenRepo(repoPath);
            var rem = repo.Network.Remotes[remote];
            if (rem == null)
                return JsonSerializer.Serialize(new { error = $"Remote '{remote}' not found" });

            var refSpecs = branch != null
                ? new[] { $"+refs/heads/{branch}:refs/remotes/{remote}/{branch}" }
                : new[] { $"+refs/heads/*:refs/remotes/{remote}/*" };

            repo.Network.Fetch(remote, refSpecs, new FetchOptions(), "LTAI GitPull");

            var signature = repo.Config.BuildSignature(DateTimeOffset.UtcNow)
                ?? new Signature("LTAI Agent", "ltai@agent.local", DateTimeOffset.UtcNow);

            var trackingBranch = branch != null
                ? repo.Branches[$"{remote}/{branch}"]
                : repo.Head.TrackedBranch;

            if (trackingBranch != null && trackingBranch.Tip != repo.Head.Tip)
            {
                var mergeResult = repo.Merge(trackingBranch, signature, new MergeOptions
                {
                    FastForwardStrategy = FastForwardStrategy.FastForwardOnly
                });

                return JsonSerializer.Serialize(new
                {
                    fetched = true,
                    merged = true,
                    status = mergeResult.Status.ToString(),
                    commit = mergeResult.Commit?.Sha[..8]
                });
            }

            return JsonSerializer.Serialize(new { fetched = true, merged = false, status = "up_to_date" });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    [Description("Reset HEAD to a specified state.")]
    public static async Task<string> GitReset(
        [Description("Mode: soft, mixed, hard")] string mode = "mixed",
        [Description("Target commit SHA or HEAD~N")] string? target = "HEAD",
        [Description("File path for file-level reset")] string? filePath = null,
        [Description("Path to the git repository")] string? repoPath = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var repo = OpenRepo(repoPath);
            var resetMode = mode.ToLowerInvariant() switch
            {
                "soft" => ResetMode.Soft,
                "hard" => ResetMode.Hard,
                _ => ResetMode.Mixed
            };

            if (!string.IsNullOrWhiteSpace(filePath))
            {
                repo.Reset(ResetMode.Mixed, repo.Lookup<Commit>(target ?? "HEAD")
                    ?? throw new InvalidOperationException($"Target '{target}' not found"),
                    new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force });
                repo.CheckoutPaths("HEAD", new[] { filePath });
                return JsonSerializer.Serialize(new { reset = true, file = filePath, mode });
            }

            var commit = repo.Lookup<Commit>(target ?? "HEAD")
                ?? throw new InvalidOperationException($"Target '{target}' not found");
            repo.Reset(resetMode, commit);
            return JsonSerializer.Serialize(new { reset = true, target = target ?? "HEAD", mode });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    [Description("Manage git tags: create, list, or delete lightweight/annotated tags.")]
    public static async Task<string> GitTag(
        [Description("Operation: create, list, delete")] string operation = "list",
        [Description("Tag name")] string? name = null,
        [Description("Tag message for annotated tag")] string? message = null,
        [Description("Path to the git repository")] string? repoPath = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var repo = OpenRepo(repoPath);

            switch (operation.ToLowerInvariant())
            {
                case "create":
                    if (string.IsNullOrWhiteSpace(name))
                        return JsonSerializer.Serialize(new { error = "name is required for create" });
                    var signature = repo.Config.BuildSignature(DateTimeOffset.UtcNow)
                        ?? new Signature("LTAI Agent", "ltai@agent.local", DateTimeOffset.UtcNow);
                    var tag = repo.ApplyTag(name, signature, message ?? name);
                    return JsonSerializer.Serialize(new { created = true, tag = name, sha = tag.Target.Sha[..8], annotated = !string.IsNullOrWhiteSpace(message) });

                case "delete":
                    if (string.IsNullOrWhiteSpace(name))
                        return JsonSerializer.Serialize(new { error = "name is required for delete" });
                    repo.Tags.Remove(name);
                    return JsonSerializer.Serialize(new { deleted = true, tag = name });

                default:
                    var tags = repo.Tags.Select(t => new
                    {
                        name = t.FriendlyName,
                        sha = t.Target?.Sha[..8],
                        isAnnotated = t.IsAnnotated,
                        annotation = t.Annotation?.Message
                    }).ToList();
                    return JsonSerializer.Serialize(new { count = tags.Count, tags });
            }
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    [Description("Show commit details: full diff, tree, parents.")]
    public static async Task<string> GitShow(
        [Description("Commit SHA or reference")] string target = "HEAD",
        [Description("Path to the git repository")] string? repoPath = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var repo = OpenRepo(repoPath);
            var commit = repo.Lookup<Commit>(target)
                ?? repo.Lookup<Commit>(repo.Refs[target]?.TargetIdentifier ?? "")
                ?? throw new InvalidOperationException($"Target '{target}' not found");

            var commitPatch = repo.Diff.Compare<Patch>(
                commit.Parents.FirstOrDefault()?.Tree,
                commit.Tree);

            return await Task.FromResult(JsonSerializer.Serialize(new
            {
                sha = commit.Sha,
                author = new { commit.Author.Name, commit.Author.Email, date = commit.Author.When.ToString("O") },
                committer = new { commit.Committer.Name, commit.Committer.Email, date = commit.Committer.When.ToString("O") },
                message = commit.Message,
                messageShort = commit.MessageShort,
                parents = commit.Parents.Select(p => p.Sha[..8]).ToList(),
                filesChanged = commitPatch?.Count() ?? 0,
                diff = commitPatch?.Content.Length > 10000
                    ? commitPatch.Content[..10000] + "\n... (truncated)"
                    : commitPatch?.Content
            }, JsonOpts));
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    [Description("Find the best common ancestor(s) between two commits (merge base).")]
    public static async Task<string> GitMergeBase(
        [Description("First commit")] string commitA = "HEAD",
        [Description("Second commit")] string commitB = "main",
        [Description("Path to the git repository")] string? repoPath = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var repo = OpenRepo(repoPath);
            var a = repo.Lookup<Commit>(commitA) ?? throw new InvalidOperationException($"'{commitA}' not found");
            var b = repo.Lookup<Commit>(commitB) ?? throw new InvalidOperationException($"'{commitB}' not found");
            var mergeBase = repo.ObjectDatabase.FindMergeBase(a, b);
            return await Task.FromResult(JsonSerializer.Serialize(new
            {
                commitA = a.Sha[..8],
                commitB = b.Sha[..8],
                mergeBase = mergeBase?.Sha[..8],
                canMergeWithoutConflict = repo.ObjectDatabase.CanMergeWithoutConflict(a, b)
            }, JsonOpts));
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    [Description("Apply a commit from another branch onto current branch (cherry-pick).")]
    public static async Task<string> GitCherryPick(
        [Description("Commit SHA to cherry-pick")] string commitSha,
        [Description("Path to the git repository")] string? repoPath = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var repo = OpenRepo(repoPath);
            var commit = repo.Lookup<Commit>(commitSha)
                ?? throw new InvalidOperationException($"Commit '{commitSha}' not found");

            var signature = repo.Config.BuildSignature(DateTimeOffset.UtcNow)
                ?? new Signature("LTAI Agent", "ltai@agent.local", DateTimeOffset.UtcNow);

            var result = repo.CherryPick(commit, signature, new CherryPickOptions());
            return await Task.FromResult(JsonSerializer.Serialize(new
            {
                cherryPicked = true,
                fromSha = commitSha[..8],
                status = result.Status.ToString(),
                conflicts = repo.Index.Conflicts.Count()
            }, JsonOpts));
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    [Description("Revert an existing commit by creating a new inverse commit.")]
    public static async Task<string> GitRevert(
        [Description("Commit SHA to revert")] string commitSha,
        [Description("Path to the git repository")] string? repoPath = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var repo = OpenRepo(repoPath);
            var commit = repo.Lookup<Commit>(commitSha)
                ?? throw new InvalidOperationException($"Commit '{commitSha}' not found");

            var signature = repo.Config.BuildSignature(DateTimeOffset.UtcNow)
                ?? new Signature("LTAI Agent", "ltai@agent.local", DateTimeOffset.UtcNow);

            var result = repo.Revert(commit, signature, new RevertOptions());
            return await Task.FromResult(JsonSerializer.Serialize(new
            {
                reverted = true,
                originalSha = commitSha[..8],
                status = result.Status.ToString()
            }, JsonOpts));
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    [Description("Archive a git commit/tag/branch as a tar or zip stream. Returns base64-encoded bytes.")]
    public static async Task<string> GitArchive(
        [Description("Tree-ish: commit, tag, or branch")] string treeIsh = "HEAD",
        [Description("Export format: zip (default)")] string format = "zip",
        [Description("Path to the git repository")] string? repoPath = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var repo = OpenRepo(repoPath);
            var commit = repo.Lookup<Commit>(treeIsh)
                ?? repo.Lookup<Commit>(repo.Refs[treeIsh]?.TargetIdentifier ?? "")
                ?? throw new InvalidOperationException($"Tree-ish '{treeIsh}' not found");

            var tempArchive = Path.GetTempFileName() + ".tar";
            repo.ObjectDatabase.Archive(commit.Tree, tempArchive);
            var bytes = await File.ReadAllBytesAsync(tempArchive, cancellationToken);
            try { File.Delete(tempArchive); } catch { }
            return await Task.FromResult(JsonSerializer.Serialize(new
            {
                archived = true,
                treeIsh = treeIsh,
                sha = commit.Sha[..8],
                format,
                sizeBytes = bytes.Length,
                base64 = Convert.ToBase64String(bytes)
            }, JsonOpts));
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    [Description("Export commits modified between two snapshots as structured changelist.")]
    public static async Task<string> GitDiffCommits(
        [Description("From commit/tag")] string from = "HEAD~1",
        [Description("To commit/tag")] string to = "HEAD",
        [Description("Path to the git repository")] string? repoPath = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var repo = OpenRepo(repoPath);
            var fromCommit = repo.Lookup<Commit>(from) ?? throw new InvalidOperationException($"'{from}' not found");
            var toCommit = repo.Lookup<Commit>(to) ?? throw new InvalidOperationException($"'{to}' not found");

            var patch = repo.Diff.Compare<Patch>(fromCommit.Tree, toCommit.Tree);
            var changes = repo.Diff.Compare<TreeChanges>(fromCommit.Tree, toCommit.Tree);

            return await Task.FromResult(JsonSerializer.Serialize(new
            {
                from = fromCommit.Sha[..8],
                to = toCommit.Sha[..8],
                totalFiles = changes.Count,
                files = changes.Select(c => new
                {
                    path = c.Path,
                    oldPath = c.OldPath,
                    status = c.Status.ToString()
                }).ToList(),
                diff = patch?.Content.Length > 15000
                    ? patch.Content[..15000] + "\n... (truncated)"
                    : patch?.Content
            }, JsonOpts));
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    [Description("Show info about a remote repository.")]
    public static async Task<string> GitRemote(
        [Description("Operation: list, show, add")] string operation = "list",
        [Description("Remote name")] string? name = null,
        [Description("Remote URL for add")] string? url = null,
        [Description("Path to the git repository")] string? repoPath = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var repo = OpenRepo(repoPath);

            switch (operation.ToLowerInvariant())
            {
                case "add":
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
                        return JsonSerializer.Serialize(new { error = "name and url are required for add" });
                    repo.Network.Remotes.Add(name, url);
                    return JsonSerializer.Serialize(new { added = true, name, url });

                case "show":
                    if (string.IsNullOrWhiteSpace(name))
                        return JsonSerializer.Serialize(new { error = "name is required for show" });
                    var rem = repo.Network.Remotes[name];
                    if (rem == null)
                        return JsonSerializer.Serialize(new { error = $"Remote '{name}' not found" });
                    return JsonSerializer.Serialize(new
                    {
                        name = rem.Name,
                        url = rem.Url,
                        pushUrl = rem.PushUrl,
                        fetchRefSpecs = rem.FetchRefSpecs.Select(r => r.Specification).ToList(),
                        pushRefSpecs = rem.PushRefSpecs.Select(r => r.Specification).ToList()
                    });

                default:
                    var remotes = repo.Network.Remotes.Select(r => new
                    {
                        name = r.Name,
                        url = r.Url,
                        pushUrl = r.PushUrl
                    }).ToList();
                    return JsonSerializer.Serialize(new { remotes });
            }
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    [Description("Checkout a branch or restore file(s).")]
    public static async Task<string> GitCheckout(
        [Description("Branch name or file path(s)")] string target,
        [Description("Path to the git repository")] string? repoPath = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var repo = OpenRepo(repoPath);

            var branch = repo.Branches[target];
            if (branch != null)
            {
                Commands.Checkout(repo, branch);
                return await Task.FromResult(JsonSerializer.Serialize(new
                {
                    checkout = true,
                    type = "branch",
                    branch = target,
                    head = repo.Head.Tip?.Sha[..8]
                }, JsonOpts));
            }

            Commands.Checkout(repo, target);
            return await Task.FromResult(JsonSerializer.Serialize(new
            {
                checkout = true,
                type = "file",
                path = target
            }, JsonOpts));
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    [Description("Clone a remote git repository to a local directory.")]
    public static async Task<string> GitClone(
        [Description("Remote URL to clone from")] string url,
        [Description("Local target directory")] string? targetDir = null,
        [Description("Branch to checkout")] string? branch = null,
        [Description("Shallow clone (--depth 1)")] bool shallow = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var repoName = Path.GetFileNameWithoutExtension(
                url.TrimEnd('/').EndsWith(".git", StringComparison.OrdinalIgnoreCase)
                    ? url.TrimEnd('/')[..^4].Split('/').Last()
                    : url.TrimEnd('/').Split('/').Last());
            var localPath = targetDir ?? Path.Combine(Directory.GetCurrentDirectory(), repoName);

            if (Directory.Exists(localPath) && Directory.EnumerateFileSystemEntries(localPath).Any())
                return JsonSerializer.Serialize(new
                {
                    error = $"Target directory '{localPath}' already exists and is not empty"
                });

            var cloneOptions = new CloneOptions
            {
                BranchName = branch,
                IsBare = false,
                Checkout = true
            };

            if (shallow)
            {
                cloneOptions.FetchOptions.Depth = 1;
            }

            var clonePath = await Task.Run(() =>
                Repository.Clone(url, localPath, cloneOptions), cancellationToken);

            using var repo = new Repository(clonePath);
            return JsonSerializer.Serialize(new
            {
                cloned = true,
                url,
                path = clonePath,
                branch = repo.Head.FriendlyName,
                commit = repo.Head.Tip?.Sha[..8]
            }, JsonOpts);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }
}
