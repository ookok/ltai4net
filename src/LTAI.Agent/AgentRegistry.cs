using System.Text.Json;
using System.Text.RegularExpressions;

namespace LTAI.Agent;

public sealed record AgentFileDef
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public double Temperature { get; init; } = 0.7;
    public double TopP { get; init; } = 0.95;
    public string? ModelId { get; init; }
    public string InheritTools { get; init; } = "";
    /// <summary>Permission flags: "read", "write", "list", "exec".</summary>
    public string[] Permissions { get; init; } = [];
    /// <summary>Tool category names enabled for this agent.</summary>
    public string[] Tools { get; init; } = [];
    public string Prompt { get; init; } = "";
}

public static class AgentRegistry
{
    public static List<AgentFileDef> LoadAll()
    {
        var result = new List<AgentFileDef>();
        var searchDirs = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "agents"),
            Path.Combine(Directory.GetCurrentDirectory(), "agents"),
        };

        foreach (var dir in searchDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.GetFiles(dir, "*.agent.md"))
            {
                try
                {
                    var def = ParseFile(file);
                    if (def != null && !string.IsNullOrEmpty(def.Name))
                        result.Add(def);
                }
                catch { }
            }
            break;
        }
        return result;
    }

    public static AgentFileDef? ParseFile(string path)
    {
        var text = File.ReadAllText(path);
        return Parse(text);
    }

    public static AgentFileDef? Parse(string text)
    {
        var match = Regex.Match(text, "^---\n(.*?)\n---\n(.*)", RegexOptions.Singleline);
        if (!match.Success) return null;

        var frontmatter = match.Groups[1].Value;
        var body = match.Groups[2].Value.Trim();
        var def = new AgentFileDef { Prompt = body };

        foreach (var line in frontmatter.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;
            var colonPos = trimmed.IndexOf(':');
            if (colonPos < 0) continue;

            var key = trimmed.Substring(0, colonPos).Trim().ToLowerInvariant();
            var val = trimmed.Substring(colonPos + 1).Trim().Trim('"');

            switch (key)
            {
                case "name":          def = def with { Name = val }; break;
                case "description":   def = def with { Description = val }; break;
                case "temperature":   if (double.TryParse(val, out var t)) def = def with { Temperature = t }; break;
                case "topp":          if (double.TryParse(val, out var p)) def = def with { TopP = p }; break;
                case "modelid":       def = def with { ModelId = val }; break;
                case "inherittools":  def = def with { InheritTools = val.ToLowerInvariant() }; break;
                case "permissions":   def = def with { Permissions = ParseJsonArray(val) ?? def.Permissions }; break;
                case "tools":         def = def with { Tools = ParseJsonArray(val) ?? def.Tools }; break;
            }
        }
        return def;
    }

    private static string[]? ParseJsonArray(string json)
    {
        try
        {
            var arr = JsonSerializer.Deserialize<string[]>(json);
            return arr?.Length > 0 ? arr : null;
        }
        catch
        {
            return null;
        }
    }
}
