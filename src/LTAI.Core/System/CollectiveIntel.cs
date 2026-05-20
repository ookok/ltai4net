using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LTAI.Core.System;

public enum MemoryTier
{
    Hot,
    Warm,
    Cold
}

public sealed class TieredMemory
{
    public string MemoryId { get; set; } = "";
    public string Content { get; set; } = "";
    public MemoryTier Tier { get; set; } = MemoryTier.Warm;
    public int HitCount { get; set; }
    public double LastHit { get; set; }
    public double CreatedAt { get; set; }
    public int ValidatedCount { get; set; }
    public string SourceSession { get; set; } = "";
    public List<string> Tags { get; set; } = new();

    public double Hotness
    {
        get
        {
            if (LastHit == 0) return 0;
            var days = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - LastHit) / 86400.0;
            return Math.Exp(-0.15 * days);
        }
    }

    public double SearchBoost => Tier switch
    {
        MemoryTier.Hot => 1.5 * Math.Max(0.1, Hotness),
        MemoryTier.Warm => 1.0 * Math.Max(0.1, Hotness),
        MemoryTier.Cold => 0.5 * Math.Max(0.1, Hotness),
        _ => 1.0
    };
}

public sealed class MemoryTierManager
{
    private static readonly Lazy<MemoryTierManager> _instance = new(() => new MemoryTierManager());
    public static MemoryTierManager Instance => _instance.Value;

    private const int HotThreshold = 5;
    private const int ColdThresholdDays = 30;
    private const int CrystalThreshold = 3;

    private readonly Dictionary<string, TieredMemory> _memories = new();
    private readonly string _tierFile;
    private readonly object _lock = new();

    private MemoryTierManager()
    {
        _tierFile = Path.Combine(".livingtree", "collective", "memory_tiers.json");
        Load();
    }

    private void Load()
    {
        var dir = Path.GetDirectoryName(_tierFile);
        if (dir != null) Directory.CreateDirectory(dir);

        if (!File.Exists(_tierFile)) return;

        try
        {
            var json = File.ReadAllText(_tierFile);
            var items = JsonSerializer.Deserialize<List<JsonElement>>(json);
            if (items == null) return;

            foreach (var item in items)
            {
                var m = new TieredMemory
                {
                    MemoryId = item.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                    Content = item.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "",
                    Tier = item.TryGetProperty("tier", out var t) && Enum.TryParse<MemoryTier>(t.GetString(), true, out var tier) ? tier : MemoryTier.Warm,
                    HitCount = item.TryGetProperty("hits", out var h) ? h.GetInt32() : 0,
                    LastHit = item.TryGetProperty("last_hit", out var lh) ? lh.GetDouble() : 0,
                    CreatedAt = item.TryGetProperty("created_at", out var ca) ? ca.GetDouble() : 0,
                    ValidatedCount = item.TryGetProperty("validated", out var v) ? v.GetInt32() : 0,
                    SourceSession = item.TryGetProperty("session", out var s) ? s.GetString() ?? "" : ""
                };
                if (item.TryGetProperty("tags", out var tags))
                {
                    m.Tags = tags.EnumerateArray().Select(t => t.GetString() ?? "").ToList();
                }
                _memories[m.MemoryId] = m;
            }
        }
        catch
        {
        }
    }

    private void Save()
    {
        lock (_lock)
        {
            var items = _memories.Values.Select(m => new Dictionary<string, object>
            {
                ["id"] = m.MemoryId,
                ["content"] = m.Content,
                ["tier"] = m.Tier.ToString().ToLower(),
                ["hits"] = m.HitCount,
                ["last_hit"] = m.LastHit,
                ["created_at"] = m.CreatedAt,
                ["validated"] = m.ValidatedCount,
                ["session"] = m.SourceSession,
                ["tags"] = m.Tags,
                ["hotness"] = Math.Round(m.Hotness, 3)
            }).ToList();
            File.WriteAllText(_tierFile, JsonSerializer.Serialize(items));
        }
    }

    public string Store(string content, string sourceSession = "", List<string>? tags = null)
    {
        var raw = $"{content}{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        var mid = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(raw)))[..12];

        if (tags == null || tags.Count == 0)
            tags = AutoClassify(content);

        var m = new TieredMemory
        {
            MemoryId = mid,
            Content = content,
            Tier = MemoryTier.Warm,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            SourceSession = sourceSession,
            Tags = tags
        };

        lock (_lock)
        {
            _memories[mid] = m;
        }
        Save();
        return mid;
    }

    private static List<string> AutoClassify(string content)
    {
        var tags = new List<string>();
        var cl = content.ToLower();

        var memoryRules = new (string Category, string[] Keywords)[]
        {
            ("error", ["error", "bug", "fail", "crash"]),
            ("fix", ["fix", "solution", "resolve"]),
            ("pattern", ["pattern", "template"]),
            ("security", ["security", "vuln", "attack"]),
            ("config", ["config", "setup", "install"]),
            ("api", ["api", "endpoint"])
        };
        foreach (var (category, keywords) in memoryRules)
        {
            if (keywords.Any(k => cl.Contains(k)))
                tags.Add(category);
        }

        return tags.Count > 0 ? tags : new List<string> { "general" };
    }

    public TieredMemory? Hit(string memoryId, bool validated = false)
    {
        TieredMemory? m;
        lock (_lock)
        {
            if (!_memories.TryGetValue(memoryId, out m))
                return null;
        }

        m.HitCount++;
        m.LastHit = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (validated)
            m.ValidatedCount++;

        if (m.HitCount >= HotThreshold && m.Tier != MemoryTier.Hot)
            m.Tier = MemoryTier.Hot;

        Save();
        return m;
    }

    public void ApplyDecay()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var demoted = 0;
        var removed = 0;

        lock (_lock)
        {
            foreach (var mid in _memories.Keys.ToList())
            {
                var m = _memories[mid];
                var days = (now - Math.Max(m.LastHit, m.CreatedAt)) / 86400.0;

                if (days > 90)
                {
                    _memories.Remove(mid);
                    removed++;
                }
                else if (days > ColdThresholdDays && m.Tier != MemoryTier.Cold)
                {
                    m.Tier = MemoryTier.Cold;
                    demoted++;
                }
            }
        }

        if (demoted > 0 || removed > 0)
            Save();
    }

    public List<TieredMemory> Search(string query, int limit = 10)
    {
        var ql = query.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var scored = new List<(double score, TieredMemory memory)>();

        foreach (var m in _memories.Values)
        {
            var ml = m.Content.ToLower();
            var overlap = ql.Length > 0 ? (double)ql.Count(w => ml.Contains(w)) / ql.Length : 0;
            var tagBonus = m.Tags.Count(t => ql.Any(q => t.Contains(q))) * 0.2;
            var score = (overlap + tagBonus) * m.SearchBoost;
            scored.Add((score, m));
        }

        return scored.OrderByDescending(x => x.score).Take(limit).Select(x => x.memory).ToList();
    }

    public List<TieredMemory> GetCrystallizationCandidates()
    {
        lock (_lock)
        {
            return _memories.Values
                .Where(m => m.ValidatedCount >= CrystalThreshold && m.Tier == MemoryTier.Hot)
                .ToList();
        }
    }

    public Dictionary<string, object> GetStats()
    {
        var hot = 0;
        var warm = 0;
        var cold = 0;
        lock (_lock)
        {
            foreach (var m in _memories.Values)
            {
                switch (m.Tier)
                {
                    case MemoryTier.Hot: hot++; break;
                    case MemoryTier.Warm: warm++; break;
                    case MemoryTier.Cold: cold++; break;
                }
            }
        }

        return new Dictionary<string, object>
        {
            ["total"] = _memories.Count,
            ["tiers"] = new Dictionary<string, int> { ["hot"] = hot, ["warm"] = warm, ["cold"] = cold },
            ["crystallization_candidates"] = GetCrystallizationCandidates().Count
        };
    }
}

public sealed class AgentBlueprint
{
    public string BlueprintId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Persona { get; set; } = "";
    public Dictionary<string, object> ModelConfig { get; set; } = new();
    public List<string> SkillNames { get; set; } = new();
    public List<string> MemorySnapshot { get; set; } = new();
    public double CreatedAt { get; set; }
    public List<string> Tags { get; set; } = new();
}

public sealed class BlueprintHub
{
    private static readonly Lazy<BlueprintHub> _instance = new(() => new BlueprintHub());
    public static BlueprintHub Instance => _instance.Value;

    private readonly Dictionary<string, AgentBlueprint> _blueprints = new();
    private readonly string _blueprintDir;

    private BlueprintHub()
    {
        _blueprintDir = Path.Combine(".livingtree", "collective", "blueprints");
        Directory.CreateDirectory(_blueprintDir);
        LoadAll();
    }

    private void LoadAll()
    {
        foreach (var f in Directory.GetFiles(_blueprintDir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(f);
                var bp = JsonSerializer.Deserialize<AgentBlueprint>(json);
                if (bp != null)
                    _blueprints[bp.BlueprintId] = bp;
            }
            catch
            {
            }
        }
    }

    public string Publish(string name, string description = "", string persona = "", List<string>? tags = null)
    {
        var raw = $"{name}{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        var bid = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(raw)))[..10];

        var bp = new AgentBlueprint
        {
            BlueprintId = bid,
            Name = name,
            Description = description,
            Persona = persona,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Tags = tags ?? new List<string>()
        };

        _blueprints[bid] = bp;
        File.WriteAllText(Path.Combine(_blueprintDir, $"{bid}.json"), JsonSerializer.Serialize(bp));
        return bid;
    }

    public bool ImportBlueprint(string blueprintId)
    {
        if (_blueprints.TryGetValue(blueprintId, out _))
            return true;

        var f = Path.Combine(_blueprintDir, $"{blueprintId}.json");
        if (!File.Exists(f)) return false;

        try
        {
            var json = File.ReadAllText(f);
            var bp = JsonSerializer.Deserialize<AgentBlueprint>(json);
            if (bp != null)
            {
                _blueprints[blueprintId] = bp;
                return true;
            }
        }
        catch
        {
        }
        return false;
    }

    public List<Dictionary<string, object>> ListBlueprints()
    {
        return _blueprints.Values.Select(bp => new Dictionary<string, object>
        {
            ["id"] = bp.BlueprintId,
            ["name"] = bp.Name,
            ["description"] = bp.Description,
            ["persona"] = bp.Persona,
            ["skills"] = bp.SkillNames,
            ["memories"] = bp.MemorySnapshot.Count,
            ["created_at"] = bp.CreatedAt,
            ["tags"] = bp.Tags
        }).ToList();
    }

    public bool Delete(string blueprintId)
    {
        _blueprints.Remove(blueprintId);
        var f = Path.Combine(_blueprintDir, $"{blueprintId}.json");
        if (File.Exists(f))
            File.Delete(f);
        return true;
    }
}
