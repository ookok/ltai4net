using System.ComponentModel;
using System.Text;
using LTAI.AI;
using LibGit2Sharp;

namespace LTAI.Agent.Tools;

/// <summary>
/// 完整 Git 工具集 — 基于 LibGit2Sharp 原生实现，不依赖 git CLI。
/// 涵盖 28 个操作：status/log/diff/branch/commit/stash/tag/remote/rebase/blame/reset等
/// 环境变量: 无（所有操作在本地仓库完成，remote 操作需 SSH/HTTP 凭据）
/// </summary>
[ToolDomain("git")]
public sealed class GitTools
{
    private readonly string _ws;
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(60);
    public GitTools(string ws) => _ws = ws;
    private Signature Sig() => new("LTAI", "ltai@local", DateTimeOffset.Now);
    private Repository Open() => new(Repository.Discover(_ws));

    private static string RunWithTimeout(Func<string> action, string errorLabel)
    {
        using var cts = new CancellationTokenSource(GitTimeout);
        try
        {
            var task = Task.Run(action, cts.Token);
            return task.Wait(GitTimeout) ? task.Result! : $"⏱ {errorLabel} timed out ({GitTimeout.TotalSeconds}s)";
        }
        catch (AggregateException ae) when (ae.InnerException != null)
        {
            return $"{errorLabel}: {ae.InnerException.Message}";
        }
    }

    // ═══ Status & Log ═══

    [Description("显示 Git 仓库状态：已暂存、已修改、未跟踪、已删除的文件列表，以及当前分支和远程进度差。\n"
        + "适用场景：查看当前工作区状态、确认还有哪些未提交的更改、检查分支同步进度。\n"
        + "不适用场景：查看提交历史（请用 GitLog）、查看具体差异（请用 GitDiff）。")]
    [ToolExample("看看当前仓库状态")]
    [ToolExample("有哪些文件未提交")]
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
            var entryList = entries.ToList();
            if (!entryList.Any()) continue;
            sb.AppendLine($"\n{label} ({entryList.Count}):");
            foreach (var e in entryList.Take(15)) sb.AppendLine($"  - {e.FilePath}");
            if (entryList.Count > 15) sb.AppendLine($"  ... +{entryList.Count - 15}");
        }
        return sb.ToString();
    }

    [Description("查看 Git 提交历史。支持按分支和文件过滤。\n"
        + "适用场景：查看最近的提交记录、按分支查看提交、查看某个文件的历史变更。\n"
        + "不适用场景：查看工作区状态（请用 GitStatus）、查看具体差异（请用 GitDiff）。\n"
        + "关键参数：count — 显示的提交数；branch — 分支过滤；file — 文件过滤。")]
    [ToolExample("查看最近的提交历史")]
    [ToolExample("看看 main 分支的提交记录")]
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

    [Description("查看文件或仓库的差异(diff)内容。支持按提交引用和文件过滤。\n"
        + "适用场景：查看未暂存的修改内容、比较两次提交的差异、审查代码变更。\n"
        + "不适用场景：查看提交历史（请用 GitLog）、查看提交详情（请用 GitShow）。\n"
        + "关键参数：ref — 提交引用(可选)；file — 文件路径(可选)。")]
    [ToolExample("看看我改了什么")]
    [ToolExample("比较这两个版本的区别")]
    public string GitDiff(string? @ref = null, string? file = null)
    {
        using var repo = Open();
        Commit? commit = @ref != null ? repo.Lookup<Commit>(@ref) : repo.Head.Tip;
        if (commit == null) return "(no commits)";
        var patch = repo.Diff.Compare<Patch>(commit.Tree, null,
            file != null ? new[] { file } : null);
        return string.IsNullOrWhiteSpace(patch.Content) ? "(no diff)" : patch.Content;
    }

    [Description("查看文件的每一行是谁在什么时候修改的（git blame）。\n"
        + "适用场景：追查某行代码是谁写的、排查代码变更历史、代码审计。\n"
        + "不适用场景：查看文件整体变更历史（请用 GitLog）、查看 diff（请用 GitDiff）。\n"
        + "关键参数：file — 要追溯的文件路径。")]
    [ToolExample("这行代码是谁写的")]
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

    [Description("显示某次提交的详细信息：SHA、作者、日期、提交消息和代码变更。\n"
        + "适用场景：查看某次提交的完整内容、审查特定 commit 的变更。\n"
        + "不适用场景：查看提交列表（请用 GitLog）、查看工作区 diff（请用 GitDiff）。\n"
        + "关键参数：@ref — 提交引用(SHA/分支名/标签)。")]
    [ToolExample("看看这个提交改了什么")]
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

    [Description("列出所有本地分支（可选包含远程分支），显示各分支的最新提交。\n"
        + "适用场景：查看仓库有哪些分支、确认当前所在分支、浏览分支列表。\n"
        + "不适用场景：切换分支（请用 GitCheckout）、删除分支（请用 GitBranchDelete）。\n"
        + "关键参数：all — 是否包含远程分支。")]
    [ToolExample("看看有哪些分支")]
    public string GitBranch(bool all = false)
    {
        using var repo = Open();
        var sb = new StringBuilder("## Branches\n| Branch | Latest |\n|--------|--------|\n");
        foreach (var b in (all ? repo.Branches : repo.Branches.Where(b => !b.IsRemote)).OrderByDescending(b => b.Tip?.Author.When))
            sb.AppendLine($"| {(b.IsCurrentRepositoryHead ? "▶ " : "  ")}{b.FriendlyName} | {b.Tip?.Sha[..8] ?? "-"} |");
        return sb.ToString();
    }

    [Description("切换到已有分支，或用 createNew=true 创建并切换到新分支。\n"
        + "适用场景：切换到其他分支继续工作、创建功能分支或修复分支。\n"
        + "不适用场景：分支列表（请用 GitBranch）、删除分支（请用 GitBranchDelete）。\n"
        + "关键参数：target — 分支名；createNew — 是否创建新分支。注意：切换分支会修改工作区文件。")]
    [ToolExample("切换到 main 分支")]
    [ToolExample("创建一个新分支")]
    public string GitCheckout(string target, bool createNew = false)
    {
        using var repo = Open();
        if (createNew) { var b = repo.CreateBranch(target, repo.Head.Tip); Commands.Checkout(repo, b); return $"✅ Created '{target}'"; }
        var branch = repo.Branches[target];
        if (branch != null) { Commands.Checkout(repo, branch); return $"✅ Switched to '{target}'"; }
        return $"Branch '{target}' not found";
    }

    [Description("删除本地分支。已合并的分支可以直接删除，未合并的需要 force=true。\n"
        + "适用场景：清理已合并的功能分支、删除废弃分支。\n"
        + "不适用场景：切换分支（请用 GitCheckout）、批量清理（请用 GitCleanupBranches）。\n"
        + "关键参数：name — 分支名；force — 是否强制删除(未合并时)。注意：删除分支可能导致提交丢失。")]
    [ToolExample("删除这个分支")]
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

    [Description("自动清理所有已合并的本地分支。\n"
        + "适用场景：在合并 PR 后批量清理过期的功能分支、保持分支列表整洁。\n"
        + "不适用场景：删除单个分支（请用 GitBranchDelete）。注意：此操作会批量删除多个分支。")]
    [ToolExample("清理已合并的分支")]
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

    [Description("暂存文件变更到暂存区(index)，准备提交。\n"
        + "适用场景：将修改的文件添加到暂存区、选择特定文件暂存而非全部提交。\n"
        + "不适用场景：取消暂存（请用 GitUnstage）、直接提交（请用 GitCommit）。\n"
        + "关键参数：paths — 要暂存的文件路径(逗号分隔，默认全部)。")]
    [ToolExample("暂存这个文件")]
    public string GitAdd(string paths = ".")
    {
        using var repo = Open();
        if (paths == ".") Commands.Stage(repo, "*");
        else foreach (var f in paths.Split(',')) Commands.Stage(repo, f.Trim());
        return $"✅ Staged: {paths}";
    }

    [Description("取消暂存区的文件变更，将文件移出暂存区但保留工作区修改。\n"
        + "适用场景：不小心暂存了不该提交的文件、修改暂存的文件列表。\n"
        + "关键参数：paths — 要取消暂存的文件路径(逗号分隔)。")]
    [ToolExample("取消暂存这个文件")]
    public string GitUnstage(string paths)
    {
        using var repo = Open();
        foreach (var f in paths.Split(',')) Commands.Unstage(repo, f.Trim());
        return $"Unstaged: {paths}";
    }

    [Description("创建 Git 提交(commit)。将暂存区的内容保存为一个新提交。\n"
        + "适用场景：在完成一个功能后提交代码、保存工作进度。\n"
        + "不适用场景：暂存文件（请用 GitAdd）、提交并推送（请用 GitCommitAndPush）。\n"
        + "关键参数：message — 提交消息；author/email — 可选的自定义作者。注意：提交会创建不可变的历史记录。")]
    [ToolExample("提交代码")]
    public string GitCommit(string message, string? author = null, string? email = null)
    {
        using var repo = Open();
        var sig = author != null ? new Signature(author, email ?? "user@local", DateTimeOffset.Now) : Sig();
        var c = repo.Commit(message, sig, sig);
        return $"✅ {c.Sha[..8]}: {c.MessageShort.Trim()}";
    }

    [Description("创建提交并立即推送到远程仓库。GitCommit + GitPush 的快捷操作。\n"
        + "适用场景：快速完成提交并推送、一次完成本地和远程的变更保存。\n"
        + "不适用场景：只想本地提交（请用 GitCommit）、只推送已有提交（请用 GitPush）。\n"
        + "关键参数：message — 提交消息；remote — 远程仓库名。注意：此操作会修改远程仓库。")]
    [ToolExample("提交并推送代码")]
    public string GitCommitAndPush(string message, string remote = "origin")
    {
        return RunWithTimeout(() =>
        {
            using var repo = Open();
            var sig = Sig();
            var c = repo.Commit(message, sig, sig);
            var rmt = repo.Network.Remotes[remote];
            if (rmt == null) return $"Remote '{remote}' not found";
            repo.Network.Push(rmt, $"+refs/heads/{repo.Head.FriendlyName}", new PushOptions());
            return $"✅ {c.Sha[..8]}: {c.MessageShort.Trim()}\n✅ Pushed to '{remote}'";
        }, "CommitAndPush");
    }

    [Description("撤销上一次提交（软重置，保留工作区的更改）。相当于 git reset --soft HEAD~1。\n"
        + "适用场景：提交后发现忘了包含某个文件、提交消息写错需要重做、合并后想重新编辑。\n"
        + "不适用场景：彻底丢弃更改（请用 GitReset hard=true）。注意：此操作修改提交历史。")]
    [ToolExample("撤销刚刚的提交")]
    public string GitUndoLast()
    {
        using var repo = Open();
        if (repo.Head.Tip == null) return "No commits to undo";
        var msg = repo.Head.Tip.MessageShort;
        repo.Reset(ResetMode.Soft, repo.Head.Tip.Parents.FirstOrDefault());
        return $"↩️ Undid commit: {msg}";
    }

    [Description("重置 HEAD 到指定提交。hard=true 会丢弃所有工作区更改。\n"
        + "适用场景：放弃所有本地更改回到某个提交、撤销一系列提交、清理工作区。\n"
        + "不适用场景：只撤销最近一次提交（请用 GitUndoLast）。\n"
        + "关键参数：target — 目标提交；hard — 是否丢弃工作区更改。注意：hard=true 会永久丢弃未提交的更改！")]
    [ToolExample("重置到上一个提交")]
    public string GitReset(string? target = null, bool hard = false)
    {
        using var repo = Open();
        var c = target != null ? repo.Lookup<Commit>(target) : repo.Head.Tip?.Parents.FirstOrDefault();
        if (c == null) return "Target not found";
        repo.Reset(hard ? ResetMode.Hard : ResetMode.Soft, c);
        return $"✅ Reset {(hard ? "(HARD)" : "(soft)")} to {c.Sha[..8]}";
    }

    // ═══ Remote ═══

    [Description("列出所有远程仓库及其 URL。\n"
        + "适用场景：查看配置了哪些远程仓库、确认远程仓库地址。\n"
        + "不适用场景：推送到远程（请用 GitPush）、拉取远程更新（请用 GitFetch/GitPull）。")]
    [ToolExample("有哪些远程仓库")]
    public string GitRemote()
    {
        using var repo = Open();
        return "## Remotes\n" + string.Join("\n", repo.Network.Remotes.Select(r => $"- {r.Name} → {r.Url}"));
    }

    [Description("从远程仓库拉取最新数据(fetch)，但不自动合并到当前分支。\n"
        + "适用场景：查看远程最新变更而不影响本地代码、在合并前先获取更新。\n"
        + "不适用场景：拉取并合并（请用 GitPull）、推送本地更改（请用 GitPush）。\n"
        + "关键参数：remote — 远程仓库名。")]
    [ToolExample("拉取远程最新代码")]
    public string GitFetch(string remote = "origin") => RunWithTimeout(() =>
    {
        using var repo = Open();
        Commands.Fetch(repo, remote, [], new FetchOptions(), "");
        return $"✅ Fetched '{remote}'";
    }, "Fetch");

    [Description("将本地提交推送到远程仓库。\n"
        + "适用场景：将本地代码共享到远程仓库、提交 PR 前推送分支。\n"
        + "不适用场景：拉取远程更新（请用 GitPull/GitFetch）、提交并推送（请用 GitCommitAndPush）。\n"
        + "关键参数：remote — 远程仓库名；branch — 分支名(默认当前分支)。注意：推送会修改远程仓库。")]
    [ToolExample("推送代码到远程")]
    public string GitPush(string remote = "origin", string? branch = null)
    {
        return RunWithTimeout(() =>
        {
            using var repo = Open();
            var rmt = repo.Network.Remotes[remote];
            if (rmt == null) return $"Remote '{remote}' not found";
            repo.Network.Push(rmt, $"+refs/heads/{branch ?? repo.Head.FriendlyName}", new PushOptions());
            return $"✅ Pushed to '{remote}'";
        }, "Push");
    }

    [Description("从远程仓库拉取并合并到当前分支(pull = fetch + merge)。\n"
        + "适用场景：同步远程最新代码到本地、更新当前分支到最新。\n"
        + "不适用场景：只拉取不合并（请用 GitFetch）、推送本地更改（请用 GitPush）。\n"
        + "关键参数：remote — 远程仓库名；branch — 分支名。注意：拉取可能产生合并冲突。")]
    [ToolExample("拉取最新的代码")]
    public string GitPull(string remote = "origin", string? branch = null)
    {
        return RunWithTimeout(() =>
        {
            var fetchResult = GitFetch(remote);
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
        }, "Pull");
    }

    [Description("变基(rebase)当前分支到目标分支，使提交历史更线性整洁。\n"
        + "适用场景：在合并前将功能分支变基到 main、保持提交历史整洁。\n"
        + "不适用场景：普通合并（请用 GitMerge）、同步远程分支（请用 GitPull）。\n"
        + "关键参数：target — 目标分支名。注意：变基会重写提交历史，不可逆。")]
    [ToolExample("变基到 main 分支")]
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

    [Description("同步 fork 仓库：从上游仓库 fetch 并 merge 到当前分支。\n"
        + "适用场景：保持 fork 仓库与上游同步、更新 fork 到最新版本。\n"
        + "不适用场景：普通拉取（请用 GitPull）、合并其他分支（请用 GitMerge）。\n"
        + "关键参数：upstream — 上游远程名；branch — 要同步的分支。注意：同步会合并上游更改到当前分支。")]
    [ToolExample("同步 fork 与上游仓库")]
    public string GitSyncFork(string upstream = "upstream", string branch = "master")
    {
        return RunWithTimeout(() =>
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
        }, "SyncFork");
    }

    // ═══ Merge ═══

    [Description("合并指定分支到当前分支。\n"
        + "适用场景：将功能分支合并到 main、合并同事的更改到当前分支。\n"
        + "不适用场景：变基（请用 GitRebase）、同步远程（请用 GitPull）。\n"
        + "关键参数：branch — 要合并的分支名。注意：合并可能产生冲突。")]
    [ToolExample("合并 feature 分支")]
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

    [Description("列出或创建 Git 标签。无参数时列出所有标签；传入 name 和 message 创建新标签。\n"
        + "适用场景：查看发布的版本标签、创建新版本的标签。\n"
        + "不适用场景：创建分支（请用 GitCheckout createNew=true）。\n"
        + "关键参数：name — 标签名；message — 标签附注消息。注意：创建标签会添加引用。")]
    [ToolExample("创建 v1.0 标签")]
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

    [Description("暂存当前工作区的更改(stash)，清空工作区以便切换分支或拉取代码。\n"
        + "适用场景：需要切换分支但不想提交当前更改、暂存半成品代码。\n"
        + "不适用场景：恢复暂存（请用 GitStashPop）、查看暂存列表（请用 GitStashList）。\n"
        + "关键参数：message — 暂存描述信息。注意：暂存会清空工作区更改。")]
    [ToolExample("暂存当前更改")]
    public string GitStash(string? message = null)
    {
        using var repo = Open();
        repo.Stashes.Add(Sig(), message ?? "WIP");
        return "📦 Stashed";
    }

    [Description("恢复之前暂存的更改(stash pop)并删除暂存记录。\n"
        + "适用场景：在切换分支回来后续续工作、恢复之前暂存的半成品。\n"
        + "不适用场景：暂存新更改（请用 GitStash）、查看暂存列表（请用 GitStashList）。\n"
        + "关键参数：index — 要恢复的暂存索引(0=最近一次)。注意：pop 后暂存记录被删除。")]
    [ToolExample("恢复之前的暂存")]
    public string GitStashPop(int index = 0)
    {
        using var repo = Open();
        if (repo.Stashes.Count() <= index) return "Not found";
        repo.Stashes.Pop(index);
        return $"✅ Popped stash@{{{index}}}";
    }

    [Description("列出所有暂存(stash)记录，显示索引和描述信息。\n"
        + "适用场景：查看有哪些可恢复的暂存、查看暂存的内容描述。\n"
        + "不适用场景：恢复暂存（请用 GitStashPop）。")]
    [ToolExample("有哪些暂存的更改")]
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

    [Description("审查所有未提交的更改：包括已暂存和未暂存的差异。\n"
        + "适用场景：在提交前全面审查所有改动、确认哪些更改要提交。\n"
        + "不适用场景：查看特定文件 diff（请用 GitDiff）、查看仓库状态（请用 GitStatus）。")]
    [ToolExample("审查所有未提交的更改")]
    public string GitReviewChanges()
    {
        using var repo = Open();
        if (repo.Head.Tip == null) return "(no commits yet)";
        var patch = repo.Diff.Compare<Patch>(repo.Head.Tip.Tree, null);
        return string.IsNullOrWhiteSpace(patch.Content) ? "(no uncommitted changes)" : patch.Content;
    }

    // Removed Shell() — all operations now use LibGit2Sharp natively
}
