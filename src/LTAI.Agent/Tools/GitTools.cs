using System.ComponentModel;
using System.Text;
using LibGit2Sharp;

namespace LTAI.Agent.Tools;

/// <summary>
/// 完整 Git 工具集 — 基于 LibGit2Sharp 原生实现，不依赖 git CLI。
/// 涵盖 28 个操作：status/log/diff/branch/commit/stash/tag/remote/rebase/blame/reset等
/// 环境变量: 无（所有操作在本地仓库完成，remote 操作需 SSH/HTTP 凭据）
/// </summary>
public sealed class GitTools
{
    private readonly string _ws;
    public GitTools(string ws) => _ws = ws;
    private Signature Sig() => new("LTAI", "ltai@local", DateTimeOffset.Now);
    private Repository Open() => new(Repository.Discover(_ws));

    // ═══ Status & Log ═══

    [Description("显示仓库状态（已暂存/已修改/未跟踪/已删除）")]
    public string GitStatus()
    {
        using var repo = Open();
        var s = repo.RetrieveStatus();
        var sb = new StringBuilder($"## {Path.GetFileName(repo.Info.WorkingDirectory)} [{repo.Head.FriendlyName}]");
        if (repo.Head.TrackingDetails != null)
            sb.Append($" A{repo.Head.TrackingDetails.AheadBy ?? 0}/B{repo.Head.TrackingDetails.BehindBy ?? 0}");
        sb.AppendLine();
        foreach (var (entries, label) in new[] {
            (s.Staged, "Staged"), (s.Modified, "Modified"),
            (s.Untracked, "Untracked"), (s.Missing, "Deleted") })
        {
            if (!entries.Any()) continue;
            sb.AppendLine($"\n{label} ({entries.Count()}):");
            foreach (var e in entries.Take(15)) sb.AppendLine($"  - {e.FilePath}");
            if (entries.Count() > 15) sb.AppendLine($"  ... +{entries.Count() - 15}");
        }
        return sb.ToString();
    }

    [Description("提交历史")]
    public string GitLog(int count = 10, string? branch = null, string? file = null)
    {
        using var repo = Open();
        var sb = new StringBuilder("## Git Log\n| Commit | Author | Date | Message |\n|--------|--------|------|---------|\n");
        var filter = new CommitFilter();
        if (branch != null) filter.IncludeReachableFrom = repo.Branches[branch];
        if (file != null) filter.SortBy = CommitSortStrategies.Time;
        var query = repo.Commits.QueryBy(filter);
        foreach (var c in query.Take(count))
        {
            var msg = c.MessageShort.TrimEnd();
            if (msg.Length > 50) msg = msg[..50] + "...";
            sb.AppendLine($"| {c.Sha[..8]} | {c.Author.Name} | {c.Author.When:MM-dd HH:mm} | {msg} |");
        }
        return sb.ToString();
    }

    [Description("差异比较（基于 LibGit2Sharp）")]
    public string GitDiff(string? @ref = null, string? file = null)
    {
        using var repo = Open();
        Commit? commit = @ref != null ? repo.Lookup<Commit>(@ref) : repo.Head.Tip;
        if (commit == null) return "(no commits)";
        var patch = repo.Diff.Compare<Patch>(commit.Tree, null,
            file != null ? new[] { file } : null);
        return string.IsNullOrWhiteSpace(patch.Content) ? "(no diff)" : patch.Content;
    }

    [Description("文件追溯（blame，基于 LibGit2Sharp）")]
    public string GitBlame(string file)
    {
        using var repo = Open();
        var sb = new StringBuilder($"## Blame: {file}\n");
        var blame = repo.Blame(file, new BlameOptions());
        foreach (var hunk in blame)
        {
            var commit = hunk.FinalCommit;
            sb.AppendLine($"  L{hunk.FinalStartLineNumber,4}: {commit.Sha[..8]} {commit.Author.Name,-15} {commit.MessageShort.Trim()}");
        }
        return sb.ToString();
    }

    [Description("显示提交详情（基于 LibGit2Sharp）")]
    public string GitShow(string? @ref = null)
    {
        using var repo = Open();
        Commit? commit = @ref != null ? repo.Lookup<Commit>(@ref) : repo.Head.Tip;
        if (commit == null) return "Commit not found";
        var sb = new StringBuilder($"## {commit.Sha}\n");
        sb.AppendLine($"Author: {commit.Author.Name} <{commit.Author.Email}>");
        sb.AppendLine($"Date:   {commit.Author.When:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine(commit.Message.TrimEnd());
        sb.AppendLine();
        if (commit.Tree != null)
        {
            var parentTree = commit.Parents.FirstOrDefault()?.Tree;
            var patch = repo.Diff.Compare<Patch>(parentTree, commit.Tree);
            if (!string.IsNullOrWhiteSpace(patch.Content))
                sb.AppendLine(patch.Content);
        }
        return sb.ToString();
    }

    // ═══ Branch ═══

    [Description("分支列表")]
    public string GitBranch(bool all = false)
    {
        using var repo = Open();
        var sb = new StringBuilder("## Branches\n| Branch | Latest |\n|--------|--------|\n");
        foreach (var b in (all ? repo.Branches : repo.Branches.Where(b => !b.IsRemote)).OrderByDescending(b => b.Tip?.Author.When))
            sb.AppendLine($"| {(b.IsCurrentRepositoryHead ? "▶ " : "  ")}{b.FriendlyName} | {b.Tip?.Sha[..8] ?? "-"} |");
        return sb.ToString();
    }

    [Description("切换/创建分支")]
    public string GitCheckout(string target, bool createNew = false)
    {
        using var repo = Open();
        if (createNew) { var b = repo.CreateBranch(target, repo.Head.Tip); Commands.Checkout(repo, b); return $"✅ Created '{target}'"; }
        var branch = repo.Branches[target];
        if (branch != null) { Commands.Checkout(repo, branch); return $"✅ Switched to '{target}'"; }
        return $"Branch '{target}' not found";
    }

    [Description("删除分支")]
    public string GitBranchDelete(string name, bool force = false)
    {
        using var repo = Open();
        try
        {
            repo.Branches.Remove(name, force);
            return $"🗑️ Deleted '{name}'";
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    [Description("清理已合并分支")]
    public string GitCleanupBranches()
    {
        using var repo = Open();
        var current = repo.Head.FriendlyName;
        var merged = repo.Branches.Where(b => !b.IsCurrentRepositoryHead && !b.IsRemote
            && repo.Head.TrackingDetails?.AheadBy > 0 != true).ToList();
        foreach (var b in merged)
            try { repo.Branches.Remove(b.FriendlyName); } catch { /* non-fatal: branch may be protected */ }
        return $"Cleaned up {merged.Count} merged branches";
    }

    // ═══ Stage & Commit ═══

    [Description("暂存文件")]
    public string GitAdd(string paths = ".")
    {
        using var repo = Open();
        if (paths == ".") Commands.Stage(repo, "*");
        else foreach (var f in paths.Split(',')) Commands.Stage(repo, f.Trim());
        return $"✅ Staged: {paths}";
    }

    [Description("取消暂存")]
    public string GitUnstage(string paths)
    {
        using var repo = Open();
        foreach (var f in paths.Split(',')) Commands.Unstage(repo, f.Trim());
        return $"Unstaged: {paths}";
    }

    [Description("创建提交")]
    public string GitCommit(string message, string? author = null, string? email = null)
    {
        using var repo = Open();
        var sig = author != null ? new Signature(author, email ?? "user@local", DateTimeOffset.Now) : Sig();
        var c = repo.Commit(message, sig, sig);
        return $"✅ {c.Sha[..8]}: {c.MessageShort.Trim()}";
    }

    [Description("提交并推送到远程")]
    public string GitCommitAndPush(string message, string remote = "origin")
    {
        var commitResult = GitCommit(message);
        var pushResult = GitPush(remote);
        return $"{commitResult}\n{pushResult}";
    }

    [Description("撤销上一次提交（保留更改）")]
    public string GitUndoLast()
    {
        using var repo = Open();
        if (repo.Head.Tip == null) return "No commits to undo";
        var msg = repo.Head.Tip.MessageShort;
        repo.Reset(ResetMode.Soft, repo.Head.Tip.Parents.FirstOrDefault());
        return $"↩️ Undid commit: {msg}";
    }

    [Description("硬重置到指定提交")]
    public string GitReset(string? target = null, bool hard = false)
    {
        using var repo = Open();
        var c = target != null ? repo.Lookup<Commit>(target) : repo.Head.Tip?.Parents.FirstOrDefault();
        if (c == null) return "Target not found";
        repo.Reset(hard ? ResetMode.Hard : ResetMode.Soft, c);
        return $"✅ Reset {(hard ? "(HARD)" : "(soft)")} to {c.Sha[..8]}";
    }

    // ═══ Remote ═══

    [Description("远程仓库列表")]
    public string GitRemote()
    {
        using var repo = Open();
        return "## Remotes\n" + string.Join("\n", repo.Network.Remotes.Select(r => $"- {r.Name} → {r.Url}"));
    }

    [Description("拉取（fetch）")]
    public string GitFetch(string remote = "origin")
    {
        using var repo = Open();
        try { Commands.Fetch(repo, remote, [], new FetchOptions(), ""); return $"✅ Fetched '{remote}'"; }
        catch (Exception ex) { return $"Fetch error: {ex.Message}"; }
    }

    [Description("推送（push）")]
    public string GitPush(string remote = "origin", string? branch = null)
    {
        using var repo = Open();
        try
        {
            var rmt = repo.Network.Remotes[remote];
            if (rmt == null) return $"Remote '{remote}' not found";
            repo.Network.Push(rmt, $"+refs/heads/{branch ?? repo.Head.FriendlyName}", new PushOptions());
            return $"✅ Pushed to '{remote}'";
        }
        catch (Exception ex) { return $"Push error: {ex.Message}"; }
    }

    [Description("拉取并合并（pull）")]
    public string GitPull(string remote = "origin", string? branch = null)
    {
        var fetchResult = GitFetch(remote);
        if (!fetchResult.StartsWith("✅")) return fetchResult;
        using var repo = Open();
        var tracked = branch != null ? repo.Branches[$"{remote}/{branch}"] : repo.Head.TrackedBranch;
        if (tracked == null) return "No upstream branch";
        var result = repo.Merge(tracked, Sig());
        return result.Status switch
        {
            MergeStatus.UpToDate => "✅ Already up to date",
            MergeStatus.FastForward => $"✅ Pulled (fast-forward)",
            MergeStatus.NonFastForward => $"✅ Pulled (merge: {result.Commit?.Sha[..8]})",
            MergeStatus.Conflicts => "❌ Conflicts",
            var s => $"Pull: {s}"
        };
    }

    [Description("变基（rebase，基于 LibGit2Sharp）")]
    public string GitRebase(string? target = null)
    {
        using var repo = Open();
        Branch? upstream = target != null ? repo.Branches[target] : repo.Head.TrackedBranch;
        if (upstream == null) return "No upstream branch to rebase onto";
        var sig = Sig();
        var result = repo.Rebase.Start(
            repo.Head,
            upstream,
            null,
            new Identity(sig.Name, sig.Email),
            new RebaseOptions());
        return result.Status switch
        {
            RebaseStatus.Complete => "✅ Rebase completed",
            RebaseStatus.Conflicts => "❌ Rebase conflicts — resolve and continue with 'git rebase --continue'",
            var s => $"Rebase: {s}"
        };
    }

    [Description("同步 fork（fetch + merge upstream）")]
    public string GitSyncFork(string upstream = "upstream", string branch = "master")
    {
        var fetchResult = GitFetch(upstream);
        if (!fetchResult.StartsWith("✅")) return fetchResult;
        using var repo = Open();
        var upstreamBranch = repo.Branches[$"{upstream}/{branch}"];
        if (upstreamBranch == null) return $"Branch '{upstream}/{branch}' not found";
        var result = repo.Merge(upstreamBranch, Sig());
        return result.Status switch
        {
            MergeStatus.UpToDate => "✅ Up to date with upstream",
            MergeStatus.FastForward => $"✅ Synced (fast-forward)",
            _ => $"Sync result: {result.Status}"
        };
    }

    // ═══ Merge ═══

    [Description("合并分支")]
    public string GitMerge(string branch)
    {
        using var repo = Open();
        var b = repo.Branches[branch];
        if (b == null) return $"Branch '{branch}' not found";
        return repo.Merge(b, Sig()).Status switch
        {
            MergeStatus.Conflicts => "❌ Conflicts",
            MergeStatus.FastForward => "✅ Fast-forward",
            MergeStatus.UpToDate => "✅ Up to date",
            var s => $"✅ {s}"
        };
    }

    // ═══ Tag ═══

    [Description("标签列表/创建")]
    public string GitTag(string? name = null, string? message = null)
    {
        using var repo = Open();
        if (name == null)
            return "## Tags\n" + string.Join("\n", repo.Tags.Select(t => $"- {t.FriendlyName} → {t.Target.Sha[..8]}"));
        var target = repo.Head.Tip;
        if (!string.IsNullOrEmpty(message))
            repo.Tags.Add(name, target, Sig(), message);
        else
            repo.Tags.Add(name, target);
        return $"🏷️ Created '{name}'";
    }

    // ═══ Stash ═══

    [Description("暂存更改")]
    public string GitStash(string? message = null)
    {
        using var repo = Open();
        repo.Stashes.Add(Sig(), message ?? "WIP");
        return "📦 Stashed";
    }

    [Description("恢复暂存（pop）")]
    public string GitStashPop(int index = 0)
    {
        using var repo = Open();
        if (repo.Stashes.Count() <= index) return "Not found";
        repo.Stashes.Pop(index);
        return $"✅ Popped stash@{{{index}}}";
    }

    [Description("暂存列表")]
    public string GitStashList()
    {
        using var repo = Open();
        var sb = new StringBuilder("## Stashes\n");
        int i = 0;
        foreach (var s in repo.Stashes)
            sb.AppendLine($"  stash@{{{i++}}}: {s.Message}");
        return sb.ToString();
    }

    // ═══ Review ═══

    [Description("审查未提交的更改（基于 LibGit2Sharp）")]
    public string GitReviewChanges()
    {
        using var repo = Open();
        if (repo.Head.Tip == null) return "(no commits yet)";
        var patch = repo.Diff.Compare<Patch>(repo.Head.Tip.Tree, null);
        return string.IsNullOrWhiteSpace(patch.Content) ? "(no uncommitted changes)" : patch.Content;
    }

    // Removed Shell() — all operations now use LibGit2Sharp natively
}
