using System.Text.Json;

namespace LTAI.Agent.Tools;

/// <summary>
/// #5 CODESKILL: self-evolving skill bank for coding agents.
/// Extracts reusable procedural skills from code agent trajectories
/// and provides them as context for future tasks.
/// Skills are stored as compact patterns with pre/post conditions.
/// </summary>
public sealed class SkillBank
{
    public sealed record CodeSkill(
        string Id,
        string Name,
        string Pattern,      // code pattern / template
        string Language,     // "csharp", "python", etc.
        string Category,    // "refactoring", "testing", "async", "linq" etc.
        string Precondition,
        string Postcondition,
        int UseCount,
        double SuccessRate,
        DateTime CreatedAt,
        string[] Tags);

    private readonly string _storePath;
    private readonly object _lock = new();
    private List<CodeSkill> _skills = [];
    private readonly HashSet<string> _tagIndex = [];

    public SkillBank(string? storePath = null)
    {
        _storePath = storePath ?? Path.Combine(
            Directory.GetCurrentDirectory(), ".livingtree", "skills.json");
        Load();
    }

    public IReadOnlyList<CodeSkill> All => _skills;

    /// <summary>Register a new skill from a coding trajectory.</summary>
    public void Register(string name, string pattern, string language, string category,
        string precondition, string postcondition, string[]? tags = null)
    {
        var id = $"{language}/{category}/{name}".ToLowerInvariant().Replace(' ', '-');
        // Deduplicate by id
        lock (_lock)
        {
            var existing = _skills.FirstOrDefault(s => s.Id == id);
            if (existing != null)
            {
                _skills.Remove(existing);
            }
        _skills.Add(new CodeSkill(
            Id: id,
            Name: name,
            Pattern: pattern,
            Language: language,
            Category: category,
            Precondition: precondition,
            Postcondition: postcondition,
            UseCount: existing?.UseCount ?? 0,
            SuccessRate: existing?.SuccessRate ?? 0,
            CreatedAt: existing?.CreatedAt ?? DateTime.UtcNow,
            Tags: tags ?? []));
        }
        Save();
    }

    /// <summary>L1 preview: return only name+desc+tags (minimal tokens).</summary>
    public List<(string Name, string Description, string[] Tags)> SearchPreview(
        string query, string? language = null, int topK = 5)
    {
        var full = Search(query, language, topK);
        return full.Select(s => (s.Name, s.Precondition.Length > 80 ? s.Precondition[..77] + "..." : s.Precondition, s.Tags)).ToList();
    }

    /// <summary>L2+L3 full detail: find skills matching a query.</summary>
    public List<CodeSkill> Search(string query, string? language = null, int topK = 5)
    {
        var lower = query.ToLowerInvariant();
        List<CodeSkill> snapshot;
        lock (_lock) { snapshot = _skills.ToList(); }
        var scored = snapshot.Select(s =>
        {
            var score = 0.0;
            if (s.Name.Contains(lower, StringComparison.OrdinalIgnoreCase)) score += 3;
            if (s.Category.Contains(lower, StringComparison.OrdinalIgnoreCase)) score += 2;
            if (s.Tags.Any(t => t.Contains(lower, StringComparison.OrdinalIgnoreCase))) score += 2;
            if (s.Pattern.Contains(lower, StringComparison.OrdinalIgnoreCase)) score += 1;
            if (s.Precondition.Contains(lower, StringComparison.OrdinalIgnoreCase)) score += 1;
            if (language != null && s.Language.Equals(language, StringComparison.OrdinalIgnoreCase)) score += 2;
            score += Math.Min(s.UseCount * 0.1, 1.0); // popularity boost
            return (skill: s, score);
        })
        .Where(x => x.score > 0)
        .OrderByDescending(x => x.score)
        .Take(topK)
        .Select(x => x.skill)
        .ToList();

        return scored;
    }

    /// <summary>Mark a skill as used (increments counter).</summary>
    public void RecordUse(string id, bool success)
    {
        lock (_lock)
        {
            var idx = _skills.FindIndex(s => s.Id == id);
            if (idx < 0) return;
            var s = _skills[idx];
            var newCount = s.UseCount + 1;
            var newRate = (s.SuccessRate * s.UseCount + (success ? 1 : 0)) / newCount;
            _skills[idx] = s with { UseCount = newCount, SuccessRate = newRate };
        }
        Save();
    }

    public int Count => _skills.Count;

    private void Load()
    {
        try
        {
            if (File.Exists(_storePath))
            {
                var json = File.ReadAllText(_storePath);
                var loaded = JsonSerializer.Deserialize<List<CodeSkill>>(json) ?? [];
                lock (_lock)
                {
                    _skills = loaded;
                    _tagIndex.Clear();
                    foreach (var s in _skills)
                        foreach (var t in s.Tags)
                            _tagIndex.Add(t.ToLowerInvariant());
                }
            }
        }
        catch { _skills = []; }
    }

    private void Save()
    {
        try
        {
            List<CodeSkill> snapshot;
            lock (_lock) { snapshot = _skills.ToList(); }
            var dir = Path.GetDirectoryName(_storePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_storePath, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
