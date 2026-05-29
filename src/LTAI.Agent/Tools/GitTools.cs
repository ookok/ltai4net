using System.ComponentModel;
using System.Text;
using LibGit2Sharp;

namespace LTAI.Agent.Tools;

/// <summary>
/// Git tools using LibGit2Sharp (native libgit2, no CLI calls).
/// Operations: status, log, branch (list/create), stage, commit, merge, tag, stash.
/// </summary>
public sealed class GitTools
{
    private readonly string _ws;
    public GitTools(string ws) => _ws = ws;
    private Signature Sig() => new("LTAI", "ltai@local", DateTimeOffset.Now);
    private Repository Open() => new(Repository.Discover(_ws));

    [Description("Show repository status")]
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

    [Description("Show commit log")]
    public string GitLog([Description("Max commits")] int count = 10)
    {
        using var repo = Open();
        var sb = new StringBuilder("## Git Log\n| Commit | Author | Message |\n|--------|--------|---------|\n");
        foreach (var c in repo.Commits.QueryBy(new CommitFilter()).Take(count))
            sb.AppendLine($"| {c.Sha[..8]} | {c.Author.Name} | {c.MessageShort.TrimEnd()[..Math.Min(c.MessageShort.Length, 60)]} |");
        return sb.ToString();
    }

    [Description("List branches")]
    public string GitBranch([Description("Remote branches too")] bool all = false)
    {
        using var repo = Open();
        var sb = new StringBuilder("## Branches\n| Branch | Latest |\n|--------|--------|\n");
        foreach (var b in (all ? repo.Branches : repo.Branches.Where(b => !b.IsRemote)).OrderByDescending(b => b.Tip?.Author.When))
            sb.AppendLine($"| {(b.IsCurrentRepositoryHead ? "▶ " : "  ")}{b.FriendlyName} | {b.Tip?.Sha[..8] ?? "-"} |");
        return sb.ToString();
    }

    [Description("Create switch branch")]
    public string GitCheckout([Description("Branch")] string target, [Description("Create")] bool createNew = false)
    {
        using var repo = Open();
        if (createNew) { var b = repo.CreateBranch(target, repo.Head.Tip); Commands.Checkout(repo, b); return $"✅ Created '{target}'"; }
        var branch = repo.Branches[target];
        if (branch != null) { Commands.Checkout(repo, branch); return $"✅ Switched to '{target}'"; }
        return $"Branch '{target}' not found";
    }

    [Description("Stage files")]
    public string GitAdd([Description("Files (comma-sep) or '.'")] string paths = ".")
    {
        using var repo = Open();
        if (paths == ".") Commands.Stage(repo, "*");
        else foreach (var f in paths.Split(',')) Commands.Stage(repo, f.Trim());
        return $"✅ Staged: {paths}";
    }

    [Description("Create commit")]
    public string GitCommit([Description("Message")] string message, [Description("Author")] string? author = null, [Description("Email")] string? email = null)
    {
        using var repo = Open();
        var sig = author != null ? new Signature(author, email ?? "user@local", DateTimeOffset.Now) : Sig();
        var c = repo.Commit(message, sig, sig);
        return $"✅ {c.Sha[..8]}: {c.MessageShort.Trim()}";
    }

    [Description("Unstage files")]
    public string GitUnstage([Description("Files (comma-sep)")] string paths)
    {
        using var repo = Open();
        foreach (var f in paths.Split(',')) Commands.Unstage(repo, f.Trim());
        return $"Unstaged: {paths}";
    }

    [Description("Merge branch")]
    public string GitMerge([Description("Source branch")] string branch)
    {
        using var repo = Open();
        var b = repo.Branches[branch];
        if (b == null) return "Branch not found";
        return repo.Merge(b, Sig()).Status switch
        {
            MergeStatus.Conflicts => "❌ Conflicts",
            MergeStatus.FastForward => "✅ Fast-forward",
            MergeStatus.UpToDate => "✅ Up to date",
            var s => $"✅ {s}"
        };
    }

    [Description("List remotes")]
    public string GitRemote()
    {
        using var repo = Open();
        return "## Remotes\n" + string.Join("\n", repo.Network.Remotes.Select(r => $"- {r.Name} → {r.Url}"));
    }

    [Description("Push to remote")]
    public string GitPush([Description("Remote")] string remote = "origin", [Description("Branch")] string? branch = null)
    {
        using var repo = Open();
        var rmt = repo.Network.Remotes[remote];
        if (rmt == null) return $"Remote '{remote}' not found";
        repo.Network.Push(rmt, $"+refs/heads/{branch ?? repo.Head.FriendlyName}", new PushOptions());
        return $"✅ Pushed to '{remote}'";
    }

    [Description("List tags")]
    public string GitTag()
    {
        using var repo = Open();
        return "## Tags\n" + string.Join("\n", repo.Tags.Select(t => $"- {t.FriendlyName} → {t.Target.Sha[..8]}"));
    }

    [Description("Stash changes")]
    public string GitStash([Description("Message")] string? msg = null)
    {
        using var repo = Open();
        repo.Stashes.Add(Sig(), msg ?? "WIP");
        return $"📦 Stashed";
    }

    [Description("Pop stash")]
    public string GitStashPop([Description("Index")] int index = 0)
    {
        using var repo = Open();
        if (repo.Stashes.Count() <= index) return "Not found";
        repo.Stashes.Pop(index);
        return $"✅ Popped stash@{{{index}}}";
    }
}
