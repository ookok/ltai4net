using System.Collections.Concurrent;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Workflows;

public sealed record BlameAttribution
{
    public string FilePath { get; init; } = "";
    public int LineNumber { get; init; }
    public string CommitSha { get; init; } = "";
    public string Author { get; init; } = "";
    public DateTimeOffset When { get; init; }
    public string Message { get; init; } = "";
    public double AgentAttributionScore { get; init; }
}

public sealed record SnapshotComparison
{
    public string Tag1 { get; init; } = "";
    public string Tag2 { get; init; } = "";
    public List<string> AddedFiles { get; init; } = new();
    public List<string> ModifiedFiles { get; init; } = new();
    public List<string> DeletedFiles { get; init; } = new();
    public int Insertions { get; init; }
    public int Deletions { get; init; }
    public double SimilarityScore { get; init; }
    public List<string> KeyChanges { get; init; } = new();
}

public sealed class GitExperimentBridge
{
    private readonly string _repoPath;
    private readonly GitWorktreeManager _worktreeManager;
    private readonly ILogger<GitExperimentBridge> _logger;
    private readonly ConcurrentDictionary<string, List<BlameAttribution>> _blameCache = new();

    public GitExperimentBridge(
        GitWorktreeManager worktreeManager,
        ILogger<GitExperimentBridge>? logger = null)
    {
        _worktreeManager = worktreeManager;
        _repoPath = worktreeManager.GetWorktreesDir()
            .Replace("\\.livingtree\\worktrees", "")
            .Replace("/.livingtree/worktrees", "");
        _logger = logger ?? NullLogger<GitExperimentBridge>.Instance;
    }

    public Task<List<BlameAttribution>> GetBlameAttributionAsync(
        string filePath,
        int startLine = 1,
        int? endLine = null,
        CancellationToken ct = default)
    {
        if (_blameCache.TryGetValue(filePath, out var cached))
            return Task.FromResult(cached);

        var attributions = new List<BlameAttribution>();

        try
        {
            using var repo = new Repository(_repoPath);
            var blameHunks = repo.Blame(filePath, new BlameOptions
            {
                MinLine = startLine,
                MaxLine = endLine ?? int.MaxValue
            });

            foreach (var hunk in blameHunks)
            {
                attributions.Add(new BlameAttribution
                {
                    FilePath = filePath,
                    LineNumber = hunk.FinalStartLineNumber,
                    CommitSha = hunk.FinalCommit.Sha[..8],
                    Author = hunk.FinalSignature?.Name ?? "unknown",
                    When = hunk.FinalSignature?.When ?? DateTimeOffset.MinValue,
                    Message = hunk.FinalCommit.MessageShort ?? "",
                    AgentAttributionScore = ComputeAgentAttributionScore(hunk.FinalSignature?.Name ?? "")
                });
            }

            _blameCache[filePath] = attributions;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Blame failed for {File}", filePath);
        }

        return Task.FromResult(attributions);
    }

    public Task<Dictionary<string, double>> GetFileAttributionSummaryAsync(
        CancellationToken ct = default)
    {
        var summary = new Dictionary<string, double>();

        try
        {
            using var repo = new Repository(_repoPath);
            var status = repo.RetrieveStatus();
            var allFiles = status.Modified
                .Select(e => e.FilePath)
                .Concat(status.Added.Select(e => e.FilePath))
                .Concat(status.RenamedInWorkDir.Select(e => e.FilePath))
                .Distinct()
                .ToList();

            foreach (var file in allFiles)
            {
                try
                {
                    var blameHunks = repo.Blame(file);
                    var totalLines = 0;
                    var agentLines = 0;

                    foreach (var hunk in blameHunks)
                    {
                        var count = hunk.LineCount;
                        totalLines += count;
                        if (IsAgentCommit(hunk.FinalSignature?.Name ?? ""))
                            agentLines += count;
                    }

                    if (totalLines > 0)
                        summary[file] = (double)agentLines / totalLines;
                }
                catch
                {
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "File attribution summary failed");
        }

        return Task.FromResult(summary);
    }

    public async Task<SnapshotComparison> CompareSnapshotsAsync(
        string tag1,
        string tag2,
        CancellationToken ct = default)
    {
        try
        {
            using var repo = new Repository(_repoPath);
            var commit1 = repo.Tags[tag1]?.Target as Commit;
            var commit2 = repo.Tags[tag2]?.Target as Commit;

            if (commit1 == null || commit2 == null)
            {
                _logger.LogWarning("Snapshot tags not found: {Tag1}, {Tag2}", tag1, tag2);
                return new SnapshotComparison { Tag1 = tag1, Tag2 = tag2 };
            }

            var changes = repo.Diff.Compare<TreeChanges>(commit1.Tree, commit2.Tree);
            var patch = repo.Diff.Compare<Patch>(commit1.Tree, commit2.Tree);

            var totalFiles = changes.Added.Count() + changes.Modified.Count();
            var sameFiles = changes.Added.Count(c => changes.Modified.Any(m => m.Path == c.Path));

            return new SnapshotComparison
            {
                Tag1 = tag1,
                Tag2 = tag2,
                AddedFiles = changes.Added.Select(c => c.Path).ToList(),
                ModifiedFiles = changes.Modified.Select(c => c.Path).ToList(),
                DeletedFiles = changes.Deleted.Select(c => c.Path).ToList(),
                Insertions = patch?.Count(c => c.Status == ChangeKind.Added) ?? 0,
                Deletions = patch?.Count(c => c.Status == ChangeKind.Deleted) ?? 0,
                SimilarityScore = totalFiles > 0 ? (double)sameFiles / totalFiles : 0,
                KeyChanges = changes.Modified
                    .Take(10)
                    .Select(c => c.Path)
                    .ToList()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Snapshot comparison failed: {Tag1} vs {Tag2}", tag1, tag2);
            return new SnapshotComparison { Tag1 = tag1, Tag2 = tag2 };
        }
    }

    public async Task<string?> CreateSnapshotAsync(string tagName, string? message = null, CancellationToken ct = default)
    {
        try
        {
            using var repo = new Repository(_repoPath);
            var head = repo.Head.Tip;
            if (head == null) return null;

            var fullMessage = message ?? $"Snapshot {tagName} at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}";
            var tag = repo.Tags.Add(tagName, head,
                repo.Config.BuildSignature(DateTimeOffset.UtcNow)
                    ?? new Signature("LTAI Agent", "ltai@agent.local", DateTimeOffset.UtcNow),
                fullMessage);

            _logger.LogInformation("Created snapshot tag: {Tag} -> {Commit}",
                tagName, head.Sha[..8]);

            return tag.CanonicalName;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create snapshot tag: {Tag}", tagName);
            return null;
        }
    }

    public async Task<string?> ArchiveCommitAsync(
        string outputPath,
        string? commitRef = null,
        CancellationToken ct = default)
    {
        try
        {
            using var repo = new Repository(_repoPath);
            var commit = string.IsNullOrEmpty(commitRef)
                ? repo.Head.Tip
                : repo.Lookup<Commit>(commitRef)
                  ?? repo.Branches[commitRef]?.Tip;

            if (commit == null)
            {
                _logger.LogWarning("Commit not found: {Ref}", commitRef);
                return null;
            }

            var outputDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                Directory.CreateDirectory(outputDir);

            repo.ObjectDatabase.Archive(commit, outputPath);

            _logger.LogInformation("Archived commit {Sha} to {Path}",
                commit.Sha[..8], outputPath);

            return outputPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Archive failed for {Commit}", commitRef);
            return null;
        }
    }

    public Task<List<string>> ListSnapshotTagsAsync(CancellationToken ct = default)
    {
        var tags = new List<string>();
        try
        {
            using var repo = new Repository(_repoPath);
            tags = repo.Tags
                .Where(t => t.FriendlyName.StartsWith("ltai-snapshot-", StringComparison.OrdinalIgnoreCase))
                .Select(t => t.FriendlyName)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list snapshot tags");
        }

        return Task.FromResult(tags);
    }

    private static double ComputeAgentAttributionScore(string author)
    {
        if (string.IsNullOrEmpty(author)) return 0;
        var agentPatterns = new[] { "LTAI ", "ltai", "agent", "ai-", "copilot", "openai", "claude" };
        return agentPatterns.Any(p => author.Contains(p, StringComparison.OrdinalIgnoreCase)) ? 1.0 : 0.3;
    }

    private static bool IsAgentCommit(string author)
    {
        return ComputeAgentAttributionScore(author) >= 0.8;
    }
}
