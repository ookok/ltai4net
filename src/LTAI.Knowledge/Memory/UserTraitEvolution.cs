using LTAI.Knowledge.Memory.Models;

namespace LTAI.Knowledge.Memory;

public static class TraitEvolutionConstants
{
    public const float MomentumBeta = 0.85f;
    public const int MaxSnapshots = 500;

    public static readonly Dictionary<string, float> DefaultTraits = new()
    {
        ["EngagementDepth"] = 0.5f,
        ["TechnicalSophistication"] = 0.5f,
        ["PatienceTolerance"] = 0.7f,
        ["FeedbackDirectness"] = 0.5f,
        ["TopicBreadth"] = 0.4f,
        ["InteractionRegularity"] = 0.5f,
        ["DelegationComfort"] = 0.5f
    };

    public static readonly Dictionary<string, (List<string> Increases, List<string> Decreases)> TraitBehaviorSignals = new()
    {
        ["EngagementDepth"] = (
            ["detailed", "elaborate", "deep", "thorough", "explain more", "in detail", "详细", "深入", "仔细", "全面", "多说一点"],
            ["brief", "short", "quick", "simple", "just tell me", "summarize", "简要", "简短", "快速", "简单", "一句话"]
        ),
        ["TechnicalSophistication"] = (
            ["api", "python", "code", "function", "algorithm", "database", "sql", "json", "xml", "docker", "git", "compile", "debug", "framework", "library", "class", "interface", "编程", "算法", "数据库", "接口", "框架"],
            ["easy", "beginner", "non-technical", "what is", "how do i start", "新手", "入门", "基础", "简单操作"]
        ),
        ["PatienceTolerance"] = (
            ["take your time", "no rush", "it's ok", "whenever", "不急", "慢慢来", "没关系", "有空再说"],
            ["hurry", "quick", "fast", "now", "immediately", "asap", "urgent", "快", "马上", "立刻", "赶紧", "急"]
        ),
        ["FeedbackDirectness"] = (
            ["wrong", "incorrect", "no", "bad", "not what i want", "change", "fix", "错了", "不对", "不好", "改", "修"],
            ["maybe", "perhaps", "i think", "it seems", "could be", "possibly", "可能", "也许", "似乎", "大概"]
        ),
        ["TopicBreadth"] = (
            ["also", "another", "besides", "additionally", "furthermore", "different topic", "by the way", "还有", "另外", "顺便", "再说", "切换话题"],
            ["same", "similar", "like before", "as discussed", "继续", "同样的", "刚才说的"]
        ),
        ["InteractionRegularity"] = (
            ["good morning", "hello again", "back", "continuing", "daily", "我又来了", "又回来了", "继续", "每天"],
            ["new", "first time", "hi", "首次", "第一次", "新人"]
        ),
        ["DelegationComfort"] = (
            ["you decide", "you handle", "do it", "take care of", "figure out", "handle it", "你决定", "你处理", "你来弄", "交给你"],
            ["let me", "i will", "we should", "together", "我来", "我自己", "我们一起"]
        )
    };
}

public class MomentumPersonalityUpdater
{
    private readonly float _beta;

    public MomentumPersonalityUpdater(float beta = 0.85f)
    {
        _beta = Math.Clamp(beta, 0f, 1f);
    }

    public Dictionary<string, float> Update(Dictionary<string, float> current, Dictionary<string, float> inferred)
    {
        var result = new Dictionary<string, float>();

        foreach (var key in TraitEvolutionConstants.DefaultTraits.Keys)
        {
            var currentVal = current.TryGetValue(key, out var cv) ? cv : TraitEvolutionConstants.DefaultTraits[key];
            var inferredVal = inferred.TryGetValue(key, out var iv) ? iv : currentVal;
            var updated = _beta * currentVal + (1f - _beta) * inferredVal;
            result[key] = Math.Clamp(updated, 0.05f, 0.95f);
        }

        return result;
    }

    public List<string> ShouldEvolve(Dictionary<string, float> oldTraits, Dictionary<string, float> newTraits, float threshold = 0.10f)
    {
        var evolving = new List<string>();

        foreach (var (key, newVal) in newTraits)
        {
            var oldVal = oldTraits.TryGetValue(key, out var ov) ? ov : TraitEvolutionConstants.DefaultTraits[key];
            if (MathF.Abs(newVal - oldVal) > threshold)
                evolving.Add(key);
        }

        return evolving;
    }
}

public sealed class UserTraitEvolutionTree
{
    private static readonly Lazy<UserTraitEvolutionTree> _instance = new(() => new UserTraitEvolutionTree());
    public static UserTraitEvolutionTree Instance => _instance.Value;

    private readonly Dictionary<string, Queue<UserTraitSnapshot>> _users = [];
    private readonly Dictionary<string, Dictionary<string, float>> _currentTraits = [];
    private readonly MomentumPersonalityUpdater _updater = new(0.85f);
    private const int MaxSnapshots = 500;

    public Dictionary<string, float> InferFromConversation(List<string> messages, string userId = "default")
    {
        if (messages.Count == 0)
            return new Dictionary<string, float>(TraitEvolutionConstants.DefaultTraits);

        var combined = string.Join(" ", messages);

        var inferred = new Dictionary<string, float>
        {
            ["EngagementDepth"] = InferEngagementDepth(messages),
            ["TechnicalSophistication"] = InferTechnicalSophistication(combined),
            ["PatienceTolerance"] = InferPatienceTolerance(combined),
            ["FeedbackDirectness"] = InferFeedbackDirectness(combined),
            ["TopicBreadth"] = InferTopicBreadth(combined),
            ["InteractionRegularity"] = InferRegularity(messages),
            ["DelegationComfort"] = InferDelegationComfort(combined)
        };

        lock (_currentTraits)
        {
            var current = GetTraitVectorUnsafe(userId);
            var smoothed = _updater.Update(current, inferred);
            _currentTraits[userId] = smoothed;

            var now = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
            var snapshot = new UserTraitSnapshot(
                UserId: userId,
                Generation: GetNextGeneration(userId),
                Traits: new Dictionary<string, float>(smoothed),
                Timestamp: now,
                ConversationStats: new Dictionary<string, object>
                {
                    ["message_count"] = messages.Count,
                    ["combined_length"] = combined.Length
                }
            );

            if (!_users.TryGetValue(userId, out var queue))
            {
                queue = new Queue<UserTraitSnapshot>();
                _users[userId] = queue;
            }

            while (queue.Count >= MaxSnapshots)
                queue.Dequeue();

            queue.Enqueue(snapshot);
        }

        return new Dictionary<string, float>(_currentTraits.TryGetValue(userId, out var traits)
            ? traits
            : TraitEvolutionConstants.DefaultTraits);
    }

    public Dictionary<string, float> GetTraitVector(string userId = "default")
    {
        lock (_currentTraits)
        {
            return GetTraitVectorUnsafe(userId);
        }
    }

    private Dictionary<string, float> GetTraitVectorUnsafe(string userId)
    {
        return _currentTraits.TryGetValue(userId, out var traits)
            ? new Dictionary<string, float>(traits)
            : new Dictionary<string, float>(TraitEvolutionConstants.DefaultTraits);
    }

    private int GetNextGeneration(string userId)
    {
        if (_users.TryGetValue(userId, out var queue) && queue.Count > 0)
            return queue.Max(s => s.Generation) + 1;
        if (_currentTraits.TryGetValue(userId, out var _))
            return 1;
        return 0;
    }

    public TraitGrowthReport GetGrowthSummary(string userId = "default")
    {
        lock (_currentTraits)
        {
            if (!_users.TryGetValue(userId, out var queue) || queue.Count == 0)
            {
                return new TraitGrowthReport(0, 0, 0, [], [], [], []);
            }

            var snapshots = queue.ToList();
            var first = snapshots[0];
            var last = snapshots[^1];

            var span = last.Generation - first.Generation + 1;
            var deltas = new Dictionary<string, float>();
            var trends = new Dictionary<string, string>();
            var emergent = new List<string>();
            var stable = new List<string>();

            foreach (var key in TraitEvolutionConstants.DefaultTraits.Keys)
            {
                var oldVal = first.Traits.TryGetValue(key, out var ov) ? ov : TraitEvolutionConstants.DefaultTraits[key];
                var newVal = last.Traits.TryGetValue(key, out var nv) ? nv : TraitEvolutionConstants.DefaultTraits[key];
                var delta = newVal - oldVal;
                deltas[key] = delta;

                if (MathF.Abs(delta) < 0.02f)
                {
                    trends[key] = "stable";
                    stable.Add(key);
                }
                else if (delta > 0f)
                {
                    trends[key] = "increasing";
                }
                else
                {
                    trends[key] = "decreasing";
                }

                if (newVal >= 0.65f && oldVal < 0.65f)
                    emergent.Add(key);
            }

            return new TraitGrowthReport(first.Generation, last.Generation, span, deltas, trends, emergent, stable);
        }
    }

    public List<float> GetTraitTimeline(string userId, string trait)
    {
        lock (_currentTraits)
        {
            if (!_users.TryGetValue(userId, out var queue) || queue.Count == 0)
                return [];

            return [.. queue
                .Select(s => s.Traits.TryGetValue(trait, out var val) ? val : 0f)];
        }
    }

    private static float InferEngagementDepth(List<string> messages)
    {
        var avgLen = messages.Count > 0 ? (float)messages.Average(m => m.Length) : 10f;
        var score = MathF.Min(1f, avgLen / 120f);
        return Math.Clamp(score, 0.1f, 0.95f);
    }

    private static float InferTechnicalSophistication(string combined)
    {
        var (increases, _) = TraitEvolutionConstants.TraitBehaviorSignals["TechnicalSophistication"];
        var hits = increases.Count(kw => combined.Contains(kw, StringComparison.OrdinalIgnoreCase));
        var score = MathF.Min(0.95f, 0.25f + hits * 0.08f);
        return score;
    }

    private static float InferPatienceTolerance(string combined)
    {
        var (increases, decreases) = TraitEvolutionConstants.TraitBehaviorSignals["PatienceTolerance"];
        var incHits = increases.Count(kw => combined.Contains(kw, StringComparison.OrdinalIgnoreCase));
        var decHits = decreases.Count(kw => combined.Contains(kw, StringComparison.OrdinalIgnoreCase));
        var baseVal = 0.7f;
        var adjusted = baseVal + incHits * 0.05f - decHits * 0.08f;
        return Math.Clamp(adjusted, 0.1f, 0.95f);
    }

    private static float InferFeedbackDirectness(string combined)
    {
        var (increases, decreases) = TraitEvolutionConstants.TraitBehaviorSignals["FeedbackDirectness"];
        var incHits = increases.Count(kw => combined.Contains(kw, StringComparison.OrdinalIgnoreCase));
        var decHits = decreases.Count(kw => combined.Contains(kw, StringComparison.OrdinalIgnoreCase));
        var baseVal = 0.5f;
        var adjusted = baseVal + incHits * 0.06f - decHits * 0.04f;
        return Math.Clamp(adjusted, 0.1f, 0.95f);
    }

    private float InferTopicBreadth(string combined)
    {
        var domains = 0;

        if (ContainsAny(combined, ["code", "programming", "api", "function", "python", "java", "c#", "编程", "代码", "程序"]))
            domains++;
        if (ContainsAny(combined, ["data", "analysis", "statistics", "chart", "数据分析", "统计"]))
            domains++;
        if (ContainsAny(combined, ["write", "article", "blog", "email", "写作", "文章", "博客"]))
            domains++;
        if (ContainsAny(combined, ["design", "ui", "ux", "style", "设计", "界面", "样式"]))
            domains++;

        return MathF.Min(0.95f, (float)domains / 4f + 0.3f);
    }

    private static float InferRegularity(List<string> messages)
    {
        if (messages.Count < 3)
            return 0.5f;

        var lengths = messages.Select(m => (float)m.Length).ToList();
        var mean = lengths.Average();
        var variance = lengths.Average(l => (l - mean) * (l - mean));
        var std = MathF.Sqrt(variance);
        var cv = mean > 0 ? std / mean : 0f;

        return Math.Clamp(1f - cv * 0.5f, 0.1f, 0.95f);
    }

    private static float InferDelegationComfort(string combined)
    {
        var (increases, decreases) = TraitEvolutionConstants.TraitBehaviorSignals["DelegationComfort"];
        var incHits = increases.Count(kw => combined.Contains(kw, StringComparison.OrdinalIgnoreCase));
        var decHits = decreases.Count(kw => combined.Contains(kw, StringComparison.OrdinalIgnoreCase));
        var baseVal = 0.5f;
        var adjusted = baseVal + incHits * 0.07f - decHits * 0.05f;
        return Math.Clamp(adjusted, 0.1f, 0.95f);
    }

    private static bool ContainsAny(string text, string[] keywords)
    {
        return keywords.Any(kw => text.Contains(kw, StringComparison.OrdinalIgnoreCase));
    }
}
