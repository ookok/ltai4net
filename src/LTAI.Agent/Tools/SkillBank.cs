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
            SuccessRate: existing?.SuccessRate ?? 1.0,
            CreatedAt: DateTime.UtcNow,
            Tags: tags ?? []));

        foreach (var tag in (tags ?? []))
            _tagIndex.Add(tag.ToLowerInvariant());
        Save();
    }

    /// <summary>Find skills matching a query.</summary>
    public List<CodeSkill> Search(string query, string? language = null, int topK = 5)
    {
        var lower = query.ToLowerInvariant();
        var scored = _skills.Select(s =>
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
        var idx = _skills.FindIndex(s => s.Id == id);
        if (idx < 0) return;
        var s = _skills[idx];
        var newCount = s.UseCount + 1;
        var newRate = (s.SuccessRate * s.UseCount + (success ? 1 : 0)) / newCount;
        _skills[idx] = s with { UseCount = newCount, SuccessRate = newRate };
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
                _skills = JsonSerializer.Deserialize<List<CodeSkill>>(json) ?? [];
                foreach (var s in _skills)
                    foreach (var t in s.Tags)
                        _tagIndex.Add(t.ToLowerInvariant());
            }
        }
        catch { _skills = []; }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_storePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_storePath, JsonSerializer.Serialize(_skills, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
