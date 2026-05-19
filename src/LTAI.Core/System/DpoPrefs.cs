using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.Prefs;

public record PreferencePair(
    string PairId,
    string Context,
    string Chosen,
    string Rejected,
    string Source,
    DateTime Timestamp,
    Dictionary<string, string> Metadata);

public sealed class PreferenceTracker
{
    private readonly ConcurrentDictionary<string, double> _scores = new();
    private readonly List<PreferencePair> _history = new();
    private readonly object _lock = new();
    private const int MaxHistory = 500;
    private readonly ILogger<PreferenceTracker> _logger;

    public IReadOnlyDictionary<string, double> Scores => _scores;
    public IReadOnlyList<PreferencePair> History
    {
        get { lock (_lock) return _history.ToList(); }
    }

    public PreferenceTracker() : this(NullLogger<PreferenceTracker>.Instance) { }

    public PreferenceTracker(ILogger<PreferenceTracker> logger)
    {
        _logger = logger ?? NullLogger<PreferenceTracker>.Instance;
    }

    public void Record(string context, string chosen, string rejected, string source, Dictionary<string, string>? metadata = null)
    {
        var pair = new PreferencePair(
            Guid.NewGuid().ToString("N"),
            context,
            chosen,
            rejected,
            source,
            DateTime.UtcNow,
            metadata ?? new Dictionary<string, string>());

        lock (_lock)
        {
            _history.Add(pair);
            while (_history.Count > MaxHistory)
                _history.RemoveAt(0);
        }

        _scores.AddOrUpdate(chosen, 1.0, (_, v) => Math.Clamp(v + 0.1, 0.0, 1.0));
        _scores.AddOrUpdate(rejected, 0.0, (_, v) => Math.Clamp(v - 0.1, 0.0, 1.0));

        _logger.LogDebug("Recorded preference: {Chosen} > {Rejected} (source: {Source})", chosen, rejected, source);
    }

    public void RecordImplicit(string context, string response, bool wasAccepted)
    {
        var delta = wasAccepted ? 0.05 : -0.05;
        _scores.AddOrUpdate(response, wasAccepted ? 0.55 : 0.45, (_, v) => Math.Clamp(v + delta, 0.0, 1.0));

        _logger.LogDebug("Implicit preference: {Response} accepted={WasAccepted}", response, wasAccepted);
    }

    public double GetScore(string entity)
    {
        return _scores.GetValueOrDefault(entity, 0.5);
    }

    public List<string> RankEntities(IEnumerable<string> entities)
    {
        return entities.OrderByDescending(e => GetScore(e)).ToList();
    }

    public string? BestEntity(IEnumerable<string> entities)
    {
        return entities.OrderByDescending(e => GetScore(e)).FirstOrDefault();
    }

    public double PreferenceStrength(string entityA, string entityB)
    {
        var scoreA = GetScore(entityA);
        var scoreB = GetScore(entityB);
        return 1.0 / (1.0 + Math.Exp(-(scoreA - scoreB) * 5.0));
    }
}

public sealed class PreferenceRouter
{
    private static readonly Lazy<PreferenceRouter> _instance = new(() => new PreferenceRouter());
    public static PreferenceRouter Instance => _instance.Value;

    private readonly PreferenceTracker _tracker;
    private readonly ILogger<PreferenceRouter> _logger;

    public PreferenceRouter() : this(new PreferenceTracker(), NullLogger<PreferenceRouter>.Instance) { }

    public PreferenceRouter(PreferenceTracker tracker, ILogger<PreferenceRouter> logger)
    {
        _tracker = tracker;
        _logger = logger ?? NullLogger<PreferenceRouter>.Instance;
    }

    public string? RouteModel(IEnumerable<string> candidates)
    {
        var best = _tracker.BestEntity(candidates);
        _logger.LogDebug("Routed model: {Best}", best);
        return best;
    }

    public string? RouteSkill(string taskContext, IEnumerable<string> availableSkills)
    {
        var best = _tracker.BestEntity(availableSkills);
        _logger.LogDebug("Routed skill for '{Context}': {Best}", taskContext, best);
        return best;
    }

    public bool ShouldRetry(string entity, double fallback = 0.5)
    {
        var score = _tracker.GetScore(entity);
        return _tracker.PreferenceStrength(entity, "_fallback_") > fallback || score >= fallback;
    }
}

public sealed class DpoPrefs
{
    private static readonly Lazy<DpoPrefs> _instance = new(() => new DpoPrefs());
    public static DpoPrefs Instance => _instance.Value;

    private readonly PreferenceTracker _tracker;
    private readonly PreferenceRouter _router;
    private readonly ILogger<DpoPrefs> _logger;

    public PreferenceTracker Tracker => _tracker;
    public PreferenceRouter Router => _router;

    public DpoPrefs() : this(new PreferenceTracker(), NullLogger<DpoPrefs>.Instance) { }

    public DpoPrefs(PreferenceTracker tracker, ILogger<DpoPrefs> logger)
    {
        _tracker = tracker;
        _router = new PreferenceRouter(tracker, NullLogger<PreferenceRouter>.Instance);
        _logger = logger ?? NullLogger<DpoPrefs>.Instance;
    }

    public void OnHitlDecision(string requestId, bool approved, string context)
    {
        var label = approved ? "approved" : "rejected";
        _tracker.RecordImplicit(context, $"hitl_{requestId}", approved);
        _logger.LogInformation("HITL decision: {RequestId} {Label}", requestId, label);
    }

    public void OnUserEdit(string original, string edited, string context)
    {
        _tracker.Record(context, edited, original, "user_edit", new Dictionary<string, string>
        {
            ["original"] = original,
            ["edited"] = edited
        });
        _logger.LogInformation("User edit preference recorded: {Context}", context);
    }

    public void OnModelElection(string chosenModel, IEnumerable<string> rejectedModels, string task)
    {
        foreach (var rejected in rejectedModels)
        {
            _tracker.Record(task, chosenModel, rejected, "model_election", new Dictionary<string, string>
            {
                ["task"] = task
            });
        }

        _logger.LogInformation("Model election: {Chosen} over {Count} rejected", chosenModel, rejectedModels.Count());
    }

    public void OnToolResult(string toolName, bool success, string context)
    {
        _tracker.RecordImplicit(context, toolName, success);
        _logger.LogInformation("Tool result: {ToolName} success={Success}", toolName, success);
    }

    public void SaveToJsonl()
    {
        var dir = Path.Combine(".livingtree", "preferences");
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var filePath = Path.Combine(dir, $"preferences_{DateTime.UtcNow:yyyyMMdd}.jsonl");
        var pairs = _tracker.History;

        using var writer = new StreamWriter(filePath, append: true);
        foreach (var pair in pairs)
        {
            writer.WriteLine(JsonSerializer.Serialize(pair));
        }

        _logger.LogInformation("Saved {Count} preference pairs to {Path}", pairs.Count, filePath);
    }

    public void LoadFromJsonl()
    {
        var dir = Path.Combine(".livingtree", "preferences");
        if (!Directory.Exists(dir))
            return;

        foreach (var file in Directory.GetFiles(dir, "preferences_*.jsonl").OrderByDescending(f => f))
        {
            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    var pair = JsonSerializer.Deserialize<PreferencePair>(line);
                    if (pair != null)
                        _tracker.Record(pair.Context, pair.Chosen, pair.Rejected, pair.Source, pair.Metadata);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to deserialize preference pair from {File}", file);
                }
            }

            _logger.LogInformation("Loaded preferences from {File}", file);
            break;
        }
    }

    public Dictionary<string, object> Stats()
    {
        return new Dictionary<string, object>
        {
            ["pair_count"] = _tracker.History.Count,
            ["entities_tracked"] = _tracker.Scores.Count
        };
    }
}
