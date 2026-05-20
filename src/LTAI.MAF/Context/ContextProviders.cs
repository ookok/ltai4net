namespace LTAI.MAF.Context;

public enum ContextProviderType { Memory, Knowledge, Skill, File, MCP }

public sealed class ContextItem
{
    public string Content { get; set; } = "";
    public ContextProviderType ProviderType { get; set; }
    public double Relevance { get; set; }
    public string Source { get; set; } = "";
}

public abstract class AIContextProvider(string name, ContextProviderType type)
{
    public string Name { get; } = name;
    public ContextProviderType Type { get; } = type;
    public abstract Task<IReadOnlyList<ContextItem>> GetContextAsync(string query, CancellationToken ct = default);
}

public sealed class MoEContextProvider : AIContextProvider
{
    private readonly Func<string, string, Task<object>> _moeQuery;

    public MoEContextProvider(Func<string, string, Task<object>> moeQuery)
        : base("ContextMoE", ContextProviderType.Memory)
    {
        _moeQuery = moeQuery;
    }

    public override async Task<IReadOnlyList<ContextItem>> GetContextAsync(string query, CancellationToken ct = default)
    {
        try
        {
            await _moeQuery("context_provider", query);
            return new[] { new ContextItem { Content = "ContextMoE memory enrichment active", ProviderType = ContextProviderType.Memory, Relevance = 1.0, Source = "MoE" } };
        }
        catch { return Array.Empty<ContextItem>(); }
    }
}

public sealed class SkillContextProvider : AIContextProvider
{
    private readonly Skills.AgentSkillsProvider _skillsProvider;

    public SkillContextProvider(Skills.AgentSkillsProvider skillsProvider)
        : base("Skills", ContextProviderType.Skill)
    {
        _skillsProvider = skillsProvider;
    }

    public override async Task<IReadOnlyList<ContextItem>> GetContextAsync(string query, CancellationToken ct = default)
    {
        try
        {
            var skills = await _skillsProvider.GetSkillsAsync(ct);
            return skills
                .Where(s => s.Frontmatter.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                         || s.Frontmatter.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Take(5)
                .Select(s => new ContextItem
                {
                    Content = $"{s.Frontmatter.Name}: {s.Frontmatter.Description}\n{s.Content[..Math.Min(500, s.Content.Length)]}",
                    ProviderType = ContextProviderType.Skill,
                    Relevance = 0.8,
                    Source = $"skill:{s.Frontmatter.Name}"
                }).Cast<ContextItem>().ToList();
        }
        catch { return Array.Empty<ContextItem>(); }
    }
}

public sealed class CompositeContextProvider : AIContextProvider
{
    private readonly List<AIContextProvider> _providers;

    public CompositeContextProvider(params AIContextProvider[] providers) : base("Composite", ContextProviderType.Memory)
    {
        _providers = providers.ToList();
    }

    public override async Task<IReadOnlyList<ContextItem>> GetContextAsync(string query, CancellationToken ct = default)
    {
        var tasks = _providers.Select(p => p.GetContextAsync(query, ct));
        var results = await Task.WhenAll(tasks);
        return results.SelectMany(r => r).OrderByDescending(c => c.Relevance).Take(20).ToList();
    }
}
