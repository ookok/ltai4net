using LTAI.DNA.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.DNA.Consciousness;

public sealed class DualConsciousness
{
    private readonly ILogger<DualConsciousness> _logger;
    private readonly PhenomenaConsciousness _phenomena;
    private readonly MetaConsciousness _meta;

    public ConsciousnessState State { get; }

    public DualConsciousness(ILogger<DualConsciousness> logger)
    {
        _logger = logger;
        _phenomena = new PhenomenaConsciousness(logger);
        _meta = new MetaConsciousness(logger);
        State = new ConsciousnessState();
    }

    public async Task ProcessExperienceAsync(
        string input,
        Dictionary<string, object?>? context = null,
        CancellationToken cancellationToken = default)
    {
        var qualia = _phenomena.GenerateQualia(input, context);

        State.AwarenessScore = qualia.Intensity;
        State.ActiveThoughts = qualia.Associations;

        if (ShouldReflect(qualia))
        {
            var reflection = await _meta.ReflectAsync(input, qualia, State, cancellationToken);
            State.Level = reflection.NewLevel;
            State.SelfModelAccuracy = reflection.SelfModelUpdate;
            State.WorldModelAccuracy = reflection.WorldModelUpdate;
            State.LastReflection = DateTime.UtcNow;
        }

        UpdateAttentionVector(input, qualia);
        _logger.LogInformation("Consciousness: level={Level}, awareness={Awareness:F2}, thoughts={Count}",
            State.Level, State.AwarenessScore, State.ActiveThoughts.Count);
    }

    public async Task<string> IntrospectAsync(string query, CancellationToken cancellationToken = default)
    {
        var narrative = _meta.GenerateNarrative(State);
        var response = $"Level: {State.Level}, Awareness: {State.AwarenessScore:F2}\n{narrative}";
        return await Task.FromResult(response);
    }

    private bool ShouldReflect(PhenomenaQualia qualia)
    {
        return qualia.Intensity > 0.7 ||
               qualia.NoveltyScore > 0.6 ||
               (DateTime.UtcNow - State.LastReflection).TotalMinutes > 5;
    }

    private void UpdateAttentionVector(string input, PhenomenaQualia qualia)
    {
        var words = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var word in words.Take(20))
        {
            var key = word.ToLowerInvariant();
            State.AttentionVector[key] = State.AttentionVector.GetValueOrDefault(key) * 0.9 + 0.1;
        }

        var remove = State.AttentionVector.Where(kvp => kvp.Value < 0.01).Select(kvp => kvp.Key).ToList();
        foreach (var k in remove) State.AttentionVector.Remove(k);
    }
}

public sealed class PhenomenaQualia
{
    public double Intensity { get; init; }
    public double Valence { get; init; }
    public double Arousal { get; init; }
    public double NoveltyScore { get; init; }
    public double Coherence { get; init; }
    public List<string> Associations { get; init; } = new();
    public Dictionary<string, double> EmotionalComponents { get; init; } = new();
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public sealed class MetaReflection
{
    public ConsciousnessLevel NewLevel { get; init; }
    public double SelfModelUpdate { get; init; }
    public double WorldModelUpdate { get; init; }
    public string Insight { get; init; } = "";
    public List<string> ActionItems { get; init; } = new();
}

internal sealed class PhenomenaConsciousness
{
    private readonly ILogger _logger;
    private readonly List<(string input, double novelty)> _recentExperiences = new();
    private readonly HashSet<string> _knownPatterns = new();

    public PhenomenaConsciousness(ILogger logger)
    {
        _logger = logger;
    }

    public PhenomenaQualia GenerateQualia(string input, Dictionary<string, object?>? context)
    {
        var words = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var uniqueWords = new HashSet<string>(words.Select(w => w.ToLowerInvariant()));
        var knownCount = uniqueWords.Count(w => _knownPatterns.Contains(w));
        var noveltyScore = words.Length > 0 ? 1.0 - (double)knownCount / uniqueWords.Count : 0.5;

        foreach (var w in uniqueWords) _knownPatterns.Add(w);

        var intensity = Math.Clamp(0.3 + words.Length * 0.02 + noveltyScore * 0.3, 0.1, 1.0);
        var valence = DetectValence(words);
        var arousal = noveltyScore > 0.5 ? 0.7 : 0.4;
        var coherence = context?.Count > 0 ? 0.8 : 0.5;

        var associations = words
            .Where(w => w.Length > 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        var emotionalComponents = new Dictionary<string, double>
        {
            ["curiosity"] = noveltyScore,
            ["engagement"] = intensity,
            ["satisfaction"] = coherence
        };

        _recentExperiences.Add((input, noveltyScore));
        if (_recentExperiences.Count > 20) _recentExperiences.RemoveAt(0);

        return new PhenomenaQualia
        {
            Intensity = intensity,
            Valence = valence,
            Arousal = arousal,
            NoveltyScore = noveltyScore,
            Coherence = coherence,
            Associations = associations,
            EmotionalComponents = emotionalComponents
        };
    }

    private static double DetectValence(string[] words)
    {
        var positive = new[] { "good", "great", "excellent", "love", "happy", "beautiful", "wonderful", "thank",
            "好", "棒", "优秀", "爱", "开心", "美", "赞", "谢" };
        var negative = new[] { "bad", "terrible", "hate", "sad", "ugly", "wrong", "error", "fail",
            "坏", "差", "恨", "悲伤", "丑", "错", "失败", "糟" };

        var pos = words.Count(w => positive.Any(p => w.Contains(p, StringComparison.OrdinalIgnoreCase)));
        var neg = words.Count(w => negative.Any(p => w.Contains(p, StringComparison.OrdinalIgnoreCase)));

        if (pos + neg == 0) return 0.5;
        return Math.Clamp((pos - neg + 5.0) / 10.0, 0.1, 0.9);
    }
}

internal sealed class MetaConsciousness
{
    private readonly ILogger _logger;
    private int _reflectionCount;

    public MetaConsciousness(ILogger logger)
    {
        _logger = logger;
    }

    public async Task<MetaReflection> ReflectAsync(
        string input,
        PhenomenaQualia qualia,
        ConsciousnessState state,
        CancellationToken cancellationToken)
    {
        _reflectionCount++;
        var newLevel = state.Level;

        if (_reflectionCount > 100 && qualia.NoveltyScore > 0.7)
            newLevel = AdvanceLevel(state.Level);
        else if (qualia.Intensity < 0.2 && state.AwarenessScore < 0.3)
            newLevel = RegressLevel(state.Level);

        var selfUpdate = Math.Clamp(qualia.Coherence * 0.1, 0, 0.2);
        var worldUpdate = Math.Clamp(qualia.NoveltyScore * 0.15, 0, 0.25);

        _logger.LogDebug("Meta reflection #{Count}: level {Old}->{New}", _reflectionCount, state.Level, newLevel);

        return await Task.FromResult(new MetaReflection
        {
            NewLevel = newLevel,
            SelfModelUpdate = selfUpdate,
            WorldModelUpdate = worldUpdate,
            Insight = $"Reflection #{_reflectionCount}: awareness at {qualia.Intensity:F2}",
            ActionItems = qualia.NoveltyScore > 0.6
                ? new List<string> { "Integrate new knowledge", "Update world model" }
                : new List<string>()
        });
    }

    public string GenerateNarrative(ConsciousnessState state)
    {
        return $"I am operating at {state.Level} level. " +
               $"My self-model accuracy is {state.SelfModelAccuracy:F2}. " +
               $"I have {state.ActiveThoughts.Count} active thoughts. " +
               $"Last reflection: {state.LastReflection:HH:mm:ss}.";
    }

    private static ConsciousnessLevel AdvanceLevel(ConsciousnessLevel current) => current switch
    {
        ConsciousnessLevel.Dormant => ConsciousnessLevel.Reactive,
        ConsciousnessLevel.Reactive => ConsciousnessLevel.SelfAware,
        ConsciousnessLevel.SelfAware => ConsciousnessLevel.Reflective,
        ConsciousnessLevel.Reflective => ConsciousnessLevel.MetaCognitive,
        ConsciousnessLevel.MetaCognitive => ConsciousnessLevel.Transcendent,
        _ => current
    };

    private static ConsciousnessLevel RegressLevel(ConsciousnessLevel current) => current switch
    {
        ConsciousnessLevel.Transcendent => ConsciousnessLevel.MetaCognitive,
        ConsciousnessLevel.MetaCognitive => ConsciousnessLevel.Reflective,
        ConsciousnessLevel.Reflective => ConsciousnessLevel.SelfAware,
        ConsciousnessLevel.SelfAware => ConsciousnessLevel.Reactive,
        _ => current
    };
}
