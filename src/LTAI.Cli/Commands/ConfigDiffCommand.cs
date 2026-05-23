using System.Text;
using System.Text.Json;
using LTAI.Models;

namespace LTAI.Cli.Commands;

public enum Severity { Info, Warning, Critical }

public sealed record BreakingChange(
    string Agent, string Field, string OldValue, string NewValue, Severity Level, string Impact);

public sealed record ConfigDiffReport
{
    public List<string> RemovedAgents { get; init; } = new();
    public List<BreakingChange> BreakingChanges { get; init; } = new();
    public bool HasBreakingChanges => RemovedAgents.Count > 0 || BreakingChanges.Count > 0;
    public bool HasCriticalChanges => BreakingChanges.Any(c => c.Level == Severity.Critical);

    public string ToColoredString()
    {
        var sb = new StringBuilder();
        sb.AppendLine("═══════════════════════════════════════════");
        sb.AppendLine("  ⚠ CONFIG BREAKING CHANGES DETECTED");
        sb.AppendLine("═══════════════════════════════════════════");
        foreach (var c in BreakingChanges.OrderByDescending(c => c.Level))
        {
            var prefix = c.Level switch { Severity.Critical => "🔴", Severity.Warning => "🟡", _ => "🔵" };
            sb.AppendLine($"  {prefix} [{c.Level}] {c.Agent}.{c.Field}: {c.OldValue} → {c.NewValue}");
            sb.AppendLine($"       Impact: {c.Impact}");
        }
        if (HasCriticalChanges)
            sb.AppendLine("  ⛔ CRITICAL changes found. DO NOT DEPLOY without explicit approval.");
        return sb.ToString();
    }
}

public sealed class ConfigDiffer
{
    public async Task<ConfigDiffReport> DiffAsync(
        string oldYamlPath, string newYamlPath, CancellationToken ct)
    {
        var oldConfig = await LoadConfigAsync(oldYamlPath, ct);
        var newConfig = await LoadConfigAsync(newYamlPath, ct);

        var report = new ConfigDiffReport();

        foreach (var agent in oldConfig.Agents)
        {
            var newAgent = newConfig.Agents.FirstOrDefault(a =>
                a.Name.Equals(agent.Name, StringComparison.OrdinalIgnoreCase));

            if (newAgent == null)
            {
                report.RemovedAgents.Add($"{agent.Name} ({agent.Type})");
                continue;
            }

            if (newAgent.Type != agent.Type)
                report.BreakingChanges.Add(new BreakingChange(
                    agent.Name, "type", agent.Type.ToString(), newAgent.Type.ToString(),
                    Severity.Critical, "Agent type change may break AgentFactory resolution"));

            ReportListDiff(report, agent.Name, "middleware",
                agent.Middleware, newAgent.Middleware,
                item => item is "unified_safety" or "dna_safety" or "prompt_shield"
                    ? Severity.Critical : Severity.Warning,
                "Removing safety middleware leaves the agent UNPROTECTED");

            ReportListDiff(report, agent.Name, "tools",
                agent.Tools, newAgent.Tools,
                _ => Severity.Info,
                "Tool whitelist changed — verify intentional");
        }

        return report;
    }

    public async Task<ConfigDiffReport> DiffJsonAsync(
        string oldJsonPath, string newJsonPath, CancellationToken ct)
    {
        var oldJson = await File.ReadAllTextAsync(oldJsonPath, ct);
        var newJson = await File.ReadAllTextAsync(newJsonPath, ct);

        var oldConfig = JsonSerializer.Deserialize<LtaiRootConfig>(oldJson)
            ?? new LtaiRootConfig();
        var newConfig = JsonSerializer.Deserialize<LtaiRootConfig>(newJson)
            ?? new LtaiRootConfig();

        var report = new ConfigDiffReport();

        var oldPipeline = oldConfig.Middleware?.Pipeline ?? new List<string>();
        var newPipeline = newConfig.Middleware?.Pipeline ?? new List<string>();

        var removed = oldPipeline.Except(newPipeline).ToHashSet();
        foreach (var mw in removed)
        {
            report.BreakingChanges.Add(new BreakingChange(
                "global", $"middleware[{mw}]", mw, "(removed)",
                mw is "prompt_shield" or "dna_safety" or "output_review"
                    ? Severity.Critical : Severity.Warning,
                mw is "prompt_shield" or "dna_safety" or "output_review"
                    ? $"Safety middleware '{mw}' removed — system may be unprotected"
                    : $"Middleware '{mw}' removed from pipeline"));
        }

        return report;
    }

    private static void ReportListDiff(
        ConfigDiffReport report, string agent, string field,
        List<string> oldList, List<string> newList,
        Func<string, Severity> classifySeverity, string impact)
    {
        var removed = oldList.Except(newList, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var item in removed)
            report.BreakingChanges.Add(new BreakingChange(
                agent, $"{field}[{item}]", item, "(removed)", classifySeverity(item), impact));
    }

    private static async Task<AgentConfig> LoadConfigAsync(string path, CancellationToken ct)
    {
        var yaml = await File.ReadAllTextAsync(path, ct);
        return YamlParser.ParseAgentConfig(yaml);
    }

    private sealed class LtaiRootConfig
    {
        public MiddlewareSection? Middleware { get; set; }
    }

    private sealed class MiddlewareSection
    {
        public List<string> Pipeline { get; set; } = new();
    }
}

internal static class YamlParser
{
    public static AgentConfig ParseAgentConfig(string yaml)
    {
        var config = new AgentConfig();
        var lines = yaml.Split('\n');
        LTAIAgentCard? currentAgent = null;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("#") || string.IsNullOrWhiteSpace(trimmed))
                continue;

            if (trimmed.StartsWith("- name:"))
            {
                if (currentAgent != null)
                    config.Agents.Add(currentAgent);

                currentAgent = new LTAIAgentCard
                {
                    Name = ExtractValue(trimmed, "name:")
                };
            }
            else if (currentAgent != null)
            {
                if (trimmed.StartsWith("type:"))
                    currentAgent.Type = ParseAgentType(ExtractValue(trimmed, "type:"));
                else if (trimmed.StartsWith("model:"))
                    currentAgent.Model = ExtractValue(trimmed, "model:");
                else if (trimmed == "middleware:")
                    currentAgent.Middleware = ParseList(lines, trimmed);
                else if (trimmed == "tools:")
                    currentAgent.Tools = ParseList(lines, trimmed);
            }
        }

        if (currentAgent != null)
            config.Agents.Add(currentAgent);

        return config;
    }

    private static string ExtractValue(string line, string key)
    {
        var idx = line.IndexOf(key, StringComparison.Ordinal);
        return idx >= 0 ? line[(idx + key.Length)..].Trim() : "";
    }

    private static AgentType ParseAgentType(string type) => type switch
    {
        "code_agent" => AgentType.Code,
        "eia_agent" => AgentType.EIA,
        "reasoning_agent" => AgentType.Reasoning,
        _ => AgentType.Chat
    };

    private static List<string> ParseList(string[] lines, string currentLine)
    {
        var items = new List<string>();
        int startIndex = 0;
        for (int j = 0; j < lines.Length; j++)
            if (lines[j].Trim() == currentLine) { startIndex = j; break; }

        for (int i = startIndex + 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("- "))
            {
                items.Add(line[2..].Trim());
            }
            else if (!string.IsNullOrWhiteSpace(line) && !line.StartsWith("#"))
            {
                break; // No longer in the list
            }
        }
        return items;
    }
}
