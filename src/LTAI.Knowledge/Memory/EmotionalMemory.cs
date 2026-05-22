using System.Text.Json;
using LTAI.Knowledge.Memory.Models;

namespace LTAI.Knowledge.Memory;

public static class EmotionalMemoryConstants
{
    public static readonly EmotionType[] EMOTION_ORDER =
    [
        EmotionType.JOY,
        EmotionType.TRUST,
        EmotionType.FEAR,
        EmotionType.SURPRISE,
        EmotionType.SADNESS,
        EmotionType.DISGUST,
        EmotionType.ANGER,
        EmotionType.ANTICIPATION
    ];

    public static readonly Dictionary<EmotionType, double> EmotionAngle = new()
    {
        [EmotionType.JOY] = 0 * Math.PI / 4,
        [EmotionType.TRUST] = 1 * Math.PI / 4,
        [EmotionType.FEAR] = 2 * Math.PI / 4,
        [EmotionType.SURPRISE] = 3 * Math.PI / 4,
        [EmotionType.SADNESS] = 4 * Math.PI / 4,
        [EmotionType.DISGUST] = 5 * Math.PI / 4,
        [EmotionType.ANGER] = 6 * Math.PI / 4,
        [EmotionType.ANTICIPATION] = 7 * Math.PI / 4
    };

    public static readonly HashSet<EmotionType> Positive =
        [EmotionType.JOY, EmotionType.TRUST, EmotionType.ANTICIPATION];

    public static readonly HashSet<EmotionType> Negative =
        [EmotionType.SADNESS, EmotionType.FEAR, EmotionType.ANGER, EmotionType.DISGUST];

    public static readonly HashSet<EmotionType> Neutral =
        [EmotionType.SURPRISE];

    public static readonly Dictionary<EmotionType, List<string>> EmotionKeywords = new()
    {
        [EmotionType.JOY] =
        [
            "happy", "joy", "delighted", "excited", "cheerful", "elated", "thrilled", "pleased",
            "高兴", "快乐", "开心", "喜悦", "兴奋", "欢喜", "愉快", "欣喜", "欢乐", "幸福"
        ],
        [EmotionType.TRUST] =
        [
            "trust", "confident", "secure", "reliable", "faithful", "assured",
            "信任", "信赖", "相信", "可靠", "放心", "踏实", "依赖", "有信心"
        ],
        [EmotionType.FEAR] =
        [
            "fear", "afraid", "scared", "terrified", "anxious", "worried", "frightened", "panic",
            "害怕", "恐惧", "担心", "焦虑", "恐慌", "畏惧", "不安", "紧张", "惊吓", "胆怯"
        ],
        [EmotionType.SURPRISE] =
        [
            "surprise", "amazed", "astonished", "shocked", "stunned", "wonder",
            "惊讶", "惊奇", "震惊", "意外", "诧异", "吃惊", "目瞪口呆"
        ],
        [EmotionType.SADNESS] =
        [
            "sad", "sorrow", "depressed", "grief", "unhappy", "mournful", "melancholy", "gloomy", "lonely", "heartbroken",
            "悲伤", "难过", "伤心", "忧郁", "沮丧", "失落", "哀伤", "悲痛", "消沉", "孤独"
        ],
        [EmotionType.DISGUST] =
        [
            "disgust", "revulsion", "repulsed", "sickened", "nauseated", "aversion",
            "厌恶", "反感", "恶心", "憎恶", "讨厌", "嫌弃", "反胃", "令人作呕"
        ],
        [EmotionType.ANGER] =
        [
            "anger", "angry", "furious", "rage", "irritated", "annoyed", "outraged", "frustrated", "mad", "hostile",
            "愤怒", "生气", "恼火", "发怒", "怒火", "气愤", "暴躁", "愤慨", "怨恨", "恼怒"
        ],
        [EmotionType.ANTICIPATION] =
        [
            "anticipation", "expectant", "hopeful", "eager", "looking forward", "optimistic",
            "期待", "期望", "期盼", "憧憬", "向往", "盼望", "翘首以待"
        ]
    };

    public static readonly Dictionary<(EmotionType, EmotionType), string> PlutchikDyads = new()
    {
        [(EmotionType.JOY, EmotionType.TRUST)] = "love",
        [(EmotionType.TRUST, EmotionType.FEAR)] = "submission",
        [(EmotionType.FEAR, EmotionType.SURPRISE)] = "alarm",
        [(EmotionType.SURPRISE, EmotionType.SADNESS)] = "disappointment",
        [(EmotionType.SADNESS, EmotionType.DISGUST)] = "remorse",
        [(EmotionType.DISGUST, EmotionType.ANGER)] = "contempt",
        [(EmotionType.ANGER, EmotionType.ANTICIPATION)] = "aggressiveness",
        [(EmotionType.ANTICIPATION, EmotionType.JOY)] = "optimism",

        [(EmotionType.JOY, EmotionType.FEAR)] = "guilt",
        [(EmotionType.TRUST, EmotionType.SURPRISE)] = "curiosity",
        [(EmotionType.FEAR, EmotionType.SADNESS)] = "despair",
        [(EmotionType.SURPRISE, EmotionType.DISGUST)] = "shock",
        [(EmotionType.SADNESS, EmotionType.ANGER)] = "envy",
        [(EmotionType.DISGUST, EmotionType.ANTICIPATION)] = "cynicism",
        [(EmotionType.ANGER, EmotionType.JOY)] = "pride",
        [(EmotionType.ANTICIPATION, EmotionType.TRUST)] = "fatalism"
    };
}

public static class EmotionalMemoryUtility
{
    public static EmotionVector DetectEmotion(string text)
    {
        var scores = new float[8];
        var textLower = text.ToLowerInvariant();

        for (var i = 0; i < EmotionalMemoryConstants.EMOTION_ORDER.Length; i++)
        {
            var emotion = EmotionalMemoryConstants.EMOTION_ORDER[i];
            if (!EmotionalMemoryConstants.EmotionKeywords.TryGetValue(emotion, out var keywords))
                continue;

            var hits = 0;
            foreach (var kw in keywords)
            {
                if (textLower.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    hits++;
            }

            scores[i] = MathF.Min(1f, hits * 0.25f);
        }

        var sum = scores.Sum();
        if (sum > 0f)
        {
            for (var i = 0; i < scores.Length; i++)
                scores[i] /= sum;
        }
        else
        {
            scores[0] = 0.125f;
            scores[1] = 0.125f;
            scores[2] = 0.125f;
            scores[3] = 0.125f;
            scores[4] = 0.125f;
            scores[5] = 0.125f;
            scores[6] = 0.125f;
            scores[7] = 0.125f;
        }

        return EmotionVector.FromList(scores);
    }

    public static string DyadName(EmotionType a, EmotionType b)
    {
        if (EmotionalMemoryConstants.PlutchikDyads.TryGetValue((a, b), out var name))
            return name;
        if (EmotionalMemoryConstants.PlutchikDyads.TryGetValue((b, a), out var name2))
            return name2;
        return "complex";
    }

    public static EmotionVector DyadVector(EmotionType a, EmotionType b)
    {
        var evA = EmotionVector.Dominate(a, 0.6f);
        var evB = EmotionVector.Dominate(b, 0.6f);
        return EmotionVector.Blend(evA, evB, 0.5f);
    }
}

public sealed class EmotionalMemoryStore
{
    private static readonly Lazy<EmotionalMemoryStore> _instance = new(() => new EmotionalMemoryStore());
    public static EmotionalMemoryStore Instance => _instance.Value;

    private readonly Dictionary<string, EmotionalMemory> _memories = [];
    private int _nextId = 1;
    private readonly object _lock = new();

    private const int MaxMemories = 2000;

    public string Store(string content, EmotionVector? emotion = null)
    {
        var detected = emotion ?? EmotionalMemoryUtility.DetectEmotion(content);
        var now = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
        var memory = new EmotionalMemory(
            MemoryId: string.Empty,
            Content: content,
            EmotionVector: detected,
            EmotionalIntensity: detected.Intensity,
            CreatedAt: now,
            LastRecalled: now,
            RecallCount: 0
        );

        lock (_lock)
        {
            if (_memories.Count >= MaxMemories)
            {
                var lowest = _memories.Values.MinBy(m => m.EmotionalIntensity);
                if (lowest is not null)
                    _memories.Remove(lowest.MemoryId);
            }

            var id = $"{_nextId++}";
            _memories[id] = memory with { MemoryId = id };
            return id;
        }
    }

    public List<EmotionalMemory> Recall(string query, int topK = 10)
    {
        var queryEmotion = EmotionalMemoryUtility.DetectEmotion(query);

        lock (_lock)
        {
            return _memories.Values
                .Select(m =>
                {
                    var semSim = CalculateSemanticSimilarity(query, m.Content);
                    var emoSim = CosineSimilarity(queryEmotion.AsList(), m.EmotionVector.AsList());
                    var decayed = m.DecayedIntensity;
                    var score = semSim * 0.6f + (emoSim * 0.5f + decayed * 0.5f) * 0.4f;
                    return (Memory: m, Score: score);
                })
                .OrderByDescending(x => x.Score)
                .Take(topK)
                .Select(x => x.Memory.MarkRecalled())
                .ToList();
        }
    }

    public bool Reinforce(string memoryId, float emotionDelta)
    {
        lock (_lock)
        {
            if (!_memories.TryGetValue(memoryId, out var memory))
                return false;

            var newIntensity = MathF.Min(1f, MathF.Max(0f, memory.EmotionalIntensity + emotionDelta));
            var newEmotion = EmotionVector.Blend(memory.EmotionVector, EmotionVector.Zero(), 0f) with { };
            _memories[memoryId] = memory with
            {
                EmotionalIntensity = newIntensity,
                RecallCount = memory.RecallCount + 1,
                LastRecalled = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds
            };
            return true;
        }
    }

    public List<EmotionalMemory> GetFlashbulbs(int limit = 5)
    {
        lock (_lock)
        {
            return _memories.Values
                .OrderByDescending(m => m.EmotionalIntensity)
                .Take(limit)
                .ToList();
        }
    }

    public Dictionary<string, object> DecayAll()
    {
        var stats = new Dictionary<string, object>();
        lock (_lock)
        {
            var totalBefore = _memories.Count;
            var removed = new List<string>();
            var decayedBelowThreshold = 0;
            var survivedCount = 0;

            foreach (var (id, memory) in _memories)
            {
                var decayed = memory.DecayedIntensity;
                if (decayed < 0.05f)
                {
                    removed.Add(id);
                }
                else if (decayed < 0.2f)
                {
                    decayedBelowThreshold++;
                    survivedCount++;
                }
                else
                {
                    survivedCount++;
                }
            }

            foreach (var id in removed)
                _memories.Remove(id);

            stats["total_before"] = totalBefore;
            stats["removed"] = removed.Count;
            stats["decayed_below_threshold"] = decayedBelowThreshold;
            stats["survived"] = survivedCount;
        }
        return stats;
    }

    public EmotionVector EmotionalContext()
    {
        var now = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
        const double windowSeconds = 2 * 3600;

        lock (_lock)
        {
            var recent = _memories.Values
                .Where(m => (now - m.LastRecalled) <= windowSeconds)
                .ToList();

            if (recent.Count == 0)
                return EmotionVector.Zero();

            var blended = EmotionVector.Zero();
            var totalWeight = 0f;

            foreach (var m in recent)
            {
                var weight = m.DecayedIntensity;
                blended = EmotionVector.Blend(blended, m.EmotionVector, weight / (totalWeight + weight));
                totalWeight += weight;
            }

            return blended;
        }
    }

    public bool MergeEmotion(string memoryId, EmotionVector newEmotion)
    {
        lock (_lock)
        {
            if (!_memories.TryGetValue(memoryId, out var memory))
                return false;

            var blended = EmotionVector.Blend(memory.EmotionVector, newEmotion, 0.35f);

            var primary = memory.EmotionVector.DominantEmotion;
            var secondary = blended.DominantEmotion;
            var dyad = EmotionalMemoryUtility.DyadName(primary, secondary);

            var updated = memory with
            {
                EmotionVector = blended,
                RecallCount = memory.RecallCount + 1,
                LastRecalled = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds
            };

            _memories[memoryId] = updated;
            return true;
        }
    }

    public Dictionary<string, object> Stats()
    {
        lock (_lock)
        {
            var total = _memories.Count;
            var avgIntensity = total > 0 ? _memories.Values.Average(m => m.EmotionalIntensity) : 0f;
            var avgDecayed = total > 0 ? _memories.Values.Average(m => m.DecayedIntensity) : 0f;
            var avgRecall = total > 0 ? _memories.Values.Average(m => (float)m.RecallCount) : 0f;

            return new Dictionary<string, object>
            {
                ["total_memories"] = total,
                ["avg_intensity"] = avgIntensity,
                ["avg_decayed_intensity"] = avgDecayed,
                ["avg_recall_count"] = avgRecall,
                ["capacity_used_pct"] = (float)total / MaxMemories
            };
        }
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        var dot = 0f;
        var normA = 0f;
        var normB = 0f;

        for (var i = 0; i < a.Length && i < b.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom < 1e-8f ? 0f : dot / denom;
    }

    private static float TrigramJaccard(string a, string b)
    {
        if (a.Length < 3 || b.Length < 3)
            return 0f;

        var trigramsA = new HashSet<string>();
        var trigramsB = new HashSet<string>();

        for (var i = 0; i <= a.Length - 3; i++)
            trigramsA.Add(a.Substring(i, 3));

        for (var i = 0; i <= b.Length - 3; i++)
            trigramsB.Add(b.Substring(i, 3));

        var intersection = trigramsA.Count(trigramsB.Contains);
        var union = trigramsA.Count + trigramsB.Count - intersection;

        return union > 0 ? (float)intersection / union : 0f;
    }

    private static float CharacterOverlap(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            return 0f;

        var setA = new HashSet<char>(a);
        var setB = new HashSet<char>(b);
        var intersection = setA.Count(setB.Contains);
        var union = setA.Count + setB.Count - intersection;

        return union > 0 ? (float)intersection / union : 0f;
    }

    private static float CalculateSemanticSimilarity(string a, string b)
    {
        var trigram = TrigramJaccard(a, b);
        var overlap = CharacterOverlap(a, b);
        return trigram * 0.7f + overlap * 0.3f;
    }
}
