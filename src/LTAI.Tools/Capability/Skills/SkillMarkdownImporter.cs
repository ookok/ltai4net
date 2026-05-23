using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace LTAI.Tools.Skills;

public record SkillImportResult
{
    public List<(string name, string file)> Installed { get; init; } = new();
    public List<(string name, string error)> Failed { get; init; } = new();
    public int TotalFound { get; init; }
}

/// Parses a user-provided Markdown document and extracts skill definitions.
/// Each skill is defined by a ## heading with optional description paragraph,
/// followed by a code block. Creates individual SKILL.md files for each.
public sealed class SkillMarkdownImporter
{
    private readonly SkillDiscoveryManager _discovery;
    private readonly string _skillsDir;

    public SkillMarkdownImporter(SkillDiscoveryManager discovery)
    {
        _discovery = discovery;
        _skillsDir = global::System.IO.Path.Combine(
            Environment.CurrentDirectory, ".livingtree", "skills");
        global::System.IO.Directory.CreateDirectory(_skillsDir);
    }

    public SkillImportResult ImportFromMarkdown(string mdContent)
    {
        var result = new SkillImportResult();
        if (string.IsNullOrWhiteSpace(mdContent))
            return result;

        // Pattern: ## skill_name (optional description) followed by ```language\ncode\n```
        var sections = Regex.Split(mdContent, @"^##\s+", RegexOptions.Multiline);
        foreach (var section in sections.Skip(1)) // skip content before first ##
        {
            var lines = section.Split('\n');
            if (lines.Length == 0) continue;

            var name = lines[0].Trim().ToLowerInvariant()
                .Replace(" ", "-").Replace("_", "-").Replace("/", "-");

            // Invalid skill names
            if (string.IsNullOrWhiteSpace(name) || name.Length < 2 || name.Length > 64)
                continue;

            // Find description (first non-empty, non-code-block line after name)
            var descIdx = 1;
            var description = "";
            while (descIdx < lines.Length && string.IsNullOrWhiteSpace(lines[descIdx]))
                descIdx++;
            if (descIdx < lines.Length && !lines[descIdx].TrimStart().StartsWith("```"))
                description = lines[descIdx].Trim();
            if (string.IsNullOrEmpty(description))
                description = name;

            // Find code block
            var body = ExtractCodeBlock(lines);
            if (string.IsNullOrEmpty(body))
            {
                // No code block found → use text content as body
                body = string.Join("\n", lines.Skip(descIdx + 1)
                    .TakeWhile(l => !l.TrimStart().StartsWith("## ")));
                if (string.IsNullOrWhiteSpace(body)) continue;
            }

            try
            {
                var skillDir = global::System.IO.Path.Combine(_skillsDir, name);
                global::System.IO.Directory.CreateDirectory(skillDir);
                var skillFile = global::System.IO.Path.Combine(skillDir, "SKILL.md");

                var frontmatter = $@"---
name: {name}
description: {description}
version: 1.0.0
imported: {DateTime.UtcNow:yyyy-MM-dd}
---
";
                global::System.IO.File.WriteAllText(skillFile, frontmatter + "\n" + body);
                result.Installed.Add((name, skillFile));
            }
            catch (Exception ex)
            {
                result.Failed.Add((name, ex.Message));
            }
        }

        result = result with { TotalFound = result.Installed.Count + result.Failed.Count };

        // Refresh discovery
        _discovery.DiscoverAll();

        return result;
    }

    private static string ExtractCodeBlock(string[] lines)
    {
        for (int i = 1; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("```"))
            {
                var sb = new global::System.Text.StringBuilder();
                for (int j = i + 1; j < lines.Length; j++)
                {
                    if (lines[j].TrimStart().StartsWith("```"))
                        return sb.ToString().Trim();
                    sb.AppendLine(lines[j]);
                }
                return sb.ToString().Trim();
            }
        }
        return "";
    }
}
