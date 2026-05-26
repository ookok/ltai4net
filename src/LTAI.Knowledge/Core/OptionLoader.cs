using System.Text.RegularExpressions;
using LTAI.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.Knowledge.Core;

public sealed class OptionLoader
{
    private readonly ILogger<OptionLoader> _logger;

    private static readonly Regex HeaderLine = new(@"^#\s*option:\s*(.+)$", RegexOptions.Compiled);
    private static readonly Regex KeyValue = new(@"^(\w[\w_]*):\s*(.+)$", RegexOptions.Compiled);
    private static readonly Regex KeyLine = new(@"^\s*-\s*(\w[\w_]*):\s*(\w[\w_]*)(?:\s*\(([^)]*)\))?(?:\s*—\s*(.*))?$", RegexOptions.Compiled);
    private static readonly Regex ListItem = new(@"^\s*-\s*(.+)$", RegexOptions.Compiled);
    private static readonly Regex EnvLine = new(@"^\s*env:\s*(.+)$", RegexOptions.Compiled);
    private static readonly Regex VarRef = new(@"\{\{(\w+)\}\}", RegexOptions.Compiled);

    public static string ExpandVariables(string template)
    {
        if (string.IsNullOrEmpty(template) || !template.Contains("{{")) return template;
        return VarRef.Replace(template, m =>
        {
            var varName = m.Groups[1].Value;
            return Environment.GetEnvironmentVariable(varName) ?? m.Value;
        });
    }

    public OptionLoader(ILogger<OptionLoader> logger)
    {
        _logger = logger;
    }

    public async Task<OptionFile?> LoadAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            var text = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
            return Parse(filePath, text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load option file {Path}", filePath);
            return null;
        }
    }

    public OptionFile? Parse(string filePath, string text)
    {
        var fallbackName = Path.GetFileNameWithoutExtension(filePath);
        var option = new OptionFile { SourceFile = filePath, Name = fallbackName };
        var keys = new List<OptionKey>();
        var tags = new List<string>();
        var section = "header";
        var keySection = false;
        var tagsSection = false;
        OptionKey? currentKey = null;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;

            var headerMatch = HeaderLine.Match(line);
            if (headerMatch.Success && section == "header")
            {
                option.Name = headerMatch.Groups[1].Value.Trim();
                continue;
            }

            if (line.StartsWith("## "))
            {
                var sectionName = line[3..].Trim().ToLowerInvariant();
                section = sectionName;
                keySection = sectionName == "keys";
                tagsSection = sectionName == "tags";

                if (currentKey != null) { keys.Add(currentKey); currentKey = null; }
                continue;
            }

            if (section == "header" && !line.StartsWith("#"))
            {
                var kv = KeyValue.Match(line);
                if (!kv.Success) continue;
                if (kv.Groups[1].Value.Equals("section", StringComparison.OrdinalIgnoreCase))
                    option.Section = kv.Groups[2].Value.Trim();
                else if (kv.Groups[1].Value.Equals("description", StringComparison.OrdinalIgnoreCase))
                    option.Description = kv.Groups[2].Value.Trim();
                continue;
            }

            if (keySection && line.StartsWith("- "))
            {
                var keyMatch = KeyLine.Match(line);
                if (keyMatch.Success)
                {
                    if (currentKey != null) keys.Add(currentKey);
                    currentKey = new OptionKey
                    {
                        Name = keyMatch.Groups[1].Value,
                        Type = keyMatch.Groups[2].Value,
                        Description = keyMatch.Groups.Count > 4 ? keyMatch.Groups[4].Value?.Trim() : null
                    };
                    var parens = keyMatch.Groups[3].Value;
                    if (!string.IsNullOrEmpty(parens))
                    {
                        if (parens.StartsWith("default:", StringComparison.OrdinalIgnoreCase))
                            currentKey.Default = parens["default:".Length..].Trim();
                        else if (parens.Equals("required", StringComparison.OrdinalIgnoreCase))
                            currentKey.Required = true;
                    }
                    continue;
                }

                var envMatch = EnvLine.Match(line);
                if (envMatch.Success && currentKey != null)
                {
                    currentKey.EnvVar = envMatch.Groups[1].Value.Trim();
                    continue;
                }

                if (currentKey == null)
                {
                    var item = ListItem.Match(line);
                    if (item.Success) currentKey = new OptionKey { Name = item.Groups[1].Value.Trim() };
                }
                continue;
            }

            if (tagsSection)
            {
                var item = ListItem.Match(line);
                if (item.Success) tags.Add(item.Groups[1].Value.Trim());
            }
        }

        if (currentKey != null) keys.Add(currentKey);
        option.Keys = keys;
        option.Tags = tags;

        if (string.IsNullOrEmpty(option.Name))
            option.Name = Path.GetFileNameWithoutExtension(filePath);

        return option;
    }

    public string Serialize(OptionFile option)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# option: {option.Name}");
        if (!string.IsNullOrEmpty(option.Section))
            sb.AppendLine($"section: {option.Section}");
        if (!string.IsNullOrEmpty(option.Description))
            sb.AppendLine($"description: {option.Description}");
        sb.AppendLine();
        sb.AppendLine("## keys");

        foreach (var key in option.Keys)
        {
            var extras = new List<string>();
            if (key.Default != null) extras.Add($"default: {key.Default}");
            if (key.Required) extras.Add("required");
            var extStr = extras.Count > 0 ? $" ({string.Join(", ", extras)})" : "";
            sb.AppendLine($"- {key.Name}: {key.Type}{extStr}{(key.Description != null ? $" — {key.Description}" : "")}");
            if (key.EnvVar != null)
                sb.AppendLine($"  env: {key.EnvVar}");
        }

        sb.AppendLine();
        sb.AppendLine("## tags");
        foreach (var tag in option.Tags)
            sb.AppendLine($"- {tag}");

        return sb.ToString();
    }

    public async Task SaveAsync(OptionFile option, string? directory = null, CancellationToken ct = default)
    {
        var dir = directory ?? Path.GetDirectoryName(option.SourceFile!)!;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{option.Name}.md");
        await File.WriteAllTextAsync(path, Serialize(option), ct).ConfigureAwait(false);
        option.SourceFile = path;
    }
}
