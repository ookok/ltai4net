using System.Text.Json;

namespace LTAI.TreeLLM.Prompting;

public sealed class PromptTemplate
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Content { get; set; } = "";
    public int Version { get; set; } = 1;
    public string Goal { get; set; } = "";
    public string Constraints { get; set; } = "";
    public List<string> DomainTerms { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public bool Deprecated { get; set; }
    public string DeprecatedReason { get; set; } = "";

    public string Render()
    {
        var parts = new List<string> { Content };
        if (!string.IsNullOrEmpty(Goal))
            parts.Insert(0, $"# Goal\n{Goal}");
        if (!string.IsNullOrEmpty(Constraints))
            parts.Add($"## Constraints\n{Constraints}");
        if (DomainTerms.Count > 0)
            parts.Add($"## Domain Terms\n{string.Join(", ", DomainTerms)}");
        return string.Join("\n\n", parts);
    }
}

public sealed class AbTest
{
    public string TestId { get; set; } = "";
    public string PromptId { get; set; } = "";
    public int VersionA { get; set; }
    public int VersionB { get; set; }
    public int CountA { get; set; }
    public int CountB { get; set; }
    public int WinA { get; set; }
    public int WinB { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    public string? GetWinner()
    {
        var total = CountA + CountB;
        if (total < 20) return null;
        var rateA = CountA > 0 ? (double)WinA / CountA : 0;
        var rateB = CountB > 0 ? (double)WinB / CountB : 0;
        if (rateA > rateB + 0.1) return "A";
        if (rateB > rateA + 0.1) return "B";
        return null;
    }
}

public sealed class PromptVersionManager
{
    private static readonly Lazy<PromptVersionManager> _instance = new(() => new PromptVersionManager());
    public static PromptVersionManager Instance => _instance.Value;

    private readonly Dictionary<string, List<PromptTemplate>> _versions = new();
    private readonly Dictionary<string, AbTest> _abTests = new();
    private readonly List<Dictionary<string, object>> _usageLog = new();
    private readonly string _dataDir;
    private readonly object _lock = new();

    private PromptVersionManager()
    {
        _dataDir = global::System.IO.Path.Combine(".livingtree", "prompts");
        global::System.IO.Directory.CreateDirectory(_dataDir);
        Load();
        if (_versions.Count == 0) SeedDefaults();
    }

    public PromptTemplate Register(string name, string content, string goal = "", string constraints = "")
    {
        var id = SanitizeId(name);
        lock (_lock)
        {
            var version = 1;
            if (_versions.TryGetValue(id, out var existing) && existing.Count > 0)
                version = existing.Max(v => v.Version) + 1;

            var tmpl = new PromptTemplate
            {
                Id = id, Name = name, Content = content,
                Version = version, Goal = goal, Constraints = constraints
            };

            if (!_versions.ContainsKey(id))
                _versions[id] = new List<PromptTemplate>();
            _versions[id].Add(tmpl);
        }
        Save();
        return Get(id, version: -1)!;
    }

    public PromptTemplate? Get(string name, int version = -1)
    {
        var id = SanitizeId(name);
        lock (_lock)
        {
            if (!_versions.TryGetValue(id, out var list) || list.Count == 0) return null;
            if (version <= 0) return list.Last(v => !v.Deprecated);
            return list.FirstOrDefault(v => v.Version == version);
        }
    }

    public List<PromptTemplate> ListVersions(string name)
    {
        var id = SanitizeId(name);
        lock (_lock) { return _versions.GetValueOrDefault(id)?.ToList() ?? new(); }
    }

    public bool Deprecate(string name, string reason = "")
    {
        var tmpl = Get(name);
        if (tmpl == null) return false;
        tmpl.Deprecated = true;
        tmpl.DeprecatedReason = reason;
        Save();
        return true;
    }

    public PromptTemplate? Rollback(string name, int targetVersion)
    {
        var current = Get(name);
        if (current == null) return null;
        Deprecate(name, "rolled back");
        return Register(name, Get(name, targetVersion)?.Content ?? current.Content, current.Goal, current.Constraints);
    }

    public AbTest StartAbTest(string name, int versionA, int versionB)
    {
        var test = new AbTest
        {
            TestId = Guid.NewGuid().ToString("N")[..8],
            PromptId = SanitizeId(name),
            VersionA = versionA, VersionB = versionB
        };
        lock (_lock) { _abTests[test.TestId] = test; }
        return test;
    }

    public void RecordAbResult(string testId, bool isA, bool won)
    {
        AbTest? test;
        lock (_lock) { _abTests.TryGetValue(testId, out test); }
        if (test == null) return;
        if (isA) { test.CountA++; if (won) test.WinA++; }
        else { test.CountB++; if (won) test.WinB++; }
    }

    public Dictionary<string, object> GetAbResults(string testId)
    {
        AbTest? test;
        lock (_lock) { _abTests.TryGetValue(testId, out test); }
        if (test == null) return new() { ["error"] = "not found" };
        return new()
        {
            ["test_id"] = test.TestId, ["prompt_id"] = test.PromptId,
            ["count_a"] = test.CountA, ["count_b"] = test.CountB,
            ["win_a"] = test.WinA, ["win_b"] = test.WinB,
            ["winner"] = test.GetWinner() ?? "undecided"
        };
    }

    public void RecordUsage(string name, int version, double quality, int tokens)
    {
        _usageLog.Add(new Dictionary<string, object>
        {
            ["prompt"] = name, ["version"] = version,
            ["quality"] = quality, ["tokens"] = tokens,
            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        });
        if (_usageLog.Count > 2000) _usageLog.RemoveRange(0, 500);
        if (_usageLog.Count % 20 == 0) SaveUsage();
    }

    public string Diff(string name, int v1, int v2)
    {
        var a = Get(name, v1);
        var b = Get(name, v2);
        if (a == null) return $"Version {v1} not found";
        if (b == null) return $"Version {v2} not found";
        var lines = new List<string> { $"Diff {name} v{v1} → v{v2}:" };
        if (a.Content.Length != b.Content.Length)
            lines.Add($"  Length: {a.Content.Length} → {b.Content.Length} (+{b.Content.Length - a.Content.Length})");
        lines.Add($"  Goal: {(a.Goal == b.Goal ? "unchanged" : "changed")}");
        lines.Add($"  Constraints: {(a.Constraints == b.Constraints ? "unchanged" : "changed")}");
        return string.Join("\n", lines);
    }

    private void SeedDefaults()
    {
        Register("summary", "Summarize the following content concisely:\n\n{content}", goal: "Produce a concise summary");
        Register("code-review", "Review the following code for bugs, style issues, and improvements:\n\n```{language}\n{code}\n```");
        Register("reasoning", "Think step by step about the following problem:\n\n{problem}");
        Register("tool-synthesis", "Generate a tool specification for: {requirement}\n\nOutput JSON with name, description, inputs, outputs.");
        Register("agent-eval", "Evaluate the following agent output against the original request.\nRequest: {request}\nOutput: {output}\n\nScore from 1-10 on: accuracy, completeness, clarity.");
    }

    private static string SanitizeId(string name) =>
        string.Join("-", name.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private void Save()
    {
        var data = new Dictionary<string, List<Dictionary<string, object>>>();
        lock (_lock)
        {
            foreach (var (k, v) in _versions)
                data[k] = v.Select(t => new Dictionary<string, object>
                {
                    ["id"] = t.Id, ["name"] = t.Name, ["content"] = t.Content,
                    ["version"] = t.Version, ["goal"] = t.Goal, ["constraints"] = t.Constraints,
                    ["domain_terms"] = t.DomainTerms, ["deprecated"] = t.Deprecated,
                    ["created_at"] = new DateTimeOffset(t.CreatedAt).ToUnixTimeSeconds()
                }).ToList();
        }
        var path = global::System.IO.Path.Combine(_dataDir, "versions.json");
        global::System.IO.File.WriteAllText(path, JsonSerializer.Serialize(data));
    }

    private void Load()
    {
        var path = global::System.IO.Path.Combine(_dataDir, "versions.json");
        if (!global::System.IO.File.Exists(path)) return;
        try
        {
            var json = global::System.IO.File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<Dictionary<string, List<JsonElement>>>(json);
            if (data == null) return;
            lock (_lock)
            {
                foreach (var (k, list) in data)
                {
                    _versions[k] = list.Select(item => new PromptTemplate
                    {
                        Id = item.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "",
                        Name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        Content = item.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "",
                        Version = item.TryGetProperty("version", out var v) ? v.GetInt32() : 1,
                        Goal = item.TryGetProperty("goal", out var g) ? g.GetString() ?? "" : "",
                        Constraints = item.TryGetProperty("constraints", out var ct) ? ct.GetString() ?? "" : "",
                        Deprecated = item.TryGetProperty("deprecated", out var dp) && dp.GetBoolean(),
                        CreatedAt = item.TryGetProperty("created_at", out var ca) ? DateTimeOffset.FromUnixTimeSeconds(ca.GetInt64()).UtcDateTime : DateTime.UtcNow
                    }).ToList();
                }
            }
        }
        catch { }
    }

    private void SaveUsage()
    {
        var path = global::System.IO.Path.Combine(_dataDir, "usage.jsonl");
        foreach (var entry in _usageLog.TakeLast(20))
            global::System.IO.File.AppendAllText(path, JsonSerializer.Serialize(entry) + "\n");
    }
}
