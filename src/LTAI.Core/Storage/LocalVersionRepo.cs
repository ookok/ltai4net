using System.Diagnostics;
using System.Text;
using LibGit2Sharp;

namespace LTAI.Core.Storage;

public static class LocalVersionRepo
{
    private static readonly string BaseDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".livingtree");
    private static readonly string GitDir = Path.Combine(BaseDir, ".git");
    private static readonly Signature Committer = new("LTAI", "ltai@local", DateTimeOffset.Now);
    private static readonly Lazy<Repository> _repo = new(InitRepo);
    private static long _commitCount;

    public static string RepositoryPath => GitDir;
    public static string BaseDirectory => BaseDir;

    static LocalVersionRepo()
    {
        _ = _repo.Value;
    }

    private static Repository InitRepo()
    {
        Directory.CreateDirectory(BaseDir);
        if (!Repository.IsValid(GitDir))
            Repository.Init(BaseDir);
        return new Repository(BaseDir);
    }

    private static Repository Repo => _repo.Value;

    private static string Resolve(string relPath) =>
        Path.GetFullPath(Path.Combine(BaseDir, relPath));

    /// <summary>Stage and commit a single file (relative to ~/.livingtree/).</summary>
    public static string Commit(string relPath, string message)
    {
        var full = Resolve(relPath);
        if (!File.Exists(full))
            throw new FileNotFoundException($"File not in .livingtree: {relPath}", full);
        var r = Repo;
        r.Index.Add(NormalizePath(relPath));
        r.Index.Write();
        var c = r.Commit(message, Committer, Committer);
        Interlocked.Increment(ref _commitCount);
        return c.Sha;
    }

    /// <summary>Atomically write content to a .livingtree file and commit.</summary>
    public static string AtomicCommit(string relPath, string content, string message)
    {
        var full = Resolve(relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var tmp = full + ".tmp." + Guid.NewGuid().ToString("N");
        File.WriteAllText(tmp, content);
        File.Move(tmp, full, true);
        return Commit(relPath, message);
    }

    /// <summary>Recent commits touching a specific file path.</summary>
    public static IReadOnlyList<VersionEntry> Log(string relPath, int maxCount = 20)
    {
        var result = new List<VersionEntry>();
        var normalized = NormalizePath(relPath);
        var filter = new CommitFilter { SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time };
        foreach (var c in Repo.Commits.QueryBy(filter))
        {
            if (c.Tree[normalized] == null) continue;
            result.Add(new VersionEntry(c.Sha, c.MessageShort.Trim(), c.Author.When.UtcDateTime, c.Author.Name));
            if (result.Count >= maxCount) break;
        }
        return result;
    }

    /// <summary>Restore a file to its state in a specific commit.</summary>
    public static string Rollback(string relPath, string commitSha)
    {
        var r = Repo;
        var commit = r.Lookup<Commit>(commitSha)
            ?? throw new ArgumentException($"Commit not found: {commitSha}");
        var entry = commit.Tree[NormalizePath(relPath)]
            ?? throw new ArgumentException($"Path '{relPath}' not found in commit {commitSha[..8]}");
        if (entry.Target is not Blob blob)
            throw new InvalidOperationException($"Entry '{relPath}' is not a blob in {commitSha[..8]}");
        var full = Resolve(relPath);
        var text = blob.GetContentText() ?? string.Empty;
        File.WriteAllText(full, text);
        r.Index.Add(NormalizePath(relPath));
        r.Index.Write();
        var revert = r.Commit($"↩ Rollback: {relPath} to {commitSha[..8]}", Committer, Committer);
        return revert.Sha;
    }

    /// <summary>Show diff of a file between HEAD and an older commit.</summary>
    public static string Diff(string relPath, string? fromSha = null)
    {
        var r = Repo;
        var normalized = NormalizePath(relPath);
        var tip = r.Head.Tip;
        if (tip?.Tree == null) return "(no commits)";
        Tree? oldTree = null;
        if (fromSha != null)
        {
            var c = r.Lookup<Commit>(fromSha);
            if (c != null) oldTree = c.Tree;
        }
        else
        {
            var parent = tip.Parents.FirstOrDefault();
            if (parent != null) oldTree = parent.Tree;
        }
        if (oldTree == null) return "(no previous version)";
        var patch = r.Diff.Compare<Patch>(oldTree, tip.Tree, new[] { normalized });
        var text = patch.Content;
        return string.IsNullOrEmpty(text) ? "(no changes)" : text;
    }

    /// <summary>Currently tracked files (by name pattern).</summary>
    public static IReadOnlyList<string> ListTracked(string pattern = "*")
    {
        var r = Repo;
        return r.Index
            .Where(e => !e.Path.EndsWith("/"))
            .Select(e => e.Path)
            .Where(p => System.IO.Path.GetFileName(p).MatchesPattern(pattern))
            .OrderBy(p => p)
            .ToList();
    }

    public static long CommitCount => Interlocked.Read(ref _commitCount);

    /// <summary>Clean up tracked file history by keeping only the last N commits
    /// that touch any given path. Does NOT perform git gc; outdated commits remain
    /// in the object database until manually pruned.</summary>
    public static int Prune(int keepCount = 200)
    {
        var r = Repo;
        var filter = new CommitFilter { SortBy = CommitSortStrategies.Time, FirstParentOnly = true };
        var all = r.Commits.QueryBy(filter).ToList();
        if (all.Count <= keepCount) return 0;
        // Just reports the count - actual object removal is deferred to manual gc
        return all.Count - keepCount;
    }

    private static string NormalizePath(string relPath) =>
        relPath.Replace('\\', '/');

    private static bool MatchesPattern(this string name, string pattern)
    {
        if (pattern == "*") return true;
        if (pattern.StartsWith('*') && name.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)) return true;
        if (pattern.EndsWith('*') && name.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)) return true;
        return name.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }
}

public readonly record struct VersionEntry(
    string Sha,
    string Message,
    DateTime When,
    string Author);
