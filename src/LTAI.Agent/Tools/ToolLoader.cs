using System.Text.RegularExpressions;
using LTAI.Knowledge.Core;
using LTAI.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Tools;

public sealed class ToolLoader
{
    private readonly ILogger<ToolLoader> _logger;

    private static readonly Regex HeaderLine = new(@"^#+\s*tool:\s*(.+)$", RegexOptions.Compiled);
    private static readonly Regex KeyValue = new(@"^(\w[\w_]*):\s*(.+)$", RegexOptions.Compiled);
    private static readonly Regex ListItem = new(@"^\s*-\s*(.+)$", RegexOptions.Compiled);
    private static readonly Regex TriggerPattern = new(@"^\s*-\s*pattern:\s*""(.+)""\s*$", RegexOptions.Compiled);
    private static readonly Regex TriggerWeight = new(@"weight:\s*([\d.]+)", RegexOptions.Compiled);
    private static readonly Regex ParamSpec = new(@"^\s*-\s*(\w+):\s*(\w+)(?:\s*\(required\))?(?:\s*\(default:\s*(.+?)\))?\s*$", RegexOptions.Compiled);
    private static readonly Regex ParamDesc = new(@"—\s*(.+)$", RegexOptions.Compiled);
    private static readonly Regex StepSpec = new(@"^\s*-\s*(\w+(?:/\w+)*)\s*(\(parallel\))?\s*$", RegexOptions.Compiled);
    private static readonly Regex StepCommand = new(@"^\s+command:\s*(.+)$", RegexOptions.Compiled);
    private static readonly Regex StepInput = new(@"^\s+input\s+(\w+):\s*(.+)$", RegexOptions.Compiled);

    public ToolLoader(ILogger<ToolLoader> logger)
    {
        _logger = logger;
    }

    public async Task<MkTool?> LoadAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            var text = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
            return Parse(filePath, text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load tool file from {Path}", filePath);
            return null;
        }
    }

    public MkTool? Parse(string filePath, string text)
    {
        var tool = new MkTool { SourceFile = filePath };
        var lines = text.Split('\n');
        var section = "header";
        var templateBuilder = new System.Text.StringBuilder();
        var currentStep = (ComposeStep?)null;
        int i = 0;

        while (i < lines.Length)
        {
            var line = lines[i].TrimEnd('\r');

            if (i == 0)
            {
                var hm = HeaderLine.Match(line);
                if (hm.Success) tool.Name = hm.Groups[1].Value.Trim();
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

            if (string.IsNullOrWhiteSpace(line))
            {
                i++;
                continue;
            }

            if (section == "header")
            {
                var kvm = KeyValue.Match(line);
                if (kvm.Success)
                {
                    var key = kvm.Groups[1].Value.Trim().ToLowerInvariant();
                    var val = kvm.Groups[2].Value.Trim();
                    switch (key)
                    {
                        case "domain": tool.Domain = val; break;
                        case "type": tool.Type = Enum.TryParse<MkToolType>(val, ignoreCase: true, out var t) ? t : MkToolType.Shell; break;
                        case "description": tool.Description = val; break;
                        case "timeout": if (int.TryParse(val, out var ts)) tool.TimeoutSec = ts; break;
                        case "max_output_lines": if (int.TryParse(val, out var ml)) tool.MaxOutputLines = ml; break;
                    }
                }
            }
            else if (section == "parameters")
            {
                var lim = ListItem.Match(line);
                if (lim.Success)
                {
                    var spec = lim.Groups[1].Value.Trim();
                    var psm = ParamSpec.Match(spec);
                    if (psm.Success)
                    {
                        var desc = "";
                        var dm = ParamDesc.Match(spec);
                        if (dm.Success) desc = dm.Groups[1].Value.Trim();

                        tool.Parameters.Add(new ToolParam
                        {
                            Name = psm.Groups[1].Value.Trim(),
                            Type = psm.Groups[2].Value.Trim(),
                            Default = psm.Groups[3].Success ? psm.Groups[3].Value.Trim() : null,
                            Required = spec.Contains("(required)", StringComparison.OrdinalIgnoreCase),
                            Description = desc
                        });
                    }
                }
            }
            else if (section == "command" && tool.Type == MkToolType.Shell)
            {
                templateBuilder.AppendLine(line);
            }
            else if (section == "http")
            {
                var kvm = KeyValue.Match(line);
                if (kvm.Success)
                {
                    var key = kvm.Groups[1].Value.Trim().ToLowerInvariant();
                    var val = kvm.Groups[2].Value.Trim();
                    switch (key)
                    {
                        case "method": tool.HttpMethod = val.ToUpperInvariant(); break;
                        case "url": tool.Template = val; break;
                        case "body": tool.HttpBody = val; break;
                        case "header": tool.HttpHeaders.Add(val); break;
                    }
                }
            }
            else if (section == "template" && tool.Type == MkToolType.Prompt)
            {
                templateBuilder.AppendLine(line);
            }
            else if (section == "service" && tool.Type == MkToolType.Service)
            {
                var kvm = KeyValue.Match(line);
                if (kvm.Success)
                {
                    var key = kvm.Groups[1].Value.Trim().ToLowerInvariant();
                    var val = kvm.Groups[2].Value.Trim();
                    switch (key)
                    {
                        case "name": tool.ServiceName = val; break;
                        case "method": tool.ServiceMethod = val; break;
                    }
                }
            }
            else if (section == "steps" && tool.Type == MkToolType.Compose)
            {
                var sm = StepSpec.Match(line);
                if (sm.Success)
                {
                    if (currentStep != null) tool.Steps.Add(currentStep);
                    currentStep = new ComposeStep
                    {
                        Name = sm.Groups[1].Value.Trim(),
                        Parallel = sm.Groups[2].Success
                    };
                    i++;
                    continue;
                }

                if (currentStep != null)
                {
                    var scm = StepCommand.Match(line);
                    if (scm.Success)
                    {
                        currentStep = currentStep with { ToolRef = scm.Groups[1].Value.Trim() };
                    }
                    else
                    {
                        var sim = StepInput.Match(line);
                        if (sim.Success)
                            currentStep.Inputs[sim.Groups[1].Value.Trim()] = sim.Groups[2].Value.Trim();
                    }
                }
            }
            else if (section == "triggers")
            {
                var tpm = TriggerPattern.Match(line);
                if (tpm.Success)
                {
                    var trigger = new MkToolTrigger { Pattern = tpm.Groups[1].Value.Trim() };
                    var wm = TriggerWeight.Match(line);
                    if (wm.Success && float.TryParse(wm.Groups[1].Value, out var w))
                        trigger.Weight = w;
                    tool.Triggers.Add(trigger);
                }
            }
            else if (section == "tags")
            {
                var lim = ListItem.Match(line);
                if (lim.Success)
                    tool.Tags.Add(lim.Groups[1].Value.Trim());
            }

            i++;
        }

        if (currentStep != null) tool.Steps.Add(currentStep);
        FlushSection();

        if (string.IsNullOrEmpty(tool.Name))
        {
            tool.Name = Path.GetFileNameWithoutExtension(filePath);
        }

        return tool;

        void FlushSection()
        {
            switch (section)
            {
                case "command":
                case "template":
                    var content = templateBuilder.ToString().Trim();
                    if (content.Length > 0) tool.Template = content;
                    templateBuilder.Clear();
                    break;
            }
        }
    }

    public string Serialize(MkTool tool)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# tool: {tool.Name}");
        sb.AppendLine($"domain: {tool.Domain}");
        sb.AppendLine($"type: {tool.Type.ToString().ToLowerInvariant()}");
        sb.AppendLine($"description: {tool.Description}");
        sb.AppendLine($"timeout: {tool.TimeoutSec}");
        if (tool.MaxOutputLines != 50)
            sb.AppendLine($"max_output_lines: {tool.MaxOutputLines}");
        sb.AppendLine();

        if (tool.Parameters.Count > 0)
        {
            sb.AppendLine("## parameters");
            foreach (var p in tool.Parameters)
            {
                var flags = "";
                if (p.Required) flags += " (required)";
                if (p.Default != null) flags += $" (default: {p.Default})";
                sb.Append($"- {p.Name}: {p.Type}{flags}");
                if (!string.IsNullOrEmpty(p.Description))
                    sb.Append($" — {p.Description}");
                sb.AppendLine();
            }
            sb.AppendLine();
        }

        switch (tool.Type)
        {
            case MkToolType.Shell:
                sb.AppendLine("## command");
                sb.AppendLine(tool.Template);
                sb.AppendLine();
                break;
            case MkToolType.Http:
                sb.AppendLine("## http");
                sb.AppendLine($"method: {tool.HttpMethod}");
                sb.AppendLine($"url: {tool.Template}");
                if (!string.IsNullOrEmpty(tool.HttpBody))
                    sb.AppendLine($"body: {tool.HttpBody}");
                foreach (var h in tool.HttpHeaders)
                    sb.AppendLine($"header: {h}");
                sb.AppendLine();
                break;
            case MkToolType.Prompt:
                sb.AppendLine("## template");
                sb.AppendLine(tool.Template);
                sb.AppendLine();
                break;
            case MkToolType.Service:
                sb.AppendLine("## service");
                sb.AppendLine($"name: {tool.ServiceName}");
                sb.AppendLine($"method: {tool.ServiceMethod}");
                sb.AppendLine();
                break;
            case MkToolType.Compose:
                sb.AppendLine("## steps");
                foreach (var s in tool.Steps)
                {
                    var parallel = s.Parallel ? " (parallel)" : "";
                    sb.AppendLine($"- {s.Name}{parallel}");
                    if (s.ToolRef != null)
                        sb.AppendLine($"  command: {s.ToolRef}");
                    foreach (var kv in s.Inputs)
                        sb.AppendLine($"  input {kv.Key}: {kv.Value}");
                }
                sb.AppendLine();
                break;
        }

        if (tool.Triggers.Count > 0)
        {
            sb.AppendLine("## triggers");
            foreach (var t in tool.Triggers)
                sb.AppendLine($"- pattern: \"{t.Pattern}\" (weight: {t.Weight})");
            sb.AppendLine();
        }

        if (tool.Tags.Count > 0)
        {
            sb.AppendLine("## tags");
            foreach (var tag in tool.Tags)
                sb.AppendLine($"- {tag}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public async Task SaveAsync(MkTool tool, string? directory = null, CancellationToken ct = default)
    {
        var dir = directory ?? OptionService.Get("paths.tools") ?? Path.Combine(AppContext.BaseDirectory, "tools");
        Directory.CreateDirectory(dir);

        var fileName = tool.Name.Replace(' ', '_').ToLowerInvariant() + ".md";
        var path = Path.Combine(dir, fileName);
        tool.SourceFile = path;

        var content = Serialize(tool);
        await File.WriteAllTextAsync(path, content, ct).ConfigureAwait(false);
        _logger.LogInformation("Saved tool file: {Path}", path);
    }
}
