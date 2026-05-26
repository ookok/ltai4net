using System.Text.RegularExpressions;
using LTAI.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.Knowledge.Core;

public sealed class MemoryFileLoader
{
    private readonly ILogger<MemoryFileLoader> _logger;

    private static readonly Regex HeaderLine = new(@"^#+\s*memory:\s*(.+)$", RegexOptions.Compiled);
    private static readonly Regex KeyValue = new(@"^(\w[\w_]*):\s*(.+)$", RegexOptions.Compiled);
    private static readonly Regex ListItem = new(@"^\s*-\s*(.+)$", RegexOptions.Compiled);
    private static readonly Regex TagItem = new(@"^\s*-\s*\[[\w-]+\]\s*$", RegexOptions.Compiled);
    private static readonly Regex TriggerPattern = new(@"^\s*-\s*pattern:\s*""(.+)""\s*$", RegexOptions.Compiled);
    private static readonly Regex TriggerWeight = new(@"weight:\s*([\d.]+)", RegexOptions.Compiled);
    private static readonly Regex FactItem = new(@"^\s*-\s*(.*?)(?:\s*\((\d+\.?\d*)\))?$", RegexOptions.Compiled);

    public MemoryFileLoader(ILogger<MemoryFileLoader> logger)
    {
        _logger = logger;
    }

    public async Task<MemoryFile?> LoadAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            var text = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
            return Parse(filePath, text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load memory file from {Path}", filePath);
            return null;
        }
    }

    public MemoryFile? Parse(string filePath, string text)
    {
        var memoryFile = new MemoryFile { SourceFile = filePath };
        var lines = text.Split('\n');
        var section = "header";

        var facts = new List<MemoryFileFact>();
        var tags = new List<string>();
        var triggers = new List<MemoryFileTrigger>();
        var contextLines = new List<string>();
        var summaryLines = new List<string>();
        var sourceIds = new List<string>();

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (line.StartsWith("## "))
            {
                section = line[3..].Trim().ToLowerInvariant();
                continue;
            }

            if (section == "header")
            {
                var headerMatch = HeaderLine.Match(line);
                if (headerMatch.Success)
                {
                    if (memoryFile.Name.Length == 0)
                        memoryFile = memoryFile with { Name = headerMatch.Groups[1].Value.Trim() };
                    continue;
                }

                var kv = KeyValue.Match(line);
                if (kv.Success)
                {
                    var key = kv.Groups[1].Value.Trim().ToLowerInvariant();
                    var value = kv.Groups[2].Value.Trim();

                    switch (key)
                    {
                        case "id": memoryFile = memoryFile with { Id = value }; break;
                        case "domain": memoryFile = memoryFile with { Domain = value }; break;
                        case "topic": memoryFile = memoryFile with { Topic = value }; break;
                        case "confidence":
                            if (double.TryParse(value, out var c)) memoryFile = memoryFile with { Confidence = c };
                            break;
                        case "last_verified":
                            if (DateTime.TryParse(value, out var lv))
                                memoryFile = memoryFile with { Verification = memoryFile.Verification with { LastVerified = lv } };
                            break;
                        case "verified_by":
                            memoryFile = memoryFile with { Verification = memoryFile.Verification with { VerifiedBy = value } };
                            break;
                        case "source_entity_ids":
                            sourceIds.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                            break;
                    }
                    continue;
                }
            }

            if (section == "summary")
            {
                summaryLines.Add(line);
                continue;
            }

            if (section == "facts")
            {
                var factMatch = FactItem.Match(line);
                if (factMatch.Success)
                {
                    var statement = factMatch.Groups[1].Value.Trim();
                    double confidence = 1.0;
                    if (factMatch.Groups[2].Success && double.TryParse(factMatch.Groups[2].Value, out var fc))
                        confidence = fc;

                    facts.Add(new MemoryFileFact { Statement = statement, Confidence = confidence });
                }
                continue;
            }

            if (section == "context")
            {
                contextLines.Add(line);
                continue;
            }

            if (section == "tags")
            {
                var tagMatch = ListItem.Match(line);
                if (tagMatch.Success)
                {
                    var tag = tagMatch.Groups[1].Value.Trim().TrimStart('[').TrimEnd(']');
                    tags.Add(tag);
                }
                continue;
            }

            if (section == "triggers")
            {
                var pm = TriggerPattern.Match(line);
                if (pm.Success)
                {
                    var trigger = new MemoryFileTrigger { Pattern = pm.Groups[1].Value };
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

            if (section == "verification")
            {
                var kv = KeyValue.Match(line);
                if (kv.Success)
                {
                    var key = kv.Groups[1].Value.Trim();
                    var value = kv.Groups[2].Value.Trim().Trim('"');
                    if (key == "last_verified" && DateTime.TryParse(value, out var lv2))
                        memoryFile = memoryFile with { Verification = memoryFile.Verification with { LastVerified = lv2 } };
                }
                continue;
            }
        }

        if (string.IsNullOrEmpty(memoryFile.Name))
        {
            var fileName = Path.GetFileNameWithoutExtension(filePath);
            memoryFile = memoryFile with { Name = fileName };
        }

        return memoryFile with
        {
            Summary = string.Join("\n", summaryLines),
            Facts = facts,
            Context = string.Join("\n", contextLines),
            Tags = tags,
            Triggers = triggers,
            SourceEntityIds = sourceIds.Count > 0 ? sourceIds : memoryFile.SourceEntityIds
        };
    }

    public string Serialize(MemoryFile memoryFile)
    {
        var lines = new List<string>();
        lines.Add($"# memory: {memoryFile.Name}");
        lines.Add($"id: {memoryFile.Id}");
        lines.Add($"domain: {memoryFile.Domain}");
        if (!string.IsNullOrEmpty(memoryFile.Topic))
            lines.Add($"topic: {memoryFile.Topic}");
        lines.Add($"confidence: {memoryFile.Confidence:F2}");

        if (memoryFile.Verification.LastVerified.HasValue)
        {
            lines.Add($"last_verified: {memoryFile.Verification.LastVerified:O}");
            lines.Add($"verified_by: {memoryFile.Verification.VerifiedBy}");
        }

        if (memoryFile.SourceEntityIds.Count > 0)
            lines.Add($"source_entity_ids: {string.Join(", ", memoryFile.SourceEntityIds)}");

        if (!string.IsNullOrEmpty(memoryFile.Summary))
        {
            lines.Add("");
            lines.Add("## summary");
            lines.Add(memoryFile.Summary);
        }

        if (memoryFile.Facts.Count > 0)
        {
            lines.Add("");
            lines.Add("## facts");
            foreach (var fact in memoryFile.Facts)
                lines.Add($"- {fact.Statement} ({fact.Confidence:F2})");
        }

        if (!string.IsNullOrEmpty(memoryFile.Context))
        {
            lines.Add("");
            lines.Add("## context");
            lines.Add(memoryFile.Context);
        }

        if (memoryFile.Tags.Count > 0)
        {
            lines.Add("");
            lines.Add("## tags");
            foreach (var tag in memoryFile.Tags)
                lines.Add($"- [{tag}]");
        }

        if (memoryFile.Triggers.Count > 0)
        {
            lines.Add("");
            lines.Add("## triggers");
            foreach (var trigger in memoryFile.Triggers)
                lines.Add($"- pattern: \"{trigger.Pattern}\" weight: {trigger.Weight:F1}");
        }

        return string.Join("\n", lines);
    }

    public async Task SaveAsync(MemoryFile memoryFile, string? filePath = null, CancellationToken ct = default)
    {
        var path = filePath ?? memoryFile.SourceFile
            ?? Path.Combine("memory", $"{memoryFile.Id}.md");
        var dir = Path.GetDirectoryName(path);
        if (dir != null) Directory.CreateDirectory(dir);

        var content = Serialize(memoryFile);
        await File.WriteAllTextAsync(path, content, ct).ConfigureAwait(false);
        memoryFile = memoryFile with { SourceFile = path };
    }
}
