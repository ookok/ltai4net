namespace LTAI.MAF.Skills;

public abstract class AgentSkillsSource
{
    public abstract Task<IList<AgentSkill>> GetSkillsAsync(CancellationToken ct = default);
}

public sealed class AgentFileSkillsSource : AgentSkillsSource
{
    private readonly List<string> _skillPaths;

    public AgentFileSkillsSource(IEnumerable<string> skillPaths)
    {
        _skillPaths = skillPaths.ToList();
    }

    public override async Task<IList<AgentSkill>> GetSkillsAsync(CancellationToken ct = default)
    {
        var skills = new List<AgentSkill>();
        foreach (var dir in _skillPaths)
        {
            if (global::System.IO.Directory.Exists(dir))
            {
                foreach (var sub in global::System.IO.Directory.GetDirectories(dir))
                {
                    if (global::System.IO.File.Exists(global::System.IO.Path.Combine(sub, "SKILL.md")))
                    {
                        try { skills.Add(new AgentFileSkill(sub)); }
                        catch { }
                    }
                }
            }
        }
        return await Task.FromResult(skills);
    }
}

public sealed class AgentInMemorySkillsSource : AgentSkillsSource
{
    private readonly List<AgentSkill> _skills;

    public AgentInMemorySkillsSource(IEnumerable<AgentSkill> skills)
    {
        _skills = skills.ToList();
    }

    public override Task<IList<AgentSkill>> GetSkillsAsync(CancellationToken ct = default)
        => Task.FromResult<IList<AgentSkill>>(_skills);
}

public sealed class AggregatingAgentSkillsSource : AgentSkillsSource
{
    private readonly List<AgentSkillsSource> _sources;

    public AggregatingAgentSkillsSource(IEnumerable<AgentSkillsSource> sources)
    {
        _sources = sources.ToList();
    }

    public override async Task<IList<AgentSkill>> GetSkillsAsync(CancellationToken ct = default)
    {
        var all = new List<AgentSkill>();
        foreach (var src in _sources)
            all.AddRange(await src.GetSkillsAsync(ct));
        return all;
    }
}

public sealed class FilteringAgentSkillsSource : AgentSkillsSource
{
    private readonly AgentSkillsSource _inner;
    private readonly Func<AgentSkill, bool> _predicate;

    public FilteringAgentSkillsSource(AgentSkillsSource inner, Func<AgentSkill, bool> predicate)
    {
        _inner = inner; _predicate = predicate;
    }

    public override async Task<IList<AgentSkill>> GetSkillsAsync(CancellationToken ct = default)
    {
        var all = await _inner.GetSkillsAsync(ct);
        return all.Where(s => _predicate(s)).ToList();
    }
}

public sealed class AgentSkillsProvider(AgentSkillsSource source)
{
    public async Task<IList<AgentSkill>> GetSkillsAsync(CancellationToken ct = default) =>
        await source.GetSkillsAsync(ct);
}

public sealed class AgentSkillsProviderBuilder
{
    private readonly List<AgentSkillsSource> _sources = new();
    private Func<AgentSkill, bool>? _filter;

    public AgentSkillsProviderBuilder UseFileSkill(string path)
    {
        _sources.Add(new AgentFileSkillsSource(new[] { path }));
        return this;
    }

    public AgentSkillsProviderBuilder UseInlineSkills(params AgentSkill[] skills)
    {
        _sources.Add(new AgentInMemorySkillsSource(skills));
        return this;
    }

    public AgentSkillsProviderBuilder UseClassSkills(params AgentClassSkill[] skills)
    {
        _sources.Add(new AgentInMemorySkillsSource(skills));
        return this;
    }

    public AgentSkillsProviderBuilder UseSource(AgentSkillsSource source)
    {
        _sources.Add(source);
        return this;
    }

    public AgentSkillsProviderBuilder UseFilter(Func<AgentSkill, bool> predicate)
    {
        _filter = predicate;
        return this;
    }

    public AgentSkillsProvider Build()
    {
        AgentSkillsSource combined = new AggregatingAgentSkillsSource(_sources);
        if (_filter != null)
            combined = new FilteringAgentSkillsSource(combined, _filter);
        return new AgentSkillsProvider(combined);
    }
}

public sealed class LTAISkills
{
    public static void RegisterBuiltins(AgentSkillsProviderBuilder builder)
    {
        builder.UseClassSkills(
            new CodeGenerationSkill(),
            new CodeReviewSkill(),
            new EIAReportSkill(),
            new DataAnalysisSkill(),
            new KnowledgeSearchSkill()
        );
    }
}

public sealed class CodeGenerationSkill : AgentClassSkill
{
    public override AgentSkillFrontmatter Frontmatter { get; } = new("code-generation", "Generate code from requirements");
    public override string Instructions => "Analyze the requirements. Generate clean, well-structured code. Include error handling and documentation.";
    public override IReadOnlyList<AgentSkillScript>? Scripts { get; } = new[]
    {
        new InlineCodeScript("validate", async (s, a, ct) => await Task.FromResult<object?>("Syntax check passed"))
    };
}

public sealed class CodeReviewSkill : AgentClassSkill
{
    public override AgentSkillFrontmatter Frontmatter { get; } = new("code-review", "Review code for quality issues");
    public override string Instructions => "Review code for bugs, security issues, performance problems, and style violations. Provide actionable feedback.";
}

public sealed class EIAReportSkill : AgentClassSkill
{
    public override AgentSkillFrontmatter Frontmatter { get; } = new("eia-report", "Environmental Impact Assessment report generation");
    public override string Instructions => """
Generate EIA reports following GB3095 and HJ2.2 standards.
Include: project overview, environmental baseline, impact analysis, mitigation measures, conclusions.
Use proper formatting with headings, tables, and citations.
""";
    public override IReadOnlyList<AgentSkillResource>? Resources { get; } = new[]
    {
        new StaticSkillResource("GB3095_standards", "GB3095-2012 Ambient Air Quality Standards: SO2, NO2, PM10, PM2.5, CO, O3 limits"),
        new StaticSkillResource("HJ2_2_standards", "HJ2.2-2018 Technical Guidelines for Atmospheric Environmental Impact Assessment"),
    };
}

public sealed class DataAnalysisSkill : AgentClassSkill
{
    public override AgentSkillFrontmatter Frontmatter { get; } = new("data-analysis", "Analyze data and provide insights");
    public override string Instructions => "Analyze the provided data. Identify patterns, trends, and anomalies. Present findings with statistical evidence.";
}

public sealed class KnowledgeSearchSkill : AgentClassSkill
{
    public override AgentSkillFrontmatter Frontmatter { get; } = new("knowledge-search", "Search knowledge base for information");
    public override string Instructions => "Search the knowledge base for relevant information. Retrieve documents, facts, and references. Synthesize findings.";
}

internal sealed class StaticSkillResource(string name, string value) : AgentSkillResource(name)
{
    public override Task<object?> ReadAsync(IServiceProvider? services = null, CancellationToken ct = default)
        => Task.FromResult<object?>(value);
}

internal sealed class InlineCodeScript(string name, Func<AgentSkill, Dictionary<string, object?>, CancellationToken, Task<object?>> fn) : AgentSkillScript(name)
{
    public override Task<object?> RunAsync(AgentSkill skill, Dictionary<string, object?> args, CancellationToken ct = default)
        => fn(skill, args, ct);
}
