using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed record DreamCycleResult
{
    public int MemoriesConsolidated { get; init; }
    public int MemoriesForgotten { get; init; }
    public int PatternsMerged { get; init; }
    public int KnowledgeDistilled { get; init; }
    public string Summary { get; init; } = "";
}

public sealed class DreamCycle : BackgroundService
{
    private readonly SynapticMemory _memory;
    private readonly KnowledgeGraphBridge _graphBridge;
    private readonly SkillTree _skillTree;
    private readonly MetaCognitiveLayer _metaCognition;
    private readonly ILogger<DreamCycle> _logger;
    private readonly TimeSpan _cycleInterval;
    private readonly string _dreamLogPath;
    private volatile bool _isIdle;
    private DateTime _lastInteraction = DateTime.UtcNow;
    private int _totalDreams;

    public DreamCycle(
        SynapticMemory memory,
        KnowledgeGraphBridge graphBridge,
        SkillTree skillTree,
        MetaCognitiveLayer metaCognition,
        ILogger<DreamCycle>? logger = null,
        TimeSpan? cycleInterval = null)
    {
        _memory = memory;
        _graphBridge = graphBridge;
        _skillTree = skillTree;
        _metaCognition = metaCognition;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DreamCycle>.Instance;
        _cycleInterval = cycleInterval ?? TimeSpan.FromMinutes(10);
        _dreamLogPath = Path.Combine(AppContext.BaseDirectory, "synaptic", "dream_log.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_dreamLogPath)!);

        _logger.LogInformation("DreamCycle initialized: interval={Interval}s, dream log={Path}", _cycleInterval.TotalSeconds, _dreamLogPath);
    }

    public void RecordInteraction()
    {
        _lastInteraction = DateTime.UtcNow;
        _isIdle = false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DreamCycle started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var idleDuration = DateTime.UtcNow - _lastInteraction;
                if (idleDuration >= _cycleInterval)
                {
                    _isIdle = true;
                    await DreamAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Dream cycle failed");
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    private async Task DreamAsync(CancellationToken ct)
    {
        _logger.LogInformation("Dream cycle #{Count} beginning...", _totalDreams + 1);

        var consolidated = ConsolidateMemories();
        var forgotten = ForgetWeakMemories();
        var merged = MergeSimilarPatterns();
        var distilled = DistillKnowledgeToGraph();

        _totalDreams++;

        var result = new DreamCycleResult
        {
            MemoriesConsolidated = consolidated,
            MemoriesForgotten = forgotten,
            PatternsMerged = merged,
            KnowledgeDistilled = distilled,
            Summary = $"Dream #{_totalDreams}: consolidated={consolidated}, forgotten={forgotten}, merged={merged}, distilled={distilled}"
        };

        await PersistDreamResultAsync(result, ct);

        _logger.LogInformation("Dream cycle #{Count} complete: {Summary}", _totalDreams, result.Summary);
    }

    private readonly object _persistLock = new();

    private async Task PersistDreamResultAsync(DreamCycleResult result, CancellationToken ct)
    {
        try
        {
            lock (_persistLock)
            {
                List<DreamCycleResult> history;
                if (File.Exists(_dreamLogPath))
                {
                    var json = File.ReadAllText(_dreamLogPath);
                    history = System.Text.Json.JsonSerializer.Deserialize<List<DreamCycleResult>>(json) ?? new();
                }
                else
                {
                    history = new();
                }

                history.Add(result);

                if (history.Count > 100)
                    history = history.OrderByDescending(d => d.MemoriesConsolidated + d.KnowledgeDistilled).Take(50).ToList();

                var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(_dreamLogPath, System.Text.Json.JsonSerializer.Serialize(history, options));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist dream result");
        }
    }

    private int ConsolidateMemories()
    {
        var untrained = _memory.GetRecentUntrained(100);
        var teachingExperiences = untrained.Where(e => e.Type == SynapseType.Teaching)
            .OrderByDescending(e => ComputeExperiencePriority(e))
            .ToList();

        var consolidated = 0;
        foreach (var exp in teachingExperiences.Take(10))
        {
            _graphBridge.IngestExperience(exp.Query, exp.Response, exp.Label);
            exp.UsedForTraining = true;
            consolidated++;
        }

        return consolidated;
    }

    private static float ComputeExperiencePriority(SynapticExperience exp)
    {
        var recencyBonus = (float)Math.Max(0, 1.0 - (DateTime.UtcNow - exp.CreatedAt).TotalHours / 24.0);
        var rewardWeight = exp.Reward;
        var typeBonus = exp.Type switch
        {
            SynapseType.Teaching => 0.3f,
            SynapseType.Correction => 0.2f,
            SynapseType.Feedback => 0.1f,
            _ => 0f
        };

        return recencyBonus * 0.4f + rewardWeight * 0.4f + typeBonus;
    }

    private int ForgetWeakMemories()
    {
        var oldInteractions = _memory.GetExperiencesByType(SynapseType.Interaction, 500)
            .Where(e => e.Reward < 0.3f && (DateTime.UtcNow - e.CreatedAt).TotalHours > 24)
            .ToList();

        foreach (var exp in oldInteractions)
        {
            _memory.DeleteExperience(exp.Id);
        }

        var forgotten = oldInteractions.Count;
        _logger.LogInformation("Dream cycle: forgot {Count} weak memories (reward<0.3, age>24h)", forgotten);
        return forgotten;
    }

    private int MergeSimilarPatterns()
    {
        var teachings = _memory.GetExperiencesByType(SynapseType.Teaching, 200)
            .Where(e => !e.UsedForTraining)
            .ToList();

        var merged = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var exp in teachings)
        {
            var normalized = NormalizeText(exp.Query);
            if (seen.Any(s => Similarity(normalized, s) > 0.8f))
            {
                merged++;
                continue;
            }
            seen.Add(normalized);
        }

        if (merged > 0)
            _logger.LogInformation("Dream cycle: merged {Count} similar patterns", merged);
        return merged;
    }

    private static string NormalizeText(string text)
    {
        return System.Text.RegularExpressions.Regex.Replace(text.ToLowerInvariant(), @"[^\w\s]", "").Trim();
    }

    private static float Similarity(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0f;

        var wordsA = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var wordsB = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

        if (wordsA.Count == 0 || wordsB.Count == 0) return 0f;

        var intersection = wordsA.Intersect(wordsB).Count();
        var union = wordsA.Union(wordsB).Count();

        return union > 0 ? (float)intersection / union : 0f;
    }

    private int DistillKnowledgeToGraph()
    {
        var teachings = _memory.GetExperiencesByType(SynapseType.Teaching, 50)
            .Where(e => !e.UsedForTraining)
            .Take(5)
            .ToList();

        var count = 0;
        foreach (var exp in teachings)
        {
            _graphBridge.IngestExperience(exp.Query, exp.Response, exp.Label);
            exp.UsedForTraining = true;
            count++;
        }

        return count;
    }

    public async Task ForceDreamAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Forced dream cycle triggered");
        await DreamAsync(ct);
    }
}
