using System.Text.Json;

namespace LTAI.Agent.Tools;

/// <summary>
/// Tracks skill usage timestamps for TTL-based expiry.
/// Skills not used within the TTL period are auto-forgotten.
/// Data stored in .livingtree/skill_usage.json.
/// </summary>
public sealed class SkillUsageTracker
{
    private readonly string _filePath;
    private readonly TimeSpan _ttl;
    private Dictionary<string, DateTime> _usage;

    public SkillUsageTracker(string filePath, TimeSpan ttl)
    {
        _filePath = filePath;
        _ttl = ttl;
        _usage = Load();
    }

    /// <summary>Record that a skill was used now.</summary>
    public void Touch(string skillName)
    {
        _usage[skillName] = DateTime.UtcNow;
        Save();
    }

    /// <summary>Forget a specific skill (remove from tracking).</summary>
    public void Forget(string skillName)
    {
        _usage.Remove(skillName);
        Save();
    }

    /// <summary>Check if a skill is expired (not used within TTL).</summary>
    public bool IsExpired(string skillName)
    {
        if (!_usage.TryGetValue(skillName, out var lastUsed))
            return false; // never used = not expired (allow first use)
        return DateTime.UtcNow - lastUsed > _ttl;
    }

    /// <summary>Get all active (non-expired) skill names.</summary>
    public IEnumerable<string> ActiveSkills =>
        _usage.Where(kv => !IsExpired(kv.Key)).Select(kv => kv.Key);

    /// <summary>Get all tracked skills with timestamps.</summary>
    public Dictionary<string, DateTime> All => _usage;

    private Dictionary<string, DateTime> Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<Dictionary<string, DateTime>>(json) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_usage));
        }
        catch { }
    }
}
