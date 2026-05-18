using System.Text.Json.Serialization;

namespace LTAI.Memory.Models;

public enum EmotionType
{
    JOY,
    SURPRISE,
    SADNESS,
    FEAR,
    ANGER,
    DISGUST,
    TRUST,
    ANTICIPATION
}

public enum PersonaDomain
{
    CORE_IDENTITY,
    BIOGRAPHY,
    EXPERIENCES,
    PREFERENCES,
    SOCIAL,
    WORK,
    PSYCHOMETRICS,
    PROCEDURAL
}

public record EmotionVector(
    float Joy = 0f,
    float Trust = 0f,
    float Fear = 0f,
    float Surprise = 0f,
    float Sadness = 0f,
    float Disgust = 0f,
    float Anger = 0f,
    float Anticipation = 0f)
{
    public float[] AsList() => [Joy, Trust, Fear, Surprise, Sadness, Disgust, Anger, Anticipation];

    public Dictionary<string, float> AsDict() => new()
    {
        ["Joy"] = Joy,
        ["Trust"] = Trust,
        ["Fear"] = Fear,
        ["Surprise"] = Surprise,
        ["Sadness"] = Sadness,
        ["Disgust"] = Disgust,
        ["Anger"] = Anger,
        ["Anticipation"] = Anticipation
    };

    public static EmotionVector FromList(float[] values)
    {
        if (values.Length < 8)
            throw new ArgumentException("Expected 8 values", nameof(values));
        return new EmotionVector(values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7]);
    }

    public static EmotionVector FromDict(Dictionary<string, float> dict)
    {
        float Get(string k) => dict.TryGetValue(k, out var v) ? v : 0f;
        return new EmotionVector(Get("Joy"), Get("Trust"), Get("Fear"), Get("Surprise"), Get("Sadness"), Get("Disgust"), Get("Anger"), Get("Anticipation"));
    }

    public static EmotionVector Zero() => new();

    [JsonIgnore]
    public EmotionType DominantEmotion
    {
        get
        {
            var list = AsList();
            var maxIdx = 0;
            for (var i = 1; i < list.Length; i++)
                if (list[i] > list[maxIdx])
                    maxIdx = i;
            return maxIdx switch
            {
                0 => EmotionType.JOY,
                1 => EmotionType.TRUST,
                2 => EmotionType.FEAR,
                3 => EmotionType.SURPRISE,
                4 => EmotionType.SADNESS,
                5 => EmotionType.DISGUST,
                6 => EmotionType.ANGER,
                7 => EmotionType.ANTICIPATION,
                _ => EmotionType.JOY
            };
        }
    }

    [JsonIgnore]
    public float Intensity
    {
        get
        {
            var sum = Joy * Joy + Trust * Trust + Fear * Fear + Surprise * Surprise
                      + Sadness * Sadness + Disgust * Disgust + Anger * Anger + Anticipation * Anticipation;
            return MathF.Min(1f, MathF.Sqrt(sum));
        }
    }

    [JsonIgnore]
    public float Valence
    {
        get
        {
            var pos = Joy + Trust + Anticipation;
            var neg = Sadness + Fear + Anger + Disgust;
            return (pos - neg) / (pos + neg + 1e-8f);
        }
    }

    public static EmotionVector Dominate(EmotionType emotion, float value)
    {
        var v = Zero();
        return emotion switch
        {
            EmotionType.JOY => v with { Joy = value },
            EmotionType.TRUST => v with { Trust = value },
            EmotionType.FEAR => v with { Fear = value },
            EmotionType.SURPRISE => v with { Surprise = value },
            EmotionType.SADNESS => v with { Sadness = value },
            EmotionType.DISGUST => v with { Disgust = value },
            EmotionType.ANGER => v with { Anger = value },
            EmotionType.ANTICIPATION => v with { Anticipation = value },
            _ => v
        };
    }

    public static EmotionVector Blend(EmotionVector a, EmotionVector b, float weightB)
    {
        var wa = 1f - weightB;
        return new EmotionVector(
            a.Joy * wa + b.Joy * weightB,
            a.Trust * wa + b.Trust * weightB,
            a.Fear * wa + b.Fear * weightB,
            a.Surprise * wa + b.Surprise * weightB,
            a.Sadness * wa + b.Sadness * weightB,
            a.Disgust * wa + b.Disgust * weightB,
            a.Anger * wa + b.Anger * weightB,
            a.Anticipation * wa + b.Anticipation * weightB
        );
    }
}

public record EmotionalMemory(
    string MemoryId,
    string Content,
    EmotionVector EmotionVector,
    float EmotionalIntensity,
    double CreatedAt,
    double LastRecalled,
    int RecallCount,
    float DecayLambda = 0.05f)
{
    [JsonIgnore]
    public float AgeHours
    {
        get
        {
            var nowSeconds = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
            return (float)((nowSeconds - CreatedAt) / 3600.0);
        }
    }

    [JsonIgnore]
    public float HoursSinceRecall
    {
        get
        {
            var nowSeconds = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
            return (float)((nowSeconds - LastRecalled) / 3600.0);
        }
    }

    [JsonIgnore]
    public float DecayedIntensity
    {
        get
        {
            var lambda = DecayLambda;
            if (EmotionalIntensity > 0.7f) lambda *= 0.3f;
            else if (EmotionalIntensity > 0.4f) lambda *= 0.6f;
            return EmotionalIntensity * MathF.Exp(-lambda * HoursSinceRecall);
        }
    }

    public EmotionalMemory MarkRecalled() => this with
    {
        LastRecalled = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds,
        RecallCount = RecallCount + 1
    };

    public Dictionary<string, object> ToDict() => new()
    {
        ["memory_id"] = MemoryId,
        ["content"] = Content,
        ["emotion_vector"] = EmotionVector.AsList(),
        ["emotional_intensity"] = EmotionalIntensity,
        ["created_at"] = CreatedAt,
        ["last_recalled"] = LastRecalled,
        ["recall_count"] = RecallCount,
        ["decay_lambda"] = DecayLambda
    };

    public static EmotionalMemory FromDict(Dictionary<string, object> dict)
    {
        EmotionVector ev = EmotionVector.Zero();
        if (dict.TryGetValue("emotion_vector", out var evRaw))
        {
            if (evRaw is float[] arr)
                ev = EmotionVector.FromList(arr);
            else if (evRaw is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Array)
                ev = EmotionVector.FromList([.. je.EnumerateArray().Select(x => x.GetSingle())]);
        }

        return new EmotionalMemory(
            MemoryId: dict.TryGetValue("memory_id", out var m) ? m.ToString() ?? "" : "",
            Content: dict.TryGetValue("content", out var c) ? c.ToString() ?? "" : "",
            EmotionVector: ev,
            EmotionalIntensity: dict.TryGetValue("emotional_intensity", out var ei) ? Convert.ToSingle(ei) : 0f,
            CreatedAt: dict.TryGetValue("created_at", out var ca) ? Convert.ToDouble(ca) : 0d,
            LastRecalled: dict.TryGetValue("last_recalled", out var lr) ? Convert.ToDouble(lr) : 0d,
            RecallCount: dict.TryGetValue("recall_count", out var rc) ? Convert.ToInt32(rc) : 0,
            DecayLambda: dict.TryGetValue("decay_lambda", out var dl) ? Convert.ToSingle(dl) : 0.05f
        );
    }
}

public record UserCorrection(
    string Trigger,
    string Correction,
    string Category,
    int Count,
    double LastSeen,
    string Source)
{
    public string ToRule() => $"用户偏好: {Correction} (而非{Trigger})";
}

public record UserHabit(
    string Name,
    float Value,
    float Threshold,
    string Signal);

public record UserTraitVector(
    float EngagementDepth = 0.5f,
    float TechnicalSophistication = 0.5f,
    float PatienceTolerance = 0.7f,
    float FeedbackDirectness = 0.5f,
    float TopicBreadth = 0.4f,
    float InteractionRegularity = 0.5f,
    float DelegationComfort = 0.5f,
    int Generation = 0)
{
    public Dictionary<string, float> ToDict() => new()
    {
        ["EngagementDepth"] = EngagementDepth,
        ["TechnicalSophistication"] = TechnicalSophistication,
        ["PatienceTolerance"] = PatienceTolerance,
        ["FeedbackDirectness"] = FeedbackDirectness,
        ["TopicBreadth"] = TopicBreadth,
        ["InteractionRegularity"] = InteractionRegularity,
        ["DelegationComfort"] = DelegationComfort
    };

    public float[] TraitList() => [EngagementDepth, TechnicalSophistication, PatienceTolerance, FeedbackDirectness, TopicBreadth, InteractionRegularity, DelegationComfort];

    public List<string> DominantTraits(float threshold = 0.65f)
    {
        var results = new List<string>();
        var dict = ToDict();
        foreach (var (key, val) in dict)
            if (val >= threshold)
                results.Add(key);
        return results;
    }

    public List<string> LowTraits(float threshold = 0.35f)
    {
        var results = new List<string>();
        var dict = ToDict();
        foreach (var (key, val) in dict)
            if (val <= threshold)
                results.Add(key);
        return results;
    }
}

public record UserBeliefState(
    List<string> KnownTopics,
    List<string> UnknownTopics,
    List<string> StatedGoals,
    List<string> ImpliedWants,
    float FrustrationLevel,
    float SatisfactionLevel,
    string AttentionSpan)
{
    public float GapRatio()
    {
        var total = KnownTopics.Count + UnknownTopics.Count;
        if (total == 0) return 0f;
        return (float)UnknownTopics.Count / total;
    }
}

public record KnowledgeGap(
    string Topic,
    string Evidence,
    float Severity,
    double Timestamp);

public record ExpectationModel(
    string NextActionExpected,
    string ExpectedResponseType,
    string ExpectedDetailLevel,
    float DeadlinePressure,
    string ImplicitQuestion);

public record EmpathySignal(
    string PrimaryEmotion,
    string SecondaryEmotion,
    float ConfidenceLevel,
    float CognitiveLoad,
    float TimePressure,
    string SocialTone,
    string InferredNeed);

public record UserProfile(
    List<UserCorrection> Corrections,
    List<UserHabit> Habits,
    Dictionary<string, int> DomainAffinity,
    string PreferredModel,
    int VerbosityAvg,
    int PeakHour,
    float NegationRatio,
    string ProjectContext,
    double LastUpdated,
    UserTraitVector Traits,
    Dictionary<string, float> HabitSignals,
    UserBeliefState BeliefState,
    List<KnowledgeGap> KnowledgeGaps,
    ExpectationModel Expectation,
    EmpathySignal EmpathySignal);

public record PersonaFact(
    string Id,
    PersonaDomain Domain,
    string Fact,
    float Confidence,
    string SourceConversation,
    string FirstSeen,
    string LastConfirmed,
    int ConfirmationCount,
    List<string> ContradictedBy)
{
    [JsonIgnore]
    public bool IsStable => ConfirmationCount >= 2 && ContradictedBy.Count == 0;
}

public record PersonaProfile(
    string UserId,
    Dictionary<string, Dictionary<string, PersonaFact>> Facts,
    int TotalFacts,
    int StableFacts,
    string LastUpdated)
{
    public List<PersonaFact> ByDomain(PersonaDomain domain)
    {
        var domainKey = domain.ToString();
        if (!Facts.TryGetValue(domainKey, out var domainFacts))
            return [];

        return [.. domainFacts.Values];
    }

    public PersonaFact? GetFact(string factId)
    {
        foreach (var domainFacts in Facts.Values)
            if (domainFacts.TryGetValue(factId, out var fact))
                return fact;
        return null;
    }
}

public record MemoryItem(
    string Content,
    float Importance,
    int AccessCount,
    double LastAccessed,
    double CreatedAt,
    List<Dictionary<string, object>> CreditHistory,
    Dictionary<string, object> Metadata)
{
    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public float RecencyScore(double? now = null)
    {
        var nowTs = now ?? (DateTime.UtcNow - UnixEpoch).TotalSeconds;
        var ageHours = (float)(nowTs - CreatedAt) / 3600f;
        if (ageHours <= 0f) return 1f;
        return MathF.Exp(-ageHours / 24f);
    }

    public float RetentionScore(double? now = null)
    {
        var baseScore = Importance * RecencyScore(now);
        var accessBonus = AccessCount > 0 ? 1f + MathF.Log(AccessCount) * 0.3f : 1f;
        var score = baseScore * accessBonus;
        var cap = Importance * 2f;
        return MathF.Min(score, cap);
    }

    public bool IsCold(double? now = null)
    {
        var nowTs = now ?? (DateTime.UtcNow - UnixEpoch).TotalSeconds;
        return (nowTs - LastAccessed) > 7 * 24 * 3600;
    }

    public MemoryItem MarkAccessed() => this with
    {
        AccessCount = AccessCount + 1,
        LastAccessed = (DateTime.UtcNow - UnixEpoch).TotalSeconds
    };

    public MemoryItem AddCredit(float score, string taskId) => this with
    {
        CreditHistory = [.. CreditHistory, new Dictionary<string, object>
        {
            ["task_id"] = taskId,
            ["score"] = score,
            ["timestamp"] = (DateTime.UtcNow - UnixEpoch).TotalSeconds
        }]
    };

    public double AgeSeconds()
    {
        return (DateTime.UtcNow - UnixEpoch).TotalSeconds - CreatedAt;
    }
}

public record CreditEvent(
    string TaskId,
    float TaskSuccess,
    List<string> ContributedMemories,
    double Timestamp);

public record UserTraitSnapshot(
    string UserId,
    int Generation,
    Dictionary<string, float> Traits,
    double Timestamp,
    Dictionary<string, object> ConversationStats)
{
    public float[] TraitVector()
    {
        var sorted = new SortedDictionary<string, float>(Traits);
        return [.. sorted.Values];
    }
}

public record TraitGrowthReport(
    int FirstGen,
    int LastGen,
    int Span,
    Dictionary<string, float> TraitDeltas,
    Dictionary<string, string> TraitTrends,
    List<string> EmergentTraits,
    List<string> StableTraits);

public record OptimizationStats(
    int TotalMemories,
    int Retained,
    int Compressed,
    int Forgotten,
    float RetentionRate,
    float AvgImportance,
    float AvgRetentionScore,
    double ProcessingTimeMs);
