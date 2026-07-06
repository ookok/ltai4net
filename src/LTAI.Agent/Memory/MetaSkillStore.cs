using System.Text.Json;
using LTAI.Agent.Evolution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Memory;

public sealed class MetaSkillStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _storeDir;
    private readonly ILogger<MetaSkillStore> _logger;
    private MetaSkill _currentSkill;
    private readonly object _lock = new();
    private bool _loaded;

    public MetaSkill Current => _currentSkill;
    public int LatestRound => _currentSkill.Round;

    public MetaSkillStore(ILogger<MetaSkillStore>? logger = null)
    {
        _logger = logger ?? NullLogger<MetaSkillStore>.Instance;
        _storeDir = Path.Combine(Directory.GetCurrentDirectory(), ".livingtree", "meta-skill");
        Directory.CreateDirectory(_storeDir);
        _currentSkill = MetaSkill.CreateInitial();
    }

    public async Task<MetaSkill> GetLatestAsync(CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        return _currentSkill;
    }

    public async Task<MetaSkill> SaveVersionAsync(MetaSkill skill, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);

        var json = JsonSerializer.Serialize(skill, JsonOpts);
        var path = Path.Combine(_storeDir, $"v{skill.Version}.json");
        await File.WriteAllTextAsync(path, json, ct).ConfigureAwait(false);

        lock (_lock)
        {
            _currentSkill = skill;
        }

        _logger.LogInformation("MetaSkillStore: saved v{V} (round {R}, {Modules})",
            skill.Version, skill.Round, skill.ModuleCountLabel);

        return skill;
    }

    public async Task<MetaSkill?> LoadVersionAsync(int version, CancellationToken ct = default)
    {
        var path = Path.Combine(_storeDir, $"v{version}.json");
        if (!File.Exists(path))
        {
            _logger.LogWarning("MetaSkillStore: version v{V} not found", version);
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<MetaSkill>(json, JsonOpts);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MetaSkillStore: failed to load v{V}", version);
            return null;
        }
    }

    public async Task<List<int>> ListVersionsAsync(CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);

        var files = Directory.GetFiles(_storeDir, "v*.json");
        var versions = new List<int>();
        foreach (var f in files)
        {
            var name = Path.GetFileNameWithoutExtension(f);
            if (name.StartsWith("v") && int.TryParse(name[1..], out var v))
                versions.Add(v);
        }
        versions.Sort();
        return versions;
    }

    public async Task<MetaSkill> ApplyPatchAsync(
        MetaSkillPatch[] patches,
        CancellationToken ct = default)
    {
        var current = await GetLatestAsync(ct).ConfigureAwait(false);
        var next = MetaSkill.EvolvedFromPrevious(current, patches);
        return await SaveVersionAsync(next, ct).ConfigureAwait(false);
    }

    public async Task<MetaSkill> RewriteAsync(
        MetaSkillModule? newTD = null,
        MetaSkillModule? newAE = null,
        MetaSkillModule? newWO = null,
        MetaSkillPatch[]? patches = null,
        double? validationScore = null,
        CancellationToken ct = default)
    {
        var current = await GetLatestAsync(ct).ConfigureAwait(false);

        var next = new MetaSkill(
            Version: current.Version + 1,
            Round: current.Round + 1,
            EvolvedFrom: $"v{current.Version}",
            TaskDecomposition: newTD ?? current.TaskDecomposition,
            AgentEngineering: newAE ?? current.AgentEngineering,
            WorkflowOrchestration: newWO ?? current.WorkflowOrchestration,
            CreatedAt: DateTime.UtcNow,
            PatchesApplied: patches,
            ValidationScore: validationScore);

        return await SaveVersionAsync(next, ct).ConfigureAwait(false);
    }

    private async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_loaded) return;

        try
        {
            var versions = await ListVersionsAsync(ct).ConfigureAwait(false);
            if (versions.Count > 0)
            {
                var latest = versions[^1];
                var skill = await LoadVersionAsync(latest, ct).ConfigureAwait(false);
                if (skill != null)
                {
                    lock (_lock)
                        _currentSkill = skill;
                    _logger.LogInformation("MetaSkillStore: loaded v{V} as current", latest);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MetaSkillStore: failed to load existing versions");
        }
        finally
        {
            _loaded = true;
        }
    }
}
