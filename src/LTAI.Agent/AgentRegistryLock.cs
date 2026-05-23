using System.Text.Json;
using LTAI.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent;

public sealed class AgentRegistryLock
{
    private readonly ILogger<AgentRegistryLock> _logger;
    private readonly string _lockPath;

    public AgentRegistryLock(ILogger<AgentRegistryLock> logger, string? lockPath = null)
    {
        _logger = logger;
        _lockPath = lockPath ?? Path.Combine(AppContext.BaseDirectory, "config", "agents", "registry.lock");
    }

    public RegistryLockInfo? ReadCurrent()
    {
        if (!File.Exists(_lockPath))
            return null;

        try
        {
            var json = File.ReadAllText(_lockPath);
            return JsonSerializer.Deserialize<RegistryLockInfo>(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AgentRegistryLock: Failed to read lock file");
            return null;
        }
    }

    public void Write(RegistryLockInfo info)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_lockPath)!);
        var json = JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_lockPath, json);
        _logger.LogInformation("AgentRegistryLock: Written v{Version} with {Count} agents", info.Version, info.AgentHashes.Count);
    }

    public (bool Compatible, List<string> Warnings) Validate(AgentConfig config)
    {
        var warnings = new List<string>();
        var current = ReadCurrent();

        if (current == null)
        {
            warnings.Add("No registry.lock found — first run or lock file missing");
            return (true, warnings);
        }

        if (current.Version != GetCurrentVersion())
        {
            warnings.Add($"Registry version mismatch: lock={current.Version}, config={GetCurrentVersion()}. Run migration check.");
        }

        foreach (var (name, hash) in current.AgentHashes)
        {
            var agent = config.Agents.FirstOrDefault(a =>
                a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (agent == null)
            {
                warnings.Add($"Agent '{name}' exists in lock but not in config — may have been removed");
                continue;
            }

            var computedHash = ComputeAgentHash(agent);
            if (computedHash != hash)
            {
                warnings.Add($"Agent '{name}' configuration has changed since last lock.");
            }
        }

        var missingInLock = config.Agents
            .Where(a => !current.AgentHashes.ContainsKey(a.Name))
            .Select(a => a.Name)
            .ToList();

        if (missingInLock.Count > 0)
        {
            foreach (var name in missingInLock)
                warnings.Add($"New agent '{name}' not in registry lock — needs registration.");
        }

        var isCompatible = warnings.Count <= 2; // Minor warnings are OK
        return (isCompatible, warnings);
    }

    public void GenerateLock(AgentConfig config)
    {
        var info = new RegistryLockInfo
        {
            Version = GetCurrentVersion(),
            CreatedAt = DateTime.UtcNow,
            AgentHashes = config.Agents.ToDictionary(
                a => a.Name,
                a => ComputeAgentHash(a))
        };

        Write(info);
    }

    private static string GetCurrentVersion() => "v6.1";

    private static string ComputeAgentHash(LTAI.Models.LTAIAgentCard agent)
    {
        var key = $"{agent.Name}|{agent.Type}|{agent.Model}|{agent.Instructions?.Length ?? 0}|{string.Join(",", agent.Tools.Order())}";
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(key));
        return Convert.ToHexStringLower(bytes)[..16];
    }
}

public sealed class RegistryLockInfo
{
    public string Version { get; init; } = "";
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public Dictionary<string, string> AgentHashes { get; init; } = new();
}
