using System.Collections.Concurrent;
using LTAI.Knowledge.Core;
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
    private readonly MarketplaceClient? _marketplace;
    private readonly ILogger<SkillRegistry> _logger;
    private readonly ConcurrentDictionary<string, Skill> _skills = new();
    private readonly ConcurrentDictionary<string, List<Skill>> _byDomain = new();
    private readonly ConcurrentDictionary<SkillLayer, List<Skill>> _byLayer = new();
    private readonly string _skillsRoot;

    public IReadOnlyDictionary<string, Skill> All => _skills;

    public event Action<Skill>? SkillInstalled;
#pragma warning disable CS0067 // Used by external consumers via reflection/DI inspection
    public event Action<Skill>? SkillUpdated;
#pragma warning restore CS0067
    public event Action<string>? SkillRemoved;

    public SkillRegistry(SkillLoader loader, ILogger<SkillRegistry> logger, string? skillsRoot = null,
        MarketplaceClient? marketplace = null)
    {
        _loader = loader;
        _logger = logger;
        _skillsRoot = skillsRoot ?? OptionService.Get("paths.skills") ?? Path.Combine(AppContext.BaseDirectory, "skills");
        _marketplace = marketplace;
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

            try
            {
                var skill = await _loader.LoadAsync(file, ct).ConfigureAwait(false);
                if (skill == null) continue;

                _skills[skill.Name] = skill;
                _byDomain.GetOrAdd(skill.Domain, _ => new List<Skill>()).Add(skill);
                _byLayer.GetOrAdd(skill.Layer, _ => new List<Skill>()).Add(skill);

                _logger.LogDebug("Loaded skill: {Name} ({Layer}) from {Domain}", skill.Name, skill.Layer, skill.Domain);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load skill from {File}, skipping", file);
            }
        }
    }

    public Skill? Get(string name)
    {
        _skills.TryGetValue(name, out var skill);
        return skill;
    }

    public void Register(Skill skill)
    {
        if (!_skills.TryAdd(skill.Name, skill))
            return;

        _byDomain.GetOrAdd(skill.Domain, _ => new List<Skill>()).Add(skill);
        _byLayer.GetOrAdd(skill.Layer, _ => new List<Skill>()).Add(skill);
        _logger.LogInformation("SkillRegistry: registered {Name} ({Layer}) from {Domain}",
            skill.Name, skill.Layer, skill.Domain);
    }

    public Skill? Promote(string skillName, SkillLayer newLayer)
    {
        if (!_skills.TryGetValue(skillName, out var old))
            return null;

        if (old.SourceFile != null)
            SkillLoader.SaveVersioned(old.SourceFile, old, $"promote to {newLayer}");

        var promoted = old.PromoteTo(newLayer);
        _skills[skillName] = promoted;

        if (_byLayer.TryGetValue(old.Layer, out var oldList))
            oldList.RemoveAll(s => s.Name == skillName);
        _byLayer.GetOrAdd(newLayer, _ => new List<Skill>()).Add(promoted);

        _logger.LogInformation("SkillRegistry: promoted {Name} from {From} to {To}",
            skillName, old.Layer, newLayer);
        return promoted;
    }

    public async Task<Skill?> RollbackSkillAsync(string skillName, int versionIndex = 0)
    {
        if (!_skills.TryGetValue(skillName, out var current))
            return null;

        if (current.SourceFile == null)
            return null;

        var versions = SkillLoader.ListVersions(current.SourceFile);
        if (versionIndex < 0 || versionIndex >= versions.Count)
            return null;

        var target = versions[versionIndex];
        if (!File.Exists(target.FilePath))
            return null;

        SkillLoader.SaveVersioned(current.SourceFile, current, $"rollback to v{target.Version}");

        var restored = await _loader.LoadAsync(target.FilePath).ConfigureAwait(false);
        if (restored == null)
            return null;

        File.Copy(target.FilePath, current.SourceFile, overwrite: true);

        var prevMeta = current.SourceFile + ".meta.json";
        var targetMeta = target.FilePath + ".meta.json";
        if (File.Exists(targetMeta))
            File.Copy(targetMeta, prevMeta, overwrite: true);

        _skills[skillName] = restored;
        if (_byLayer.TryGetValue(current.Layer, out var oldList))
            oldList.RemoveAll(s => s.Name == skillName);
        _byLayer.GetOrAdd(restored.Layer, _ => new List<Skill>()).Add(restored);
        if (_byDomain.TryGetValue(current.Domain, out var domainList))
            domainList.RemoveAll(s => s.Name == skillName);
        _byDomain.GetOrAdd(restored.Domain, _ => new List<Skill>()).Add(restored);

        _logger.LogInformation("SkillRegistry: rolled back {Name} to version {Version}",
            skillName, target.Version);
        return restored;
    }

    public void RecordFailure(string skillName)
    {
        if (_skills.TryGetValue(skillName, out var skill))
        {
            skill.Evolution.RecordFailure();
            if (skill.SourceFile != null)
                SkillLoader.SaveEvolution(skill.SourceFile, skill.Evolution);
        }
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
        var l1Uses = int.TryParse(OptionService.Get("suggestion_l1_uses"), out var v1) ? v1 : 3;
        var l2Uses = int.TryParse(OptionService.Get("suggestion_l2_uses"), out var v2) ? v2 : 10;
        var l2Rate = float.TryParse(OptionService.Get("suggestion_l2_rate"), out var r2) ? r2 : 0.7f;
        var l3Uses = int.TryParse(OptionService.Get("suggestion_l3_uses"), out var v3) ? v3 : 50;
        var l3Rate = float.TryParse(OptionService.Get("suggestion_l3_rate"), out var r3) ? r3 : 0.85f;

        // L0 has been removed — new skills start at L1
        if (totalUses < l1Uses) return SkillLayer.L1;
        if (totalUses < l2Uses) return SkillLayer.L1;
        if (successRate >= l3Rate && totalUses >= l3Uses) return SkillLayer.L3;
        if (successRate >= l2Rate && totalUses >= l2Uses) return SkillLayer.L2;
        return null;
    }

    public Dictionary<string, object> GetStats() => new()
    {
        ["total_skills"] = _skills.Count,
        ["domains"] = _byDomain.Count,
        ["by_layer"] = new Dictionary<string, int>
        {
            // L0 fully removed
            ["L1"] = GetByLayer(SkillLayer.L1).Count,
            ["L2"] = GetByLayer(SkillLayer.L2).Count,
            ["L3"] = GetByLayer(SkillLayer.L3).Count,
            ["L4"] = GetByLayer(SkillLayer.L4).Count,
        },
        ["active"] = _skills.Values.Count(s => s.IsActive),
        ["reliable"] = _skills.Values.Count(s => s.IsReliable)
    };

    public async Task<List<MarketplaceSearchResult>> SearchMarketplaceAsync(
        string query, string? domain = null, SkillLayer? layer = null, CancellationToken ct = default)
    {
        return _marketplace != null
            ? await _marketplace.SearchAsync(query, domain, layer, ct: ct).ConfigureAwait(false)
            : new List<MarketplaceSearchResult>();
    }

    public async Task<Skill?> InstallFromMarketplaceAsync(string marketplaceId, CancellationToken ct = default)
    {
        if (_marketplace == null) return null;

        var content = await _marketplace.DownloadAsync(marketplaceId, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(content)) return null;

        var tempFile = Path.GetTempFileName() + ".md";
        await File.WriteAllTextAsync(tempFile, content, ct).ConfigureAwait(false);
        var skill = await _loader.LoadAsync(tempFile, ct).ConfigureAwait(false);
        try { File.Delete(tempFile); } catch { }

        if (skill == null) return null;

        skill = skill with { MarketplaceId = marketplaceId };

        var dir = Path.Combine(_skillsRoot, skill.LayerDir);
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, $"{skill.Name}.md");
        await File.WriteAllTextAsync(filePath, content, ct).ConfigureAwait(false);

        skill = skill with { SourceFile = filePath };
        Register(skill);
        SkillInstalled?.Invoke(skill);
        return skill;
    }

    public async Task<bool> CheckForUpdatesAsync(string skillName, CancellationToken ct = default)
    {
        var skill = Get(skillName);
        if (skill?.MarketplaceId == null || _marketplace == null) return false;
        var newVersion = await _marketplace.CheckForUpdateAsync(skill.MarketplaceId,
            skill.Version, ct).ConfigureAwait(false);
        return newVersion != null;
    }

    public async Task<bool> RateSkillAsync(string skillName, int rating, string? review = null,
        CancellationToken ct = default)
    {
        var skill = Get(skillName);
        if (skill?.MarketplaceId == null || _marketplace == null) return false;
        return await _marketplace.RateAsync(skill.MarketplaceId, rating, review, ct).ConfigureAwait(false);
    }

    public bool Uninstall(string skillName)
    {
        if (!_skills.TryRemove(skillName, out var skill)) return false;
        if (_byDomain.TryGetValue(skill.Domain, out var domainList))
            domainList.RemoveAll(s => s.Name == skillName);
        if (_byLayer.TryGetValue(skill.Layer, out var layerList))
            layerList.RemoveAll(s => s.Name == skillName);
        SkillRemoved?.Invoke(skillName);
        return true;
    }

    public List<Skill> GetByTag(string tag) =>
        All.Values.Where(s => s.Tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase))).ToList();

    public List<Skill> Search(string query) =>
        All.Values.Where(s =>
            s.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            (s.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
            s.Tags.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
            s.Triggers.Any(t => t.Pattern.Contains(query, StringComparison.OrdinalIgnoreCase))
        ).ToList();
}
