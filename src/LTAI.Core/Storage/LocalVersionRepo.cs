using System.Diagnostics;
using System.Text;
using LibGit2Sharp;

namespace LTAI.Core.Storage;

public sealed class LocalVersionRepo : IDisposable
{
    private static LocalVersionRepo? _default;
    public static LocalVersionRepo Default => _default ??= new LocalVersionRepo();

    private readonly string _baseDir;
    private readonly string _gitDir;
    private readonly Signature _committer;
    private readonly Repository _repo;
    private long _commitCount;

    public string RepositoryPath => _gitDir;
    public string BaseDirectory => _baseDir;

    public LocalVersionRepo(string? baseDir = null)
    {
        _baseDir = baseDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".livingtree");
        _gitDir = Path.Combine(_baseDir, ".git");
        _committer = new Signature("LTAI", "ltai@local", DateTimeOffset.Now);
        Directory.CreateDirectory(_baseDir);
        if (!Repository.IsValid(_gitDir))
            Repository.Init(_baseDir);
        _repo = new Repository(_baseDir);
    }

    public void Dispose() => _repo.Dispose();

    private string Resolve(string relPath) => Path.GetFullPath(Path.Combine(_baseDir, relPath));

    public string Commit(string relPath, string message)
    {
        var full = Resolve(relPath);
        if (!File.Exists(full))
            throw new FileNotFoundException($"File not in .livingtree: {relPath}", full);
        _repo.Index.Add(NormalizePath(relPath));
        _repo.Index.Write();
        var c = _repo.Commit(message, _committer, _committer);
        Interlocked.Increment(ref _commitCount);
        return c.Sha;
    }

    public string AtomicCommit(string relPath, string content, string message)
    {
        var full = Resolve(relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var tmp = full + ".tmp." + Guid.NewGuid().ToString("N");
        File.WriteAllText(tmp, content);
        File.Move(tmp, full, true);
        return Commit(relPath, message);
    }

    public IReadOnlyList<VersionEntry> Log(string relPath, int maxCount = 20)
    {
        var result = new List<VersionEntry>();
        var normalized = NormalizePath(relPath);
        var filter = new CommitFilter { SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time };
        foreach (var c in _repo.Commits.QueryBy(filter))
        {
            if (c.Tree[normalized] == null) continue;
            result.Add(new VersionEntry(c.Sha, c.MessageShort.Trim(), c.Author.When.UtcDateTime, c.Author.Name));
            if (result.Count >= maxCount) break;
        }
        return result;
    }

    public string Rollback(string relPath, string commitSha)
    {
        var commit = _repo.Lookup<Commit>(commitSha)
            ?? throw new ArgumentException($"Commit not found: {commitSha}");
        var entry = commit.Tree[NormalizePath(relPath)]
            ?? throw new ArgumentException($"Path '{relPath}' not found in commit {commitSha[..8]}");
        if (entry.Target is not Blob blob)
            throw new InvalidOperationException($"Entry '{relPath}' is not a blob in {commitSha[..8]}");
        var full = Resolve(relPath);
        var text = blob.GetContentText() ?? string.Empty;
        File.WriteAllText(full, text);
        _repo.Index.Add(NormalizePath(relPath));
        _repo.Index.Write();
        var revert = _repo.Commit($"↩ Rollback: {relPath} to {commitSha[..8]}", _committer, _committer);
        return revert.Sha;
    }

    public string Diff(string relPath, string? fromSha = null)
    {
        var normalized = NormalizePath(relPath);
        var tip = _repo.Head.Tip;
        if (tip?.Tree == null) return "(no commits)";
        Tree? oldTree = null;
        if (fromSha != null)
        {
            var c = _repo.Lookup<Commit>(fromSha);
            if (c != null) oldTree = c.Tree;
        }
        else
        {
            var parent = tip.Parents.FirstOrDefault();
            if (parent != null) oldTree = parent.Tree;
        }
        if (oldTree == null) return "(no previous version)";
        var patch = _repo.Diff.Compare<Patch>(oldTree, tip.Tree, new[] { normalized });
        var text = patch.Content;
        return string.IsNullOrEmpty(text) ? "(no changes)" : text;
    }

    public IReadOnlyList<string> ListTracked(string pattern = "*")
    {
        return _repo.Index
            .Where(e => !e.Path.EndsWith("/"))
            .Select(e => e.Path)
            .Where(p => NameMatchesPattern(System.IO.Path.GetFileName(p), pattern))
            .OrderBy(p => p)
            .ToList();
    }

    public long CommitCount => Interlocked.Read(ref _commitCount);

    public int Prune(int keepCount = 200)
    {
        var filter = new CommitFilter { SortBy = CommitSortStrategies.Time, FirstParentOnly = true };
        var all = _repo.Commits.QueryBy(filter).ToList();
        if (all.Count <= keepCount) return 0;
        return all.Count - keepCount;
    }

    private static string NormalizePath(string relPath) => relPath.Replace('\\', '/');

    private static bool NameMatchesPattern(string name, string pattern)
    {
        if (pattern == "*") return true;
        if (pattern.StartsWith('*') && name.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)) return true;
        if (pattern.EndsWith('*') && name.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)) return true;
        return name.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }
}

public readonly record struct VersionEntry(string Sha, string Message, DateTime When, string Author);
