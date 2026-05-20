using System.Text.Json;

namespace LTAI.Core.System;

public sealed record SeedConfig
{
    public string Role { get; init; } = "";
    public string RoleName { get; init; } = "";
    public List<string> AutoRegisterSites { get; init; } = new();
    public List<string> AutoSubscribeStages { get; init; } = new();
    public List<string> KnowledgeDomains { get; init; } = new();
    public List<string> SuggestedCommands { get; init; } = new();
}

public sealed class SeedDevice
{
    private static readonly Lazy<SeedDevice> _instance = new(() => new SeedDevice());
    public static SeedDevice Instance => _instance.Value;

    private static readonly Dictionary<string, SeedConfig> Profiles = new()
    {
        ["eia_engineer"] = new()
        {
            Role = "eia_engineer",
            RoleName = "EIA Engineer",
            AutoRegisterSites = new() { "https://www.haian.gov.cn/hasgxq/gggs/gggs.html" },
            AutoSubscribeStages = new() { "受理公示", "拟批准公示", "审批批复", "验收公示" },
            KnowledgeDomains = new() { "environment", "regulation" },
            SuggestedCommands = new()
            {
                "/check view today's EIA announcements",
                "/ask atmospheric dispersion model parameters",
                "/docs generate EIA report atmospheric chapter"
            }
        },
        ["env_engineer"] = new()
        {
            Role = "env_engineer",
            RoleName = "Environmental Engineer",
            KnowledgeDomains = new() { "environment", "engineering" },
            SuggestedCommands = new()
            {
                "/check view project approval progress",
                "/ask emission treatment solutions",
                "/check regulatory deadline reminders"
            }
        },
        ["dev_engineer"] = new()
        {
            Role = "dev_engineer",
            RoleName = "Software Engineer",
            KnowledgeDomains = new() { "software", "architecture", "testing" },
            SuggestedCommands = new()
            {
                "/code generate implementation",
                "/review code quality check",
                "/docs generate technical documentation"
            }
        },
        ["consultant"] = new()
        {
            Role = "consultant",
            RoleName = "Consultant",
            KnowledgeDomains = new() { "regulation", "data_science" },
            SuggestedCommands = new()
            {
                "/ask latest policy regulations",
                "/check compliance opportunities",
                "/docs generate consulting report"
            }
        }
    };

    private readonly string _dataDir;
    private string? _activeRole;

    private SeedDevice()
    {
        _dataDir = Path.Combine(".livingtree", "seed");
        Directory.CreateDirectory(_dataDir);
    }

    public async Task<Dictionary<string, object>> PlantAsync(string role)
    {
        if (!Profiles.TryGetValue(role, out var profile))
            return new Dictionary<string, object> { ["error"] = $"Unknown role: {role}" };

        _activeRole = role;
        var result = new Dictionary<string, object>
        {
            ["role"] = role,
            ["role_name"] = profile.RoleName,
            ["steps"] = new List<string>()
        };
        var steps = (List<string>)result["steps"];

        steps.Add($"knowledge domains: {string.Join(", ", profile.KnowledgeDomains)}");
        steps.Add($"subscription stages: {string.Join(", ", profile.AutoSubscribeStages)}");

        if (profile.AutoRegisterSites.Count > 0)
            steps.Add($"registered {profile.AutoRegisterSites.Count} sites");

        SaveRole(profile);
        steps.Add("role config saved");

        await Task.CompletedTask;
        return result;
    }

    public string GetGuide(string role)
    {
        if (!Profiles.TryGetValue(role, out var profile))
            return $"Unknown role: {role}";

        var lines = new List<string>
        {
            $"# {profile.RoleName} — Quick Start",
            "Role configured. Recommended commands:",
            ""
        };
        lines.AddRange(profile.SuggestedCommands.Select(c => $"  {c}"));
        lines.Add("");
        lines.Add($"Monitor stages: {string.Join(", ", profile.AutoSubscribeStages)}");
        lines.Add($"Knowledge domains: {string.Join(", ", profile.KnowledgeDomains)}");
        return string.Join("\n", lines);
    }

    public string? GetActiveRole() => _activeRole;

    public List<string> ListRoles() => Profiles.Keys.ToList();

    private void SaveRole(SeedConfig profile)
    {
        var configPath = Path.Combine(_dataDir, "seed_config.json");
        var config = new Dictionary<string, string>
        {
            ["role"] = profile.Role,
            ["role_name"] = profile.RoleName,
            ["planted_at"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm")
        };
        File.WriteAllText(configPath, JsonSerializer.Serialize(config));
    }
}
