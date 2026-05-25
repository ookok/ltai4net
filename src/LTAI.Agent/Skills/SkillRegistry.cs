using System.Collections.Concurrent;
using LTAI.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Skills;

/// <summary>
/// Central skill registry. Replaces hardcoded tool categories and agent definitions.
/// Skills are the only distributed artifact — loaded from .md files at startup.
/// </summary>
public sealed class SkillRegistry
{
    private readonly SkillLoader _loader;
    private readonly ILogger<SkillRegistry> _logger;
    private readonly ConcurrentDictionary<string, Skill> _skills = new();
    private readonly ConcurrentDictionary<string, List<Skill>> _byDomain = new();
    private readonly ConcurrentDictionary<SkillLayer, List<Skill>> _byLayer = new();
    private readonly string _skillsRoot;

    public IReadOnlyDictionary<string, Skill> All => _skills;

    public SkillRegistry(SkillLoader loader, ILogger<SkillRegistry> logger, string? skillsRoot = null)
    {
        _loader = loader;
        _logger = logger;
        _skillsRoot = skillsRoot ?? Path.Combine(AppContext.BaseDirectory, "skills");
    }

    public async Task LoadAllAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_skillsRoot))
        {
            _logger.LogWarning("Skills root not found: {Path}", _skillsRoot);
            return;
        }

        var mdFiles = Directory.GetFiles(_skillsRoot, "*.md", SearchOption.AllDirectories);
        _logger.LogInformation("Loading {Count} skills from {Root}", mdFiles.Length, _skillsRoot);

        foreach (var file in mdFiles)
        {
            if (file.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase)) continue;

            var skill = await _loader.LoadAsync(file, ct).ConfigureAwait(false);
            if (skill == null) continue;

            _skills[skill.Name] = skill;
            _byDomain.GetOrAdd(skill.Domain, _ => new List<Skill>()).Add(skill);
            _byLayer.GetOrAdd(skill.Layer, _ => new List<Skill>()).Add(skill);

            _logger.LogDebug("Loaded skill: {Name} ({Layer}) from {Domain}", skill.Name, skill.Layer, skill.Domain);
        }
    }

    public Skill? Get(string name)
    {
        _skills.TryGetValue(name, out var skill);
        return skill;
    }

    public List<Skill> GetByDomain(string domain)
    {
        _byDomain.TryGetValue(domain, out var skills);
        return skills ?? new List<Skill>();
    }

    public List<Skill> GetByLayer(SkillLayer layer)
    {
        _byLayer.TryGetValue(layer, out var skills);
        return skills ?? new List<Skill>();
    }

    public List<Skill> MatchByTrigger(string query)
    {
        var results = new List<(Skill Skill, float Score)>();

        foreach (var skill in _skills.Values.Where(s => s.IsActive))
        {
            float bestScore = 0;
            foreach (var trigger in skill.Triggers)
            {
                try
                {
                    if (System.Text.RegularExpressions.Regex.IsMatch(query, trigger.Pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    {
                        var score = trigger.Weight * (float)skill.Confidence;
                        if (score > bestScore) bestScore = score;
                    }
                }
                catch
                {
                    if (query.Contains(trigger.Pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        var score = trigger.Weight * (float)skill.Confidence;
                        if (score > bestScore) bestScore = score;
                    }
                }
            }

            if (bestScore > 0)
                results.Add((skill, bestScore));
        }

        return results
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.Skill.Evolution.SuccessRate)
            .Select(r => r.Skill)
            .ToList();
    }

    public List<Skill> ResolveRequires(Skill skill)
    {
        var resolved = new List<Skill>();
        var visited = new HashSet<string>();

        void Resolve(Skill s)
        {
            if (!visited.Add(s.Name)) return;

            foreach (var reqName in s.Requires)
            {
                var req = Get(reqName);
                if (req != null)
                {
                    Resolve(req);
                    resolved.Add(req);
                }
            }
        }

        Resolve(skill);
        return resolved;
    }

    public SkillLayer? SuggestLayer(double successRate, int totalUses)
    {
        if (totalUses < 3) return SkillLayer.L0;
        if (totalUses < 10) return SkillLayer.L1;
        if (successRate >= 0.7 && totalUses >= 10) return SkillLayer.L2;
        if (successRate >= 0.85 && totalUses >= 50) return SkillLayer.L3;
        return null;
    }

    public Dictionary<string, object> GetStats() => new()
    {
        ["total_skills"] = _skills.Count,
        ["domains"] = _byDomain.Count,
        ["by_layer"] = new Dictionary<string, int>
        {
            ["L0"] = GetByLayer(SkillLayer.L0).Count,
            ["L1"] = GetByLayer(SkillLayer.L1).Count,
            ["L2"] = GetByLayer(SkillLayer.L2).Count,
            ["L3"] = GetByLayer(SkillLayer.L3).Count,
            ["L4"] = GetByLayer(SkillLayer.L4).Count,
        },
        ["active"] = _skills.Values.Count(s => s.IsActive),
        ["reliable"] = _skills.Values.Count(s => s.IsReliable)
    };
}
