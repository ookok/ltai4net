using System.Text.RegularExpressions;

namespace LTAI.Agent.Tools;

public static partial class BestSkillFormat
{
    public static string Build(string name, string description, string body,
        double? accuracy = null, double? validationScore = null,
        string[]? transfersTo = null, int? editCount = null,
        int? epoch = null, string[]? categories = null)
    {
        var metadata = new Dictionary<string, string>
        {
            ["name"] = name,
            ["description"] = description,
            ["allowedTools"] = "[]",
            ["version"] = "1",
            ["generated_by"] = "SkillEvolutionEngine",
            ["generated_at"] = DateTime.UtcNow.ToString("O")
        };

        if (accuracy.HasValue)
            metadata["accuracy"] = accuracy.Value.ToString("F4");
        if (validationScore.HasValue)
            metadata["validation_score"] = validationScore.Value.ToString("F4");
        if (transfersTo is { Length: > 0 })
            metadata["transfers_to"] = "[" + string.Join(", ", transfersTo.Select(t => $"\"{t}\"")) + "]";
        if (editCount.HasValue)
            metadata["edit_count"] = editCount.Value.ToString();
        if (epoch.HasValue)
            metadata["epoch"] = epoch.Value.ToString();
        if (categories is { Length: > 0 })
            metadata["categories"] = "[" + string.Join(", ", categories.Select(c => $"\"{c}\"")) + "]";

        var frontMatter = "---\n" + string.Join("\n", metadata.Select(kv => $"{kv.Key}: {kv.Value}")) + "\n---\n\n";
        return frontMatter + body.TrimStart();
    }

    public static string UpdateMetadata(string skillContent, string key, string value)
    {
        var dict = ReadMetadata(skillContent) ?? [];
        dict[key] = value;
        return WriteMetadata(skillContent, dict);
    }

    public static Dictionary<string, string>? ReadMetadata(string skillContent)
    {
        var match = FrontMatterRegex().Match(skillContent);
        if (!match.Success) return null;

        var lines = match.Groups[1].Value.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                var key = line[..colon].Trim();
                var value = line[(colon + 1)..].Trim();
                dict[key] = value;
            }
        }

        return dict;
    }

    public static string WriteMetadata(string skillContent, Dictionary<string, string> metadata)
    {
        var match = FrontMatterRegex().Match(skillContent);
        if (!match.Success)
            return skillContent;

        var newFrontMatter = "---\n" + string.Join("\n", metadata.Select(kv => $"{kv.Key}: {kv.Value}")) + "\n---";
        var body = skillContent[match.Length..];
        return newFrontMatter + body;
    }

    public static string? GetMetadata(string skillContent, string key)
    {
        var dict = ReadMetadata(skillContent);
        return dict?.TryGetValue(key, out var value) == true ? value : null;
    }

    public static string StripMetadata(string skillContent)
    {
        return FrontMatterRegex().Replace(skillContent, "").TrimStart();
    }

    [GeneratedRegex(@"^---\n(.*?)\n---", RegexOptions.Singleline)]
    private static partial Regex FrontMatterRegex();
}
