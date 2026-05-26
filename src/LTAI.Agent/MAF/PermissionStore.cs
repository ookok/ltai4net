using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using LTAI.Knowledge.Core;

namespace LTAI.Agent.MAF;

public sealed record PermissionRule
{
    public string ToolName { get; init; } = "";
    public string Pattern { get; init; } = "";
    public string Action { get; init; } = "allow";
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public int HitCount { get; set; }
}

public sealed class PermissionStore
{
    private readonly ConcurrentDictionary<string, PermissionRule> _rules = new();
    private readonly string _storePath;
    private readonly object _saveLock = new();

    public PermissionStore(string? workspaceRoot = null)
    {
        workspaceRoot ??= OptionService.Get("LTAI_WORKSPACE")
            ?? Directory.GetCurrentDirectory();
        _storePath = Path.Combine(OptionService.Get("paths.livingtree") ?? Path.Combine(workspaceRoot, ".livingtree"), "permissions.json");
        Load();
    }

    public bool IsAllowed(string toolName, string? args)
    {
        var argsKey = args ?? "";

        foreach (var rule in _rules.Values)
        {
            if (rule.Action == "deny" && rule.ToolName == toolName && MatchesPattern(rule.Pattern, argsKey))
            {
                rule.HitCount++;
                return false;
            }
        }

        foreach (var rule in _rules.Values)
        {
            if (rule.Action == "allow" && rule.ToolName == toolName && MatchesPattern(rule.Pattern, argsKey))
            {
                rule.HitCount++;
                return true;
            }
        }

        return false;
    }

    public void Grant(string toolName, string pattern)
    {
        var rule = new PermissionRule { ToolName = toolName, Pattern = pattern, Action = "allow" };
        _rules[$"{toolName}:{pattern}"] = rule;
        Save();
    }

    public void Deny(string toolName, string pattern)
    {
        var rule = new PermissionRule { ToolName = toolName, Pattern = pattern, Action = "deny" };
        _rules[$"{toolName}:{pattern}"] = rule;
        Save();
    }

    public void Revoke(string toolName, string pattern)
    {
        _rules.TryRemove($"{toolName}:{pattern}", out _);
        Save();
    }

    public PermissionRule[] GetAll() => _rules.Values.OrderByDescending(r => r.CreatedAt).ToArray();

    public static bool MatchesPattern(string pattern, string input)
    {
        if (string.IsNullOrEmpty(pattern)) return true;
        if (pattern == "*" || input.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            return true;
        try
        {
            var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
            return Regex.IsMatch(input, regexPattern, RegexOptions.IgnoreCase);
        }
        catch { return false; }
    }

    private void Load()
    {
        try
        {
            var dir = Path.GetDirectoryName(_storePath);
            if (dir != null) Directory.CreateDirectory(dir);

            if (File.Exists(_storePath))
            {
                var json = File.ReadAllText(_storePath);
                var rules = JsonSerializer.Deserialize<PermissionRule[]>(json);
                if (rules != null)
                {
                    foreach (var r in rules)
                        _rules[$"{r.ToolName}:{r.Pattern}"] = r;
                }
            }
        }
        catch { }
    }

    private void Save()
    {
        lock (_saveLock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_storePath);
                if (dir != null) Directory.CreateDirectory(dir);
                var rules = _rules.Values.ToArray();
                var json = JsonSerializer.Serialize(rules, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_storePath, json);
            }
            catch { }
        }
    }

    public int Count => _rules.Count;
}
