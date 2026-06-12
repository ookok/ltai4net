using System.Text.RegularExpressions;

namespace LTAI.Agent.Experts.Adapters;

/// <summary>
/// Wraps the skills directory as an <see cref="IExpertModule"/>.
/// Scans SKILL.md files and builds capability descriptions from
/// YAML front matter (name + description). Returns matching skill
/// metadata for the Router to select domain-specific workflows.
/// </summary>
public sealed partial class SkillExpert : IExpertModule
{
    private readonly string _skillsDir;
    private IReadOnlyList<SkillMeta>? _cachedSkills;

    public string ExpertId => "skill/expert";
    public ExpertDomain Domain => ExpertDomain.Skill;
    public string CapabilityDescription =>
        "技能专家：匹配可复用的领域技能模板。" +
        "覆盖代码审查、代码重构、测试生成、文档生成等模式。适用场景：需要特定领域工作流/最佳实践。";
    public IReadOnlyList<string> KnowledgeTags => new[] { "skill", "workflow", "pattern", "template" };
    public float MinConfidence => 0.25f; // Skills: name/description text matching, medium similarity

    public SkillExpert(string skillsDir)
    {
        _skillsDir = skillsDir;
    }

    public Task<ExpertResponse> QueryAsync(ExpertQuery query, CancellationToken ct = default)
    {
        var skills = GetSkills();
        if (skills.Count == 0)
        {
            return Task.FromResult(new ExpertResponse(ExpertId, string.Empty, 0f,
                [], new ProvenanceInfo("skills/", DateTime.UtcNow),
                NoAnswer: true, ClarifyQuestion: "未找到可用的技能。"));
        }

        var matched = skills
            .Where(s => s.Name.Contains(query.Query, StringComparison.OrdinalIgnoreCase)
                     || s.Description.Contains(query.Query, StringComparison.OrdinalIgnoreCase)
                     || query.TopicTags?.Any(t => s.Name.Contains(t, StringComparison.OrdinalIgnoreCase)) == true)
            .Take(query.MaxResults)
            .ToList();

        if (matched.Count == 0)
            matched = skills.Take(query.MaxResults).ToList();

        var lines = matched.Select(s => $"- **{s.Name}**: {s.Description}");
        var content = "## Available Skills\n\n" + string.Join('\n', lines);

        var citations = matched.Select((s, i) =>
            new Citation($"skill-{i}", s.Name, s.Path, CitationType.Skill)).ToList();

        return Task.FromResult(new ExpertResponse(ExpertId, content, 0.60f, citations,
            new ProvenanceInfo("skills/", DateTime.UtcNow)));
    }

    private IReadOnlyList<SkillMeta> GetSkills()
    {
        if (_cachedSkills != null) return _cachedSkills;
        var skills = new List<SkillMeta>();
        if (!Directory.Exists(_skillsDir)) { _cachedSkills = skills; return skills; }

        foreach (var skDir in Directory.GetDirectories(_skillsDir))
        {
            var mdFile = Path.Combine(skDir, "SKILL.md");
            if (!File.Exists(mdFile)) continue;
            try
            {
                var text = File.ReadAllText(mdFile);
                var match = SkillFrontMatterRegex().Match(text);
                if (match.Success)
                {
                    skills.Add(new SkillMeta(
                        match.Groups["name"].Value.Trim(),
                        match.Groups["desc"].Value.Trim(),
                        mdFile));
                }
            }
            catch { }
        }

        _cachedSkills = skills;
        return skills;
    }

    public sealed record SkillMeta(string Name, string Description, string Path);

    [GeneratedRegex(@"name:\s*(.+?)\s*\n\s*description:\s*(.+?)\s*\n", RegexOptions.Multiline)]
    private static partial Regex SkillFrontMatterRegex();
}
