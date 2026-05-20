namespace LTAI.MAF.Skills;

public sealed class AgentSkillFrontmatter
{
    public string Name { get; }
    public string Description { get; }
    public string? License { get; set; }
    public string? Compatibility { get; set; }
    public string? AllowedTools { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();

    public AgentSkillFrontmatter(string name, string description)
    {
        Name = name;
        Description = description;
    }
}

public abstract class AgentSkillResource(string name, string? description = null)
{
    public string Name { get; } = name;
    public string? Description { get; } = description;
    public abstract Task<object?> ReadAsync(IServiceProvider? services = null, CancellationToken ct = default);
}

public abstract class AgentSkillScript(string name, string? description = null)
{
    public string Name { get; } = name;
    public string? Description { get; } = description;
    public abstract Task<object?> RunAsync(AgentSkill skill, Dictionary<string, object?> args, CancellationToken ct = default);
}

public abstract class AgentSkill
{
    public abstract AgentSkillFrontmatter Frontmatter { get; }
    public abstract string Content { get; }
    public virtual IReadOnlyList<AgentSkillResource>? Resources => null;
    public virtual IReadOnlyList<AgentSkillScript>? Scripts => null;
}

public abstract class AgentClassSkill : AgentSkill
{
    public abstract string Instructions { get; }

    public override string Content =>
        $"# {Frontmatter.Name}\n{Frontmatter.Description}\n\n{Instructions}\n\n" +
        $"## Resources\n{string.Join("\n", (Resources ?? Array.Empty<AgentSkillResource>()).Select(r => $"- {r.Name}: {r.Description}"))}\n\n" +
        $"## Scripts\n{string.Join("\n", (Scripts ?? Array.Empty<AgentSkillScript>()).Select(s => $"- {s.Name}: {s.Description}"))}";
}

public sealed class AgentInlineSkill : AgentSkill
{
    public override AgentSkillFrontmatter Frontmatter { get; }
    public override string Content { get; }
    public override IReadOnlyList<AgentSkillResource>? Resources => _resources.Count > 0 ? _resources.AsReadOnly() : null;
    public override IReadOnlyList<AgentSkillScript>? Scripts => _scripts.Count > 0 ? _scripts.AsReadOnly() : null;

    private readonly List<AgentSkillResource> _resources = new();
    private readonly List<AgentSkillScript> _scripts = new();

    public AgentInlineSkill(string name, string description, string instructions)
    {
        Frontmatter = new AgentSkillFrontmatter(name, description);
        Content = instructions;
    }

    public AgentInlineSkill AddResource(object value, string name, string? description = null)
    {
        _resources.Add(new InlineSkillResource(value, name, description));
        return this;
    }

    public AgentInlineSkill AddScript(Delegate handler, string name, string? description = null)
    {
        _scripts.Add(new InlineSkillScript(handler, name, description));
        return this;
    }

    private sealed class InlineSkillResource(object value, string name, string? description) : AgentSkillResource(name, description)
    {
        public override Task<object?> ReadAsync(IServiceProvider? services = null, CancellationToken ct = default)
            => Task.FromResult<object?>(value);
    }

    private sealed class InlineSkillScript(Delegate handler, string name, string? description) : AgentSkillScript(name, description)
    {
        private readonly Delegate _handler = handler;

        public override Task<object?> RunAsync(AgentSkill skill, Dictionary<string, object?> args, CancellationToken ct = default)
        {
            try
            {
                var result = _handler.DynamicInvoke(args.Values.ToArray());
                return Task.FromResult(result);
            }
            catch (Exception ex)
            {
                return Task.FromResult<object?>(new { error = ex.Message });
            }
        }
    }
}

public sealed class AgentFileSkill : AgentSkill
{
    public override AgentSkillFrontmatter Frontmatter { get; }
    public override string Content { get; }
    public override IReadOnlyList<AgentSkillResource>? Resources => _resources.Count > 0 ? _resources.AsReadOnly() : null;
    public override IReadOnlyList<AgentSkillScript>? Scripts => null;

    private readonly List<AgentSkillResource> _resources = new();
    public string DirectoryPath { get; }

    public AgentFileSkill(string skillDir)
    {
        DirectoryPath = skillDir;
        var mdPath = global::System.IO.Path.Combine(skillDir, "SKILL.md");
        if (!global::System.IO.File.Exists(mdPath))
            throw new global::System.IO.FileNotFoundException($"SKILL.md not found in {skillDir}");

        var content = global::System.IO.File.ReadAllText(mdPath);
        var (name, desc, body) = ParseFrontmatter(content);
        Frontmatter = new AgentSkillFrontmatter(name ?? global::System.IO.Path.GetFileName(skillDir), desc ?? "");

        if (body != null)
            Content = body;

        var resourcesDir = global::System.IO.Path.Combine(skillDir, "resources");
        if (global::System.IO.Directory.Exists(resourcesDir))
        {
            foreach (var f in global::System.IO.Directory.GetFiles(resourcesDir))
            {
                var rname = global::System.IO.Path.GetFileNameWithoutExtension(f);
                var rpath = f;
                _resources.Add(new FileSkillResource(rname, rpath));
            }
        }
    }

    private static (string? name, string? desc, string? body) ParseFrontmatter(string content)
    {
        string? name = null, desc = null;
        var lines = content.Split('\n');
        var i = 0;
        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            i = 1;
            while (i < lines.Length && lines[i].Trim() != "---")
            {
                var line = lines[i];
                var colon = line.IndexOf(':');
                if (colon > 0)
                {
                    var key = line[..colon].Trim().ToLower();
                    var val = line[(colon + 1)..].Trim();
                    if (key == "name") name = val;
                    else if (key == "description") desc = val;
                }
                i++;
            }
            i++;
        }
        var body = i < lines.Length ? string.Join("\n", lines[i..]).Trim() : null;
        return (name, desc, body);
    }

    private sealed class FileSkillResource(string name, string fullPath) : AgentSkillResource(name)
    {
        public override async Task<object?> ReadAsync(IServiceProvider? services = null, CancellationToken ct = default)
            => await global::System.IO.File.ReadAllTextAsync(fullPath, ct);
    }
}
