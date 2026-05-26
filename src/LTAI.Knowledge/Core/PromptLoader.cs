using System.Text.RegularExpressions;
using LTAI.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.Knowledge.Core;

public sealed class PromptLoader
{
    private readonly ILogger<PromptLoader> _logger;

    private static readonly Regex HeaderLine = new(@"^#+\s*prompt:\s*(.+)$", RegexOptions.Compiled);
    private static readonly Regex TemplateHeaderLine = new(@"^#+\s*template:\s*(.+)$", RegexOptions.Compiled);
    private static readonly Regex KeyValue = new(@"^(\w[\w_]*):\s*(.+)$", RegexOptions.Compiled);
    private static readonly Regex ListItem = new(@"^\s*-\s*(.+)$", RegexOptions.Compiled);
    private static readonly Regex TriggerPattern = new(@"^\s*-\s*pattern:\s*""(.+)""\s*$", RegexOptions.Compiled);
    private static readonly Regex TriggerWeight = new(@"weight:\s*([\d.]+)", RegexOptions.Compiled);
    private static readonly Regex VarSpec = new(@"^\s*-\s*(\w+):\s*(\w+)(?:\s*\(default:\s*(.+)\))?$", RegexOptions.Compiled);
    private static readonly Regex VarRequired = new(@"\(required\)", RegexOptions.Compiled);
    private static readonly Regex SectionRef = new(@"^\s*-\s*@(\w[\w-]*)(?:\s*\(order:\s*(\d+)\))?\s*$", RegexOptions.Compiled);

    public PromptLoader(ILogger<PromptLoader> logger)
    {
        _logger = logger;
    }

    public async Task<PromptFile?> LoadPromptAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            var text = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
            return ParsePrompt(filePath, text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load prompt file from {Path}", filePath);
            return null;
        }
    }

    public async Task<PromptTemplate?> LoadTemplateAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            var text = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
            return ParseTemplate(filePath, text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load prompt template from {Path}", filePath);
            return null;
        }
    }

    public PromptFile? ParsePrompt(string filePath, string text)
    {
        var pf = new PromptFile { SourceFile = filePath };
        var lines = text.Split('\n');
        string section = "header";
        var tmpBuilder = new System.Text.StringBuilder();
        int i = 0;

        while (i < lines.Length)
        {
            var line = lines[i].TrimEnd();

            if (i == 0)
            {
                var hm = HeaderLine.Match(line);
                if (hm.Success) pf.Name = hm.Groups[1].Value.Trim();
                i++;
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal) ||
                line.StartsWith("##\t", StringComparison.Ordinal))
            {
                FlushSection();
                section = line[2..].Trim().ToLowerInvariant();
                i++;
                continue;
            }

            if (section == "header")
            {
                var kvm = KeyValue.Match(line);
                if (kvm.Success)
                {
                    var key = kvm.Groups[1].Value.ToLowerInvariant();
                    var val = kvm.Groups[2].Value.Trim();
                    switch (key)
                    {
                        case "domain": pf.Domain = val; break;
                        case "description": pf.Description = val; break;
                    }
                }
            }
            else if (section == "template")
            {
                tmpBuilder.AppendLine(line);
            }
            else if (section == "variables")
            {
                var lim = ListItem.Match(line);
                if (lim.Success)
                {
                    var spec = lim.Groups[1].Value.Trim();
                    var vsm = VarSpec.Match(spec);
                    if (vsm.Success)
                    {
                        pf.Variables.Add(new PromptVariable
                        {
                            Name = vsm.Groups[1].Value.Trim(),
                            Default = vsm.Groups[3].Success ? vsm.Groups[3].Value.Trim() : null,
                            Required = VarRequired.IsMatch(spec),
                            Description = ""
                        });
                    }
                    else
                    {
                        var parts = spec.Split(':', 2);
                        if (parts.Length == 2)
                        {
                            pf.Variables.Add(new PromptVariable
                            {
                                Name = parts[0].Trim(),
                                Description = parts[1].Trim(),
                                Required = VarRequired.IsMatch(spec)
                            });
                        }
                    }
                }
            }
            else if (section == "triggers")
            {
                var tpm = TriggerPattern.Match(line);
                if (tpm.Success)
                {
                    var trigger = new PromptTrigger
                    {
                        Pattern = tpm.Groups[1].Value.Trim()
                    };
                    var wm = TriggerWeight.Match(line);
                    if (wm.Success)
                        trigger.Weight = float.Parse(wm.Groups[1].Value);
                    pf.Triggers.Add(trigger);
                }
            }
            else if (section == "tags")
            {
                var lim = ListItem.Match(line);
                if (lim.Success)
                    pf.Tags.Add(lim.Groups[1].Value.Trim());
            }

            i++;
        }

        FlushSection();
        return pf;

        void FlushSection()
        {
            if (section == "template")
            {
                pf.Template = tmpBuilder.ToString().Trim();
                tmpBuilder.Clear();
            }
        }
    }

    public PromptTemplate? ParseTemplate(string filePath, string text)
    {
        var pt = new PromptTemplate { SourceFile = filePath };
        var lines = text.Split('\n');
        string section = "header";
        int i = 0;

        while (i < lines.Length)
        {
            var line = lines[i].TrimEnd();

            if (i == 0)
            {
                var hm = TemplateHeaderLine.Match(line);
                if (hm.Success) pt.Name = hm.Groups[1].Value.Trim();
                i++;
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal) ||
                line.StartsWith("##\t", StringComparison.Ordinal))
            {
                section = line[2..].Trim().ToLowerInvariant();
                i++;
                continue;
            }

            switch (section)
            {
                case "header":
                    var kvm = KeyValue.Match(line);
                    if (kvm.Success)
                    {
                        var key = kvm.Groups[1].Value.ToLowerInvariant();
                        var val = kvm.Groups[2].Value.Trim();
                        switch (key)
                        {
                            case "domain": pt.Domain = val; break;
                            case "description": pt.Description = val; break;
                            case "max_chars" when int.TryParse(val, out var mc): pt.MaxTotalChars = mc; break;
                        }
                    }
                    break;

                case "sections":
                    var srm = SectionRef.Match(line);
                    if (srm.Success)
                    {
                        int order = 0;
                        if (srm.Groups[2].Success)
                            int.TryParse(srm.Groups[2].Value, out order);

                        pt.Sections.Add(new PromptSection
                        {
                            PromptId = srm.Groups[1].Value.Trim(),
                            Order = order,
                            Optional = line.Contains("optional", StringComparison.OrdinalIgnoreCase)
                        });
                    }
                    else
                    {
                        var lim = ListItem.Match(line);
                        if (lim.Success)
                        {
                            var name = lim.Groups[1].Value.Trim();
                            pt.Sections.Add(new PromptSection
                            {
                                Name = name,
                                Order = pt.Sections.Count
                            });
                        }
                    }
                    break;
            }

            i++;
        }

        return pt;
    }

    public string Serialize(PromptFile pf)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# prompt: {pf.Name}");
        sb.AppendLine($"domain: {pf.Domain}");
        if (!string.IsNullOrEmpty(pf.Description))
            sb.AppendLine($"description: {pf.Description}");
        sb.AppendLine();

        sb.AppendLine("## template");
        sb.AppendLine(pf.Template);
        sb.AppendLine();

        if (pf.Variables.Count > 0)
        {
            sb.AppendLine("## variables");
            foreach (var v in pf.Variables)
            {
                var flags = v.Required ? " (required)" : "";
                var def = v.Default != null ? $" (default: {v.Default})" : "";
                sb.AppendLine($"- {v.Name}: {v.Description}{flags}{def}");
            }
            sb.AppendLine();
        }

        if (pf.Triggers.Count > 0)
        {
            sb.AppendLine("## triggers");
            foreach (var t in pf.Triggers)
                sb.AppendLine($"- pattern: \"{t.Pattern}\" (weight: {t.Weight})");
            sb.AppendLine();
        }

        if (pf.Tags.Count > 0)
        {
            sb.AppendLine("## tags");
            foreach (var tag in pf.Tags)
                sb.AppendLine($"- {tag}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public async Task SaveAsync(PromptFile pf, string? directory = null, CancellationToken ct = default)
    {
        var dir = directory ?? OptionService.Get("paths.prompts") ?? Path.Combine(AppContext.BaseDirectory, "prompts");
        Directory.CreateDirectory(dir);

        var fileName = pf.Name.Replace(' ', '_').ToLowerInvariant() + ".md";
        var path = Path.Combine(dir, fileName);
        pf.SourceFile = path;

        var content = Serialize(pf);
        await File.WriteAllTextAsync(path, content, ct).ConfigureAwait(false);
        _logger.LogInformation("Saved prompt file: {Path}", path);
    }
}
