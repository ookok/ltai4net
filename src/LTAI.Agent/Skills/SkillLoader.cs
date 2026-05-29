using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LTAI.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Skills;

/// <summary>
/// Parses .md files into Skill objects. The format is YAML-like frontmatter
/// embedded in Markdown — no YAML dependency, just simple line-by-line parsing.
/// </summary>
public sealed class SkillLoader
{
    private readonly ILogger<SkillLoader> _logger;

    private static readonly Regex HeaderLine = new(@"^#+\s*skill:\s*(.+)$", RegexOptions.Compiled);
    private static readonly Regex KeyValue = new(@"^(\w[\w_]*):\s*(.+)$", RegexOptions.Compiled);
    private static readonly Regex TriggerPattern = new(@"^\s*-\s*pattern:\s*""(.+?)""", RegexOptions.Compiled);
    private static readonly Regex TriggerWeight = new(@"weight:\s*([\d.]+)", RegexOptions.Compiled);
    private static readonly Regex ListItem = new(@"^\s*-\s*(.+)$", RegexOptions.Compiled);
    private static readonly Regex StepLine = new(@"^(\d+)\.\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex SkillRef = new(@"→\s*(\w[\w_]*)", RegexOptions.Compiled);

    public SkillLoader(ILogger<SkillLoader> logger)
    {
        _logger = logger;
    }

    public async Task<Skill?> LoadAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            var text = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);

            // Integrity check: if a .sha256 sidecar exists, verify content matches
            if (!SkillInstaller.VerifyFileIntegrity(filePath))
            {
                _logger.LogWarning(
                    "Content hash mismatch for {Path} — file may have been tampered with. Refusing to load.",
                    filePath);
                return null;
            }

            var skill = new Skill { SourceFile = filePath };
            var lines = text.Split('\n');
            var section = "header";

            var triggers = new List<SkillTrigger>();
            var steps = new List<SkillStep>();
            var verifyRules = new List<SkillVerifyRule>();
            var requires = new List<string>();
            var tags = new List<string>();
            var descriptionLines = new List<string>();
            var requiresSection = false;
            var stepsSection = false;
            var verifySection = false;
            var triggersSection = false;
            var tagsSection = false;

            foreach (var raw in lines)
            {
                var line = raw.TrimEnd('\r');

                if (string.IsNullOrWhiteSpace(line)) continue;

                if (line.StartsWith("## "))
                {
                    var sectionName = line[3..].Trim().ToLowerInvariant();
                    section = sectionName;
                    if (sectionName == "requires" || sectionName == "依赖") requiresSection = true;
                    else if (sectionName.Contains("步骤")) { stepsSection = true; requiresSection = false; triggersSection = false; }
                    else if (sectionName.Contains("验证")) { verifySection = true; stepsSection = false; triggersSection = false; }
                    else if (sectionName.Contains("trigger") || sectionName.Contains("触发")) { triggersSection = true; requiresSection = false; stepsSection = false; verifySection = false; tagsSection = false; }
                    else if (sectionName.Contains("tags") || sectionName.Contains("标签")) { tagsSection = true; triggersSection = false; requiresSection = false; stepsSection = false; verifySection = false; }
                    else { requiresSection = false; stepsSection = false; verifySection = false; triggersSection = false; }
                    continue;
                }

                if (section == "header")
                {
                    var headerMatch = HeaderLine.Match(line);
                    if (headerMatch.Success)
                    {
                        if (skill.Name.Length == 0)
                            skill = skill with { Name = headerMatch.Groups[1].Value.Trim() };
                        else
                            descriptionLines.Add(line);
                        continue;
                    }

                    var kv = KeyValue.Match(line);
                    if (kv.Success)
                    {
                        var key = kv.Groups[1].Value.Trim().ToLowerInvariant();
                        var value = kv.Groups[2].Value.Trim();

                        switch (key)
                        {
                            case "domain": skill = skill with { Domain = value }; break;
                            case "layer":
                                skill = skill with { Layer = value.ToLowerInvariant() switch
                                {
                                    // L0 has been fully removed — layer:0 maps directly to L1
                                    "0" or "l0" or "1" or "l1" => SkillLayer.L1,
                                    "2" or "l2" => SkillLayer.L2,
                                    "3" or "l3" => SkillLayer.L3,
                                    "4" or "l4" => SkillLayer.L4,
                                    _ => SkillLayer.L1
                                }};
                                break;
                            case "version": skill = skill with { Version = value }; break;
                            case "intent": skill = skill with { Intent = value }; break;
                            case "confidence":
                                if (double.TryParse(value, out var c)) skill = skill with { Confidence = c };
                                break;
                            case "author": skill = skill with { Author = value }; break;
                            case "license": skill = skill with { License = value }; break;
                            case "source_url": skill = skill with { SourceUrl = value }; break;
                            case "marketplace_id": skill = skill with { MarketplaceId = value }; break;
                            case "min_runtime_version": skill = skill with { MinRuntimeVersion = value }; break;
                            case "content_hash": skill = skill with { ContentHash = value }; break;
                        }
                        continue;
                    }
                }

                if (requiresSection)
                {
                    var item = ListItem.Match(line);
                    if (item.Success)
                    {
                        var val = item.Groups[1].Value.Trim().Trim('"');
                        requires.Add(val);
                    }
                    continue;
                }

                if (tagsSection)
                {
                    if (line.StartsWith("- "))
                        tags.Add(line[2..].Trim());
                    continue;
                }

                if (triggersSection)
                {
                    var pm = TriggerPattern.Match(line);
                    if (pm.Success)
                    {
                        var trigger = new SkillTrigger { Pattern = pm.Groups[1].Value };

                        if (line.Contains("weight:"))
                        {
                            var wm = TriggerWeight.Match(line);
                            if (wm.Success && float.TryParse(wm.Groups[1].Value, out var w))
                                trigger = trigger with { Weight = w };
                        }

                        triggers.Add(trigger);
                    }
                    continue;
                }

                if (stepsSection)
                {
                    var step = StepLine.Match(line);
                    if (step.Success)
                    {
                        var action = step.Groups[2].Value.Trim();
                        string? refSkill = null;
                        string? toolName = null;

                        var refMatch = SkillRef.Match(action);
                        if (refMatch.Success)
                        {
                            refSkill = refMatch.Groups[1].Value;
                            action = action.Replace(refMatch.Value, "").Trim();
                        }

                        if (action.StartsWith("shell:")) toolName = "shell";
                        else if (action.StartsWith("regex:")) toolName = "regex";
                        else if (action.StartsWith("code:")) toolName = "code";
                        else if (action.StartsWith("http:")) toolName = "http";

                        steps.Add(new SkillStep
                        {
                            Index = steps.Count + 1,
                            Action = action,
                            SkillRef = refSkill,
                            ToolName = toolName
                        });
                    }
                    continue;
                }

                if (verifySection)
                {
                    var kv = KeyValue.Match(line);
                    if (kv.Success)
                    {
                        var key = kv.Groups[1].Value.Trim();
                        var value = kv.Groups[2].Value.Trim().Trim('"');
                        if (key.StartsWith("must_contain")) verifyRules.Add(new SkillVerifyRule { Description = value, MustContain = value });
                        if (key.StartsWith("must_not_contain")) verifyRules.Add(new SkillVerifyRule { Description = value, MustNotContain = value });
                        if (key.StartsWith("pattern")) verifyRules.Add(new SkillVerifyRule { Description = value, Pattern = value });
                    }
                    continue;
                }
            }

            if (string.IsNullOrEmpty(skill.Name))
            {
                var fileName = Path.GetFileNameWithoutExtension(filePath);
                skill = skill with { Name = fileName };
            }

            var evolution = LoadEvolution(filePath);

            return skill with
            {
                Triggers = triggers,
                Requires = requires,
                Steps = steps,
                Verification = verifyRules,
                Evolution = evolution,
                Description = descriptionLines.Count > 0 ? string.Join("\n", descriptionLines) : skill.Intent,
                Tags = tags
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load skill from {Path}", filePath);
            return null;
        }
    }

    private static SkillEvolution LoadEvolution(string filePath)
    {
        var metaPath = filePath + ".meta.json";
        if (!File.Exists(metaPath)) return new SkillEvolution();

        try
        {
            var json = File.ReadAllText(metaPath);
            var evo = System.Text.Json.JsonSerializer.Deserialize<SkillEvolution>(json);
            return evo ?? new SkillEvolution();
        }
        catch
        {
            return new SkillEvolution();
        }
    }

    public static void SaveEvolution(string filePath, SkillEvolution evolution)
    {
        var metaPath = filePath + ".meta.json";
        var json = System.Text.Json.JsonSerializer.Serialize(evolution, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(metaPath, json);
    }

    public static void SaveVersioned(string filePath, Skill skill, string reason)
    {
        var dir = Path.GetDirectoryName(filePath)!;
        var versionsDir = Path.Combine(dir, "versions");
        Directory.CreateDirectory(versionsDir);

        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var baseName = Path.GetFileNameWithoutExtension(filePath);
        var versionedMd = Path.Combine(versionsDir, $"{baseName}_v{skill.Version}_{timestamp}.md");

        File.Copy(filePath, versionedMd, overwrite: true);

        var metaPath = filePath + ".meta.json";
        if (File.Exists(metaPath))
        {
            var versionedMeta = Path.Combine(versionsDir, $"{baseName}_v{skill.Version}_{timestamp}.md.meta.json");
            File.Copy(metaPath, versionedMeta, overwrite: true);
        }

        var entry = new SkillVersionEntry
        {
            Version = skill.Version,
            SavedAt = DateTime.UtcNow,
            FilePath = versionedMd,
            Reason = reason
        };

        var history = skill.VersionHistory.ToList();
        history.Add(entry);
        skill = skill with { VersionHistory = history };
    }

    public static List<SkillVersionEntry> ListVersions(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath)!;
        var versionsDir = Path.Combine(dir, "versions");
        if (!Directory.Exists(versionsDir)) return new();

        var baseName = Path.GetFileNameWithoutExtension(filePath);
        var versionFiles = Directory.GetFiles(versionsDir, $"{baseName}_v*.md")
            .Where(f => !f.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f);

        return versionFiles.Select(f =>
        {
            var name = Path.GetFileNameWithoutExtension(f);
            var parts = name.Split('_');
            return new SkillVersionEntry
            {
                Version = parts.Length >= 3 ? parts[^2].TrimStart('v') : "?",
                SavedAt = parts.Length >= 4 && DateTime.TryParseExact(
                    parts[^1], "yyyyMMddHHmmss", null,
                    System.Globalization.DateTimeStyles.None, out var dt) ? dt : new FileInfo(f).CreationTimeUtc,
                FilePath = f,
                Reason = "archived"
            };
        }).ToList();
    }
}
