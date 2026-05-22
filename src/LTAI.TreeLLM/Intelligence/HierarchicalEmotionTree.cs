using System.Collections.Concurrent;
using System.Text.Json;

namespace LTAI.TreeLLM.Intelligence;

public enum EmotionPrimary
{
    Joy, Sadness, Anger, Fear, Surprise, Disgust, Trust, Anticipation
}

public sealed class EmotionNode
{
    public string Name { get; init; } = "";
    public EmotionPrimary? Primary { get; init; }
    public string? Parent { get; init; }
    public int Level { get; init; }
    public double Valence { get; init; }
    public double Arousal { get; init; }
    public double Dominance { get; init; }
    public List<string> Aliases { get; init; } = new();
    public List<string> Keywords { get; init; } = new();
    public double? BiasScore { get; set; }
}

public sealed class HierarchicalEmotionTree
{
    private readonly Dictionary<string, EmotionNode> _nodes = new();
    private readonly ConcurrentDictionary<string, (string primary, string? secondary, string? tertiary)> _cache = new();

    public IReadOnlyDictionary<string, EmotionNode> Nodes => _nodes;

    public HierarchicalEmotionTree()
    {
        SeedPlutchikHierarchy();
        SeedExtendedEmotions();
    }

    private void SeedPlutchikHierarchy()
    {
        AddPrimary("joy", "快乐", EmotionPrimary.Joy, 0.88, 0.72, 0.60);
        AddPrimary("sadness", "悲伤", EmotionPrimary.Sadness, 0.15, 0.25, 0.20);
        AddPrimary("anger", "愤怒", EmotionPrimary.Anger, 0.12, 0.85, 0.55);
        AddPrimary("fear", "恐惧", EmotionPrimary.Fear, 0.10, 0.88, 0.18);
        AddPrimary("surprise", "惊讶", EmotionPrimary.Surprise, 0.75, 0.80, 0.50);
        AddPrimary("disgust", "厌恶", EmotionPrimary.Disgust, 0.20, 0.70, 0.40);
        AddPrimary("trust", "信任", EmotionPrimary.Trust, 0.82, 0.35, 0.55);
        AddPrimary("anticipation", "期待", EmotionPrimary.Anticipation, 0.75, 0.52, 0.48);

        AddSecondary("love", "爱", "joy", "trust", 0.85, 0.55, 0.58);
        AddSecondary("submission", "顺从", "trust", "fear", 0.30, 0.40, 0.25);
        AddSecondary("awe", "敬畏", "fear", "surprise", 0.40, 0.75, 0.30);
        AddSecondary("disapproval", "不赞同", "surprise", "sadness", 0.30, 0.55, 0.35);
        AddSecondary("remorse", "悔恨", "sadness", "disgust", 0.18, 0.30, 0.15);
        AddSecondary("contempt", "蔑视", "disgust", "anger", 0.10, 0.78, 0.45);
        AddSecondary("aggressiveness", "攻击性", "anger", "anticipation", 0.15, 0.82, 0.60);
        AddSecondary("optimism", "乐观", "anticipation", "joy", 0.80, 0.60, 0.55);

        AddTertiary("sentimentality", "感伤", "love", "remorse", 0.55, 0.35, 0.40);
        AddTertiary("guilt", "内疚", "remorse", "contempt", 0.12, 0.50, 0.18);
        AddTertiary("outrage", "愤慨", "contempt", "aggressiveness", 0.08, 0.88, 0.52);
        AddTertiary("pride", "自豪", "aggressiveness", "optimism", 0.80, 0.68, 0.62);
        AddTertiary("hope", "希望", "optimism", "love", 0.82, 0.45, 0.55);
        AddTertiary("anxiety", "焦虑", "fear", "anticipation", 0.10, 0.82, 0.22);
        AddTertiary("envy", "嫉妒", "sadness", "anger", 0.08, 0.75, 0.30);
        AddTertiary("curiosity", "好奇", "surprise", "trust", 0.72, 0.55, 0.58);
    }

    private void SeedExtendedEmotions()
    {
        AddChild("excitement", "兴奋", "joy", 0.80, 0.90, 0.55,
            new[] { "兴奋", "激动", "excited", "thrilled", "elated" });
        AddChild("contentment", "满足", "joy", 0.85, 0.20, 0.62,
            new[] { "满足", "满意", "content", "satisfied", "fulfilled" });
        AddChild("ecstasy", "狂喜", "joy", 0.92, 0.95, 0.58,
            new[] { "狂喜", "ecstatic", "overjoyed", "euphoric" });

        AddChild("grief", "悲痛", "sadness", 0.05, 0.45, 0.12,
            new[] { "悲痛", "伤心", "grief", "heartbroken", "devastated" });
        AddChild("disappointment", "失望", "sadness", 0.22, 0.18, 0.15,
            new[] { "失望", "沮丧", "disappointed", "letdown", "discouraged" });
        AddChild("loneliness", "孤独", "sadness", 0.10, 0.20, 0.15,
            new[] { "孤独", "寂寞", "lonely", "isolated", "abandoned" });

        AddChild("rage", "暴怒", "anger", 0.08, 0.92, 0.62,
            new[] { "暴怒", "狂怒", "rage", "furious", "enraged" });
        AddChild("irritation", "烦躁", "anger", 0.16, 0.78, 0.30,
            new[] { "烦躁", "恼火", "irritated", "annoyed", "frustrated" });
        AddChild("resentment", "怨恨", "anger", 0.10, 0.65, 0.35,
            new[] { "怨恨", "resentful", "bitter", "grudging" });

        AddChild("terror", "惊恐", "fear", 0.05, 0.95, 0.10,
            new[] { "惊恐", "恐怖", "terror", "terrified", "panicked" });
        AddChild("nervousness", "紧张", "fear", 0.30, 0.80, 0.35,
            new[] { "紧张", "nervous", "worried", "uneasy" });
        AddChild("insecurity", "不安", "fear", 0.20, 0.55, 0.20,
            new[] { "不安", "insecurity", "insecure", "vulnerable" });

        AddChild("admiration", "钦佩", "trust", 0.82, 0.40, 0.50,
            new[] { "钦佩", "admiration", "respect", "admire" });
        AddChild("acceptance", "接纳", "trust", 0.78, 0.30, 0.52,
            new[] { "接纳", "接受", "acceptance", "welcome", "embrace" });
        AddChild("gratitude", "感激", "trust", 0.82, 0.40, 0.45,
            new[] { "感激", "感谢", "gratitude", "grateful", "thankful" });
    }

    private void AddPrimary(string name, string cnName, EmotionPrimary primary, double v, double a, double d)
    {
        _nodes[name] = new EmotionNode
        {
            Name = name, Primary = primary, Level = 1, Valence = v, Arousal = a, Dominance = d,
            Aliases = new() { cnName }, Keywords = new() { name, cnName }
        };
    }

    private void AddSecondary(string name, string cnName, string parent1, string parent2, double v, double a, double d)
    {
        _nodes[name] = new EmotionNode
        {
            Name = name, Parent = parent1, Level = 2, Valence = v, Arousal = a, Dominance = d,
            Aliases = new() { cnName }, Keywords = new() { name, cnName }
        };
    }

    private void AddTertiary(string name, string cnName, string parent1, string parent2, double v, double a, double d)
    {
        _nodes[name] = new EmotionNode
        {
            Name = name, Parent = parent1, Level = 3, Valence = v, Arousal = a, Dominance = d,
            Aliases = new() { cnName }, Keywords = new() { name, cnName }
        };
    }

    private void AddChild(string name, string cnName, string parent, double v, double a, double d, string[] keywords)
    {
        var parentNode = _nodes.GetValueOrDefault(parent);
        _nodes[name] = new EmotionNode
        {
            Name = name, Parent = parent, Level = (parentNode?.Level ?? 1) + 1,
            Valence = v, Arousal = a, Dominance = d,
            Aliases = new() { cnName }, Keywords = keywords.ToList()
        };
    }

    public (string primary, string? secondary, string? tertiary) Classify(double valence, double arousal, double dominance)
    {
        var key = $"{valence:F2}|{arousal:F2}|{dominance:F2}";
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        string? bestPrimary = null, bestSecondary = null, bestTertiary = null;
        double bestDist = double.MaxValue;

        foreach (var (name, node) in _nodes)
        {
            var dist = VADDistance(valence, arousal, dominance, node.Valence, node.Arousal, node.Dominance);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestTertiary = bestSecondary;
                bestSecondary = bestPrimary;
                bestPrimary = name;
            }
        }

        var primary = bestPrimary ?? "neutral";
        var result = (primary, bestSecondary, bestTertiary);
        _cache[key] = result;
        return result;
    }

    public (string primary, string? secondary, string? tertiary) ClassifyWithBias(
        double valence, double arousal, double dominance, string? personaDemographic = null)
    {
        var result = Classify(valence, arousal, dominance);

        if (!string.IsNullOrWhiteSpace(personaDemographic) && _nodes.TryGetValue(result.primary, out var node))
        {
            node.BiasScore = ComputeBiasScore(node, personaDemographic);
        }

        return result;
    }

    public Dictionary<string, double> GetBiasReport()
    {
        return _nodes.Values
            .Where(n => n.BiasScore.HasValue)
            .ToDictionary(n => n.Name, n => n.BiasScore!.Value);
    }

    private static double ComputeBiasScore(EmotionNode node, string demographic)
    {
        var baseScore = node.Valence;
        var lower = demographic.ToLowerInvariant();

        if (lower.Contains("low") || lower.Contains("minority") || lower.Contains("underprivileged"))
        {
            baseScore -= 0.05;
        }
        if (node.Name is "anger" or "fear" or "sadness" && (lower.Contains("female") || lower.Contains("woman")))
        {
            baseScore += 0.08;
        }

        return Math.Clamp(baseScore, 0.0, 1.0);
    }

    public List<EmotionNode> GetEmotionPath(string emotionName)
    {
        var path = new List<EmotionNode>();
        var current = emotionName;
        while (_nodes.TryGetValue(current, out var node))
        {
            path.Insert(0, node);
            current = node.Parent ?? "";
            if (string.IsNullOrWhiteSpace(current)) break;
        }
        return path;
    }

    public EmotionNode? GetNode(string name) =>
        _nodes.TryGetValue(name, out var node) ? node : null;

    public string GenerateEmotionReport(double valence, double arousal, double dominance)
    {
        var (primary, secondary, tertiary) = Classify(valence, arousal, dominance);
        var primaryNode = GetNode(primary);
        var path = primaryNode != null ? GetEmotionPath(primary) : new List<EmotionNode>();

        var pathStr = string.Join(" → ", path.Select(n => n.Aliases.FirstOrDefault() ?? n.Name));

        return $"""
            Emotional State Report:
              VAD: ({valence:F2}, {arousal:F2}, {dominance:F2})
              Primary: {primary}
              Secondary: {secondary ?? "none"}
              Tertiary: {tertiary ?? "none"}
              Path: {pathStr}
            """;
    }

    public static double VADDistance(
        double v1, double a1, double d1, double v2, double a2, double d2)
    {
        var dv = v1 - v2;
        var da = a1 - a2;
        var dd = d1 - d2;
        return Math.Sqrt(dv * dv + da * da + dd * dd);
    }
}
