using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using LTAI.TreeLLM.Models;
using LTAI.TreeLLM.Routing;

namespace LTAI.TreeLLM.Session;

public sealed class FreeModelPool
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ConcurrentDictionary<string, FreeModelProfile> _models = new();
    private readonly ILogger<FreeModelPool>? _logger;
    private readonly string _persistPath;

    private static readonly Dictionary<ResearchRole, (double Coding, double Reasoning, double Reading, double Instruction, double Search)> RoleWeights = new()
    {
        [ResearchRole.DataHunter] = (0.1, 0.4, 0.4, 0.0, 0.1),
        [ResearchRole.Coder] = (0.6, 0.2, 0.0, 0.1, 0.1),
        [ResearchRole.IdeaAgent] = (0.1, 0.5, 0.1, 0.2, 0.1),
        [ResearchRole.Reviewer] = (0.2, 0.3, 0.3, 0.1, 0.1)
    };

    public FreeModelPool(ILogger<FreeModelPool>? logger = null, string? persistPath = null)
    {
        _logger = logger;
        _persistPath = persistPath ?? Path.Combine("livingtree", "meta", "free_model_pool.json");
        RegisterPresets();
        Load();
    }

    public FreeModelProfile Register(string name, string? baseUrl = null, bool isFree = true,
        double coding = 0.5, double reasoning = 0.5, double reading = 0.5,
        double instruction = 0.5, double search = 0.5,
        int contextWindow = 32768, int rpmLimit = 60, int rpdLimit = 1000)
    {
        var profile = new FreeModelProfile
        {
            Name = name,
            BaseUrl = baseUrl ?? "",
            IsFree = isFree,
            CodingScore = coding,
            ReasoningScore = reasoning,
            ReadingScore = reading,
            InstructionScore = instruction,
            SearchScore = search,
            ContextWindow = contextWindow,
            RpmLimit = rpmLimit,
            RpdLimit = rpdLimit,
            Status = PoolModelStatus.Unknown
        };

        _models[name] = profile;
        return profile;
    }

    public void MarkHealthy(string name, double latencyMs)
    {
        if (_models.TryGetValue(name, out var model))
        {
            model.EmaLatencyMs = model.EmaLatencyMs * 0.8 + latencyMs * 0.2;
            model.FailureStreak = 0;
            model.TotalCalls++;
            model.Status = PoolModelStatus.Healthy;
        }
    }

    public void MarkFailure(string name)
    {
        if (_models.TryGetValue(name, out var model))
        {
            model.FailureStreak++;
            model.TotalCalls++;

            model.Status = model.FailureStreak switch
            {
                1 => PoolModelStatus.Degraded,
                2 => PoolModelStatus.Degraded,
                3 => PoolModelStatus.Quarantined,
                _ => PoolModelStatus.Quarantined
            };

            if (model.Status == PoolModelStatus.Quarantined)
            {
                var cooldownSeconds = model.FailureStreak switch
                {
                    3 => 60,
                    4 => 300,
                    _ => 900
                };
                model.QuarantinedUntil = DateTime.UtcNow.AddSeconds(cooldownSeconds);
                _logger?.LogWarning("FreeModelPool: {Name} quarantined for {Seconds}s (streak: {Streak})",
                    name, cooldownSeconds, model.FailureStreak);
            }
        }
    }

    public void MarkRateLimited(string name)
    {
        if (_models.TryGetValue(name, out var model))
            model.Status = PoolModelStatus.RateLimited;
    }

    public bool IsAvailable(string name)
    {
        if (!_models.TryGetValue(name, out var model)) return false;

        if (model.Status == PoolModelStatus.Quarantined)
        {
            if (model.QuarantinedUntil != null && DateTime.UtcNow > model.QuarantinedUntil)
            {
                model.Status = PoolModelStatus.Healthy;
                model.FailureStreak = 0;
                _logger?.LogDebug("FreeModelPool: {Name} auto-recovered from quarantine", name);
                return true;
            }
            return false;
        }

        if (model.Status == PoolModelStatus.RateLimited)
            return CheckRateLimit(name);

        return true;
    }

    private bool CheckRateLimit(string name)
    {
        if (!_models.TryGetValue(name, out var model)) return false;

        var cutoff = DateTime.UtcNow.AddSeconds(-60);
        lock (model.RecentRequests)
        {
            while (model.RecentRequests.Count > 0 && model.RecentRequests.Peek() < cutoff)
                model.RecentRequests.Dequeue();

            return model.RecentRequests.Count < model.RpmLimit;
        }
    }

    public string? AssignRole(ResearchRole role, string? prefer = null)
    {
        var weights = RoleWeights[role];
        double bestScore = -1;
        string? bestModel = null;

        foreach (var model in _models.Values)
        {
            if (!IsAvailable(model.Name)) continue;

            double score = model.CodingScore * weights.Coding +
                           model.ReasoningScore * weights.Reasoning +
                           model.ReadingScore * weights.Reading +
                           model.InstructionScore * weights.Instruction +
                           model.SearchScore * weights.Search;

            if (prefer != null && model.Name.Contains(prefer, StringComparison.OrdinalIgnoreCase))
                score *= 1.1;

            if (model.Status == PoolModelStatus.Degraded)
                score *= 0.7;

            if (score > bestScore)
            {
                bestScore = score;
                bestModel = model.Name;
            }
        }

        return bestModel;
    }

    public Dictionary<ResearchRole, string?> AssignTeam()
    {
        var assigned = new List<string>();
        var team = new Dictionary<ResearchRole, string?>();

        foreach (var role in Enum.GetValues<ResearchRole>())
        {
            var model = AssignRole(role);
            if (model != null && !assigned.Contains(model))
            {
                assigned.Add(model);
                team[role] = model;
            }
        }

        return team;
    }

    public int RecommendChunkSize(string name, double safetyFactor = 0.7)
    {
        return _models.TryGetValue(name, out var model)
            ? (int)(model.ContextWindow * safetyFactor)
            : 16384;
    }

    private void RegisterPresets()
    {
        Register("siliconflow-flash", coding: 0.7, reasoning: 0.5, reading: 0.6, instruction: 0.7, contextWindow: 32768, rpmLimit: 60);
        Register("longcat-free", coding: 0.5, reasoning: 0.6, reading: 0.7, instruction: 0.6, contextWindow: 131072, rpmLimit: 30);
        Register("dmxapi-free", coding: 0.6, reasoning: 0.5, reading: 0.5, instruction: 0.6, contextWindow: 32768, rpmLimit: 20);
        Register("mofang-free", coding: 0.5, reasoning: 0.7, reading: 0.6, instruction: 0.5, contextWindow: 65536, rpmLimit: 20);
        Register("modelscope-free", coding: 0.5, reasoning: 0.6, reading: 0.5, instruction: 0.6, contextWindow: 32768, rpmLimit: 30);
        Register("stepfun-free", coding: 0.6, reasoning: 0.6, reading: 0.5, instruction: 0.6, contextWindow: 32768, rpmLimit: 20);
        Register("internlm-free", coding: 0.6, reasoning: 0.7, reading: 0.7, instruction: 0.5, contextWindow: 65536, rpmLimit: 15);
        Register("sensetime-free", coding: 0.5, reasoning: 0.5, reading: 0.5, instruction: 0.5, contextWindow: 32768, rpmLimit: 10);
        Register("qwen-free", coding: 0.7, reasoning: 0.6, reading: 0.5, instruction: 0.7, contextWindow: 32768, rpmLimit: 60);
        Register("default-free", coding: 0.4, reasoning: 0.4, reading: 0.4, instruction: 0.5, contextWindow: 8192, rpmLimit: 5);
    }

    public List<FreeModelProfile> GetAllProfiles() => _models.Values.ToList();

    public FreeModelProfile? GetProfile(string name) =>
        _models.TryGetValue(name, out var model) ? model : null;

    public Dictionary<string, object> GetStats()
    {
        var statuses = _models.Values
            .GroupBy(m => m.Status)
            .ToDictionary(g => g.Key.ToString(), g => g.Count());

        return new Dictionary<string, object>
        {
            ["models"] = _models.Count,
            ["healthy"] = statuses.GetValueOrDefault("Healthy", 0),
            ["degraded"] = statuses.GetValueOrDefault("Degraded", 0),
            ["quarantined"] = statuses.GetValueOrDefault("Quarantined", 0),
            ["rate_limited"] = statuses.GetValueOrDefault("RateLimited", 0),
            ["statuses"] = statuses
        };
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_persistPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var data = new { models = _models.Values.ToList(), saved_at = DateTime.UtcNow.ToString("O") };
            File.WriteAllText(_persistPath, JsonSerializer.Serialize(data, JsonOptions));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("FreeModelPool: Save failed: {Message}", ex.Message);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_persistPath)) return;

            var json = File.ReadAllText(_persistPath);
            var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (data == null) return;

            if (data.TryGetValue("models", out var models))
            {
                var loaded = JsonSerializer.Deserialize<List<FreeModelProfile>>(models.GetRawText());
                if (loaded != null)
                    foreach (var m in loaded)
                        _models[m.Name] = m;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("FreeModelPool: Load failed: {Message}", ex.Message);
        }
    }
}
