using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LTAI.Agent.Tools;

public sealed record RejectedEdit
{
    public string ContentHash { get; init; } = "";
    public string SkillName { get; init; } = "";
    public DateTime RejectedAt { get; init; }
    public string Reason { get; init; } = "";
}

public sealed class SkillRejectedBuffer
{
    private readonly ConcurrentDictionary<string, RejectedEdit> _rejected = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _storePath;
    private readonly TimeSpan _ttl;
    private readonly object _saveLock = new();
    private DateTime _lastCleanup = DateTime.UtcNow;

    private const int MaxEntries = 200;
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromDays(7);

    public SkillRejectedBuffer(string skillsDir, TimeSpan? ttl = null)
    {
        _storePath = Path.Combine(skillsDir, ".skillopt", "rejected.json");
        _ttl = ttl ?? DefaultTtl;
        Load();
    }

    public bool WasRejected(string skillName, string content)
    {
        CleanupIfNeeded();

        var hash = ComputeHash(content);
        var key = BuildKey(skillName, hash);

        if (_rejected.TryGetValue(key, out var edit))
        {
            return DateTime.UtcNow - edit.RejectedAt < _ttl;
        }

        return false;
    }

    public void RecordRejection(string skillName, string content, string reason)
    {
        CleanupIfNeeded();

        var hash = ComputeHash(content);
        var key = BuildKey(skillName, hash);

        _rejected[key] = new RejectedEdit
        {
            ContentHash = hash,
            SkillName = skillName,
            RejectedAt = DateTime.UtcNow,
            Reason = reason
        };

        if (_rejected.Count > MaxEntries)
        {
            var toRemove = _rejected
                .OrderBy(kv => kv.Value.RejectedAt)
                .Take(_rejected.Count - MaxEntries)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var k in toRemove)
                _rejected.TryRemove(k, out _);
        }

        Save();
    }

    public int Count => _rejected.Count;

    public void Clear()
    {
        _rejected.Clear();
        Save();
    }

    private static string BuildKey(string skillName, string hash) =>
        $"{skillName.ToLowerInvariant()}::{hash}";

    private static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes)[..16];
    }

    private void CleanupIfNeeded()
    {
        var now = DateTime.UtcNow;
        if (now - _lastCleanup < TimeSpan.FromHours(1))
            return;

        _lastCleanup = now;
        var cutoff = now - _ttl;
        var stale = _rejected
            .Where(kv => kv.Value.RejectedAt < cutoff)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var k in stale)
            _rejected.TryRemove(k, out _);

        if (stale.Count > 0)
            Save();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_storePath))
            {
                var json = File.ReadAllText(_storePath);
                var loaded = JsonSerializer.Deserialize<List<RejectedEdit>>(json);
                if (loaded != null)
                    foreach (var edit in loaded)
                    {
                        var key = BuildKey(edit.SkillName, edit.ContentHash);
                        _rejected.TryAdd(key, edit);
                    }
            }
        }
        catch { /* best-effort load from persistent store */ }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_storePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            lock (_saveLock)
            {
                var snapshot = _rejected.Values
                    .OrderByDescending(e => e.RejectedAt)
                    .Take(MaxEntries)
                    .ToList();
                File.WriteAllText(_storePath, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch { /* best-effort save to persistent store */ }
    }
}
