using System.Security.Cryptography;
using LTAI.Execution.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Execution.Session;

public sealed class SideGit
{
    private readonly string _basePath = ".livingtree/snapshots";
    private string _workspace;
    private readonly List<TurnSnapshot> _turns = new();
    private int _turnCounter;
    private readonly ILogger _logger;

    private static readonly Lazy<SideGit> _lazyInstance = new(() => new SideGit("."));
    private static readonly Lock _lock = new();

    public static SideGit Instance => _lazyInstance.Value;

    private static readonly HashSet<string> DefaultExclusions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".livingtree", "bin", "obj", "node_modules", "dist", "__pycache__", ".venv"
    };

    private SideGit(string workspace, ILogger? logger = null)
    {
        _workspace = workspace;
        _logger = logger ?? NullLogger.Instance;
    }

    public void SetWorkspace(string workspace)
    {
        _workspace = workspace;
        _logger.LogDebug("SideGit workspace set to {Workspace}", workspace);
    }

    public TurnSnapshot PreTurn()
    {
        lock (_lock)
        {
            var turnId = $"{_turnCounter}";
            var relativeDir = Path.Combine(_basePath, $"turn_{turnId}");
            var snapshotDir = Path.Combine(_workspace, relativeDir);
            var absoluteSnapshot = Path.GetFullPath(snapshotDir);

            CopyDirectory(_workspace, absoluteSnapshot, DefaultExclusions);

            _turnCounter++;

            var snapshot = new TurnSnapshot
            {
                TurnId = turnId,
                Workspace = _workspace,
                SnapshotPath = absoluteSnapshot,
                Timestamp = DateTime.UtcNow
            };

            _turns.Add(snapshot);

            _logger.LogInformation("PreTurn snapshot saved: {TurnId} -> {Path}", turnId, absoluteSnapshot);

            return snapshot;
        }
    }

    public TurnSnapshot PostTurn(string turnId)
    {
        lock (_lock)
        {
            var beforeDir = Path.Combine(_workspace, _basePath, $"turn_{turnId}");
            var afterDir = Path.Combine(_workspace, _basePath, $"turn_{turnId}_after");
            var absoluteAfter = Path.GetFullPath(afterDir);

            CopyDirectory(_workspace, absoluteAfter, DefaultExclusions);

            var changedFiles = DiffDirectories(beforeDir, absoluteAfter);

            _logger.LogInformation(
                "PostTurn snapshot saved: {TurnId}_after, {Count} files changed",
                turnId, changedFiles.Count);

            return new TurnSnapshot
            {
                TurnId = $"{turnId}_after",
                Workspace = _workspace,
                SnapshotPath = absoluteAfter,
                Timestamp = DateTime.UtcNow
            };
        }
    }

    public void Restore(string turnId)
    {
        lock (_lock)
        {
            var snapshotDir = Path.Combine(_workspace, _basePath, $"turn_{turnId}");
            var absoluteSnapshot = Path.GetFullPath(snapshotDir);

            if (!Directory.Exists(absoluteSnapshot))
            {
                _logger.LogWarning("Snapshot not found for restore: {TurnId} at {Path}", turnId, absoluteSnapshot);
                return;
            }

            CopyDirectory(absoluteSnapshot, _workspace, new HashSet<string>());

            _logger.LogInformation("Workspace restored to turn {TurnId}", turnId);
        }
    }

    public void RevertTurn(string turnId)
    {
        Restore(turnId);
    }

    public List<TurnSnapshot> ListTurns()
    {
        lock (_lock)
        {
            return _turns.ToList();
        }
    }

    public void Cleanup(int keepLast = 10)
    {
        lock (_lock)
        {
            var snapshotsRoot = Path.Combine(_workspace, _basePath);
            if (!Directory.Exists(snapshotsRoot))
                return;

            var dirs = Directory.GetDirectories(snapshotsRoot)
                .Select(d => new DirectoryInfo(d))
                .OrderByDescending(d => d.CreationTime)
                .ToList();

            var toRemove = dirs.Skip(keepLast).ToList();

            foreach (var dir in toRemove)
            {
                try
                {
                    Directory.Delete(dir.FullName, recursive: true);
                    _logger.LogDebug("Cleaned up snapshot directory: {Dir}", dir.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clean up snapshot directory: {Dir}", dir.Name);
                }
            }

            if (toRemove.Count > 0)
                _logger.LogInformation("Cleaned up {Count} old snapshot directories, kept {Kept}", toRemove.Count, keepLast);
        }
    }

    public Dictionary<string, object?> GetStats()
    {
        lock (_lock)
        {
            string? lastTurnId = _turns.Count > 0 ? _turns[^1].TurnId : null;

            return new Dictionary<string, object?>
            {
                ["turn_count"] = _turnCounter,
                ["workspace"] = _workspace,
                ["last_turn_id"] = lastTurnId
            };
        }
    }

    private static void CopyDirectory(string sourceDir, string destDir, HashSet<string> exclusions)
    {
        var sourceFull = Path.GetFullPath(sourceDir);
        var destFull = Path.GetFullPath(destDir);

        if (!Directory.Exists(sourceFull))
            return;

        Directory.CreateDirectory(destFull);

        foreach (var file in Directory.GetFiles(sourceFull, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceFull, file);
            var parts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            var skip = false;
            foreach (var part in parts.Take(parts.Length - 1))
            {
                if (part.StartsWith('.') || exclusions.Contains(part))
                {
                    skip = true;
                    break;
                }
            }

            if (skip)
                continue;

            var destFile = Path.Combine(destFull, relativePath);
            var destFileDir = Path.GetDirectoryName(destFile);
            if (!string.IsNullOrEmpty(destFileDir))
                Directory.CreateDirectory(destFileDir);

            File.Copy(file, destFile, overwrite: true);
        }
    }

    private static List<string> DiffDirectories(string beforeDir, string afterDir)
    {
        var changed = new List<string>();

        var beforeFull = Path.GetFullPath(beforeDir);
        var afterFull = Path.GetFullPath(afterDir);

        if (!Directory.Exists(beforeFull) && !Directory.Exists(afterFull))
            return changed;

        var beforeFiles = Directory.Exists(beforeFull)
            ? Directory.GetFiles(beforeFull, "*", SearchOption.AllDirectories)
            : Array.Empty<string>();

        var afterFiles = Directory.Exists(afterFull)
            ? Directory.GetFiles(afterFull, "*", SearchOption.AllDirectories)
            : Array.Empty<string>();

        var beforeRelative = new Dictionary<string, string>();
        foreach (var f in beforeFiles)
        {
            var rel = Path.GetRelativePath(beforeFull, f);
            beforeRelative[rel] = f;
        }

        var afterRelative = new Dictionary<string, string>();
        foreach (var f in afterFiles)
        {
            var rel = Path.GetRelativePath(afterFull, f);
            afterRelative[rel] = f;
        }

        foreach (var (rel, beforePath) in beforeRelative)
        {
            if (!afterRelative.TryGetValue(rel, out var afterPath))
            {
                changed.Add(rel);
            }
            else
            {
                var beforeBytes = File.ReadAllBytes(beforePath);
                var afterBytes = File.ReadAllBytes(afterPath);
                if (!beforeBytes.AsSpan().SequenceEqual(afterBytes))
                    changed.Add(rel);
            }
        }

        foreach (var rel in afterRelative.Keys)
        {
            if (!beforeRelative.ContainsKey(rel))
                changed.Add(rel);
        }

        return changed;
    }

    private static string HashFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }
}
