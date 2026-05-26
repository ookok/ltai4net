using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LTAI.Tools.Tools;

public record ToolSnapshot(string Name, DateTime Timestamp, Dictionary<string, object> ToolState,
    Dictionary<string, object> KbState, Dictionary<string, string> FileHashes, string Description);

public sealed class ToolOrchestrator
{
    private readonly string _snapshotDir;
    private readonly object _lock = new();

    public ToolOrchestrator()
    {
        _snapshotDir = Path.Combine(Environment.GetEnvironmentVariable("LTAI_WORKSPACE") ?? Directory.GetCurrentDirectory(), ".livingtree", "snapshots");
        Directory.CreateDirectory(_snapshotDir);
    }

    public ToolSnapshot SnapshotSave(string description, object? toolRegistryState, object? kbState,
        List<string>? trackedFiles = null)
    {
        var fileHashes = new Dictionary<string, string>();
        trackedFiles ??= new List<string>();
        foreach (var file in trackedFiles)
        {
            if (File.Exists(file))
            {
                using var sha = SHA256.Create();
                var bytes = File.ReadAllBytes(file);
                fileHashes[file] = Convert.ToHexString(sha.ComputeHash(bytes));
            }
        }

        var ts = JsonSerializer.SerializeToElement(toolRegistryState ?? new { });
        var ks = JsonSerializer.SerializeToElement(kbState ?? new { });

        var toolState = JsonSerializer.Deserialize<Dictionary<string, object>>(ts.GetRawText()) ?? new();
        var kbStateDict = JsonSerializer.Deserialize<Dictionary<string, object>>(ks.GetRawText()) ?? new();

        var snapshot = new ToolSnapshot($"snapshot_{DateTime.UtcNow:yyyyMMdd_HHmmss}", DateTime.UtcNow,
            toolState, kbStateDict, fileHashes, description);

        lock (_lock)
        {
            var path = Path.Combine(_snapshotDir, $"{snapshot.Name}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
        }

        return snapshot;
    }

    public List<ToolSnapshot> SnapshotList()
    {
        lock (_lock)
        {
            return Directory.GetFiles(_snapshotDir, "*.json")
                .OrderByDescending(f => new FileInfo(f).LastWriteTime)
                .Take(50)
                .Select(f =>
                {
                    try { return JsonSerializer.Deserialize<ToolSnapshot>(File.ReadAllText(f)); }
                    catch { return null; }
                })
                .Where(s => s != null)
                .Cast<ToolSnapshot>()
                .ToList();
        }
    }

    public ToolSnapshot? SnapshotRestore(string name)
    {
        lock (_lock)
        {
            var files = Directory.GetFiles(_snapshotDir, "*.json");
            var match = files.FirstOrDefault(f =>
                f.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileNameWithoutExtension(f).Contains(name, StringComparison.OrdinalIgnoreCase));

            if (match == null) return null;
            return JsonSerializer.Deserialize<ToolSnapshot>(File.ReadAllText(match));
        }
    }

    public Dictionary<string, List<string>> SnapshotDiff(string name1, string name2)
    {
        var s1 = SnapshotRestore(name1);
        var s2 = SnapshotRestore(name2);
        if (s1 == null || s2 == null) return new();

        var diff = new Dictionary<string, List<string>> { ["file_changes"] = new() };
        foreach (var (path, hash) in s1.FileHashes)
        {
            if (s2.FileHashes.TryGetValue(path, out var h2) && h2 != hash)
                diff["file_changes"].Add($"{path} (modified)");
            else if (!s2.FileHashes.ContainsKey(path))
                diff["file_changes"].Add($"{path} (removed)");
        }
        foreach (var path in s2.FileHashes.Keys.Where(p => !s1.FileHashes.ContainsKey(p)))
            diff["file_changes"].Add($"{path} (added)");

        return diff;
    }
}
