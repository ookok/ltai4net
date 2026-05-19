using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace LTAI.Capability.Skills;

public record DiscoveredSkill
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string Description { get; init; } = "";
    public string Source { get; init; } = "";
    public Dictionary<string, string> YamlFrontmatter { get; init; } = new();
    public string Body { get; init; } = "";
}

public sealed class SkillDiscoveryManager
{
    private readonly string _workspace;
    private readonly string _globalDir;
    private readonly Dictionary<string, DiscoveredSkill> _discovered = new();
    private readonly ILogger<SkillDiscoveryManager> _logger;

    private static readonly string[] SkillDirs = { ".agents/skills", "skills", ".opencode/skills", ".claude/skills" };
    private const int MaxFileSize = 256 * 1024;
    private const int MaxFrontmatterLines = 100;

    public SkillDiscoveryManager(string? workspace = null, ILogger<SkillDiscoveryManager>? logger = null)
    {
        _workspace = workspace ?? Directory.GetCurrentDirectory();
        _globalDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".livingtree", "skills");
        _logger = logger ?? NullLogger<SkillDiscoveryManager>.Instance;
    }

    public Dictionary<string, DiscoveredSkill> DiscoverAll()
    {
        _discovered.Clear();
        var searchDirs = new List<string>();

        foreach (var dir in SkillDirs)
            searchDirs.Add(Path.Combine(_workspace, dir));
        searchDirs.Add(_globalDir);

        foreach (var dir in searchDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var mdPath in Directory.GetFiles(dir, "SKILL.md", SearchOption.AllDirectories))
                ParseSkill(mdPath, "discovered");
        }

        _logger.LogInformation("Discovered {Count} skills", _discovered.Count);
        return _discovered;
    }

    public List<DiscoveredSkill> DiscoverForContext()
    {
        DiscoverAll();
        return _discovered.Values.ToList();
    }

    public DiscoveredSkill? GetSkill(string name)
    {
        _discovered.TryGetValue(name, out var skill);
        return skill;
    }

    public string? GetSkillBody(string name) => GetSkill(name)?.Body;

    public List<DiscoveredSkill> ListForContext()
    {
        DiscoverAll();
        return _discovered.Values.OrderBy(k => k.Name).ToList();
    }

    private void ParseSkill(string filePath, string source)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length > MaxFileSize)
            {
                _logger.LogWarning("Skill file too large: {Path}", filePath);
                return;
            }

            var content = File.ReadAllText(filePath);
            var name = Path.GetFileName(Path.GetDirectoryName(filePath)) ?? Path.GetFileNameWithoutExtension(filePath);
            var (frontmatter, body, description) = ParseYamlFrontmatter(content);

            if (_discovered.ContainsKey(name)) return;

            _discovered[name] = new DiscoveredSkill
            {
                Name = name,
                Path = filePath,
                Description = description,
                Source = source,
                YamlFrontmatter = frontmatter,
                Body = body
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse skill: {Path}", filePath);
        }
    }

    private static (Dictionary<string, string> frontmatter, string body, string description) ParseYamlFrontmatter(string content)
    {
        var frontmatter = new Dictionary<string, string>();
        var description = "";
        var lines = content.Split('\n');
        var bodyStart = 0;

        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            var count = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Trim() == "---") { bodyStart = i + 1; break; }
                if (count++ >= MaxFrontmatterLines) break;
                var match = Regex.Match(lines[i], @"^(?<key>[a-zA-Z_][a-zA-Z0-9_]*)\s*:\s*(?<value>.+)");
                if (match.Success)
                {
                    var key = match.Groups["key"].Value.ToLowerInvariant();
                    var value = match.Groups["value"].Value.Trim();
                    if (key == "description") description = value;
                    frontmatter[key] = value;
                }
            }
        }

        var body = bodyStart < lines.Length ? string.Join('\n', lines.Skip(bodyStart)).Trim() : content.Trim();
        if (string.IsNullOrEmpty(description))
        {
            var firstLine = body.Split('\n', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            description = (firstLine?.Length > 100 ? firstLine[..100] : firstLine) ?? "";
        }

        return (frontmatter, body, description);
    }
}

internal class NullLogger<T> : ILogger<T>
{
    public static NullLogger<T> Instance = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => false;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
}
