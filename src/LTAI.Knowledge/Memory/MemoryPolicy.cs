using LTAI.Knowledge.Memory.Models;

namespace LTAI.Knowledge.Memory;

public static class SchemaValidator
{
    public static readonly Dictionary<string, List<(string name, string type)>> SCHEMAS = new()
    {
        ["fact"] =
        [
            ("subject", "string"),
            ("predicate", "string"),
            ("object", "string"),
            ("confidence", "float"),
            ("domain", "string")
        ],
        ["event"] =
        [
            ("subject", "string"),
            ("action", "string"),
            ("timestamp", "string"),
            ("location", "string"),
            ("outcome", "string"),
            ("participants", "string")
        ],
        ["preference"] =
        [
            ("subject", "string"),
            ("likes_dislikes", "string"),
            ("item", "string"),
            ("strength", "float"),
            ("condition", "string")
        ],
        ["procedure"] =
        [
            ("action", "string"),
            ("steps", "string"),
            ("prerequisites", "string"),
            ("expected_result", "string"),
            ("context", "string")
        ]
    };

    public static (bool valid, string reason) Validate(string content, string schemaType = "fact")
    {
        if (!SCHEMAS.TryGetValue(schemaType, out var fields))
            return (false, $"Unknown schema type: {schemaType}");

        var matched = 0;
        var contentLower = content.ToLowerInvariant();
        foreach (var (name, _) in fields)
        {
            if (contentLower.Contains(name.ToLowerInvariant(), StringComparison.Ordinal))
                matched++;
        }

        var coverage = (float)matched / fields.Count;
        var valid = coverage >= 0.6f;
        var reason = $"Coverage: {coverage:P0}, matched {matched}/{fields.Count} fields";
        return (valid, reason);
    }

    public static string ClassifySchema(string content)
    {
        string best = "fact";
        var bestCoverage = 0f;

        foreach (var (schemaType, fields) in SCHEMAS)
        {
            var matched = 0;
            var contentLower = content.ToLowerInvariant();
            foreach (var (name, _) in fields)
            {
                if (contentLower.Contains(name.ToLowerInvariant(), StringComparison.Ordinal))
                    matched++;
            }

            var coverage = (float)matched / fields.Count;
            if (coverage > bestCoverage)
            {
                bestCoverage = coverage;
                best = schemaType;
            }
        }

        return best;
    }
}

public class CreditAssigner
{
    private readonly Dictionary<string, HashSet<string>> _taskMemories = [];
    private float _alpha;
    private float _totalCredits;
    private int _totalAssignments;
    private readonly List<CreditEvent> _events = [];
    private readonly HashSet<string> _forced = [];
    private readonly bool _decayAlpha;

    public CreditAssigner(float alpha = 0.15f, bool decayAlpha = true)
    {
        _alpha = alpha;
        _decayAlpha = decayAlpha;
    }

    public void SetForceRetrieve(string memoryId, bool enable = true)
    {
        if (enable)
            _forced.Add(memoryId);
        else
            _forced.Remove(memoryId);
    }

    public void ClearForceRetrieve()
    {
        _forced.Clear();
    }

    public void LogAccess(string memoryId, string taskId = "default")
    {
        if (!_taskMemories.TryGetValue(taskId, out var set))
        {
            set = [];
            _taskMemories[taskId] = set;
        }
        set.Add(memoryId);
    }

    public float AssignCredit(string taskId, float success, string taskOutput = "", Dictionary<string, MemoryItem>? memoryStore = null)
    {
        if (!_taskMemories.TryGetValue(taskId, out var memSet) || memSet.Count == 0)
            return 0f;

        var totalCredit = 0f;
        var contributed = new List<string>();

        foreach (var memId in memSet)
        {
            var contribution = 0.3f;
            var content = "";

            if (memoryStore != null && memoryStore.TryGetValue(memId, out var item))
            {
                content = item.Content;
                contribution = ComputeContribution(content, taskOutput);
            }

            if (IsForced(memId))
                contribution = MathF.Min(1f, contribution + 0.15f);

            var credit = _alpha * contribution * success;
            totalCredit += credit;
            contributed.Add(memId);

            if (memoryStore != null && memoryStore.TryGetValue(memId, out var existing))
                memoryStore[memId] = existing with
                {
                    Importance = Math.Clamp(existing.Importance + credit, 0.01f, 10f),
                    CreditHistory =
                    [
                        .. existing.CreditHistory,
                        new Dictionary<string, object>
                        {
                            ["task_id"] = taskId,
                            ["credit"] = credit,
                            ["contribution"] = contribution,
                            ["timestamp"] = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds
                        }
                    ]
                };
        }

        _totalCredits += totalCredit;
        _totalAssignments++;

        _events.Add(new CreditEvent(
            TaskId: taskId,
            TaskSuccess: success,
            ContributedMemories: contributed,
            Timestamp: (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds
        ));

        if (_decayAlpha)
            _alpha *= 0.999f;

        _taskMemories.Remove(taskId);

        return totalCredit;
    }

    public float PenalizeTaskFailure(string taskId, Dictionary<string, MemoryItem>? memoryStore = null)
    {
        if (!_taskMemories.TryGetValue(taskId, out var memSet) || memSet.Count == 0)
            return 0f;

        var totalPenalty = 0f;

        foreach (var memId in memSet)
        {
            var penalty = _alpha * -0.5f;
            totalPenalty += penalty;

            if (memoryStore != null && memoryStore.TryGetValue(memId, out var existing))
                memoryStore[memId] = existing with
                {
                    Importance = Math.Clamp(existing.Importance + penalty, 0.01f, 10f)
                };
        }

        _totalAssignments++;
        _taskMemories.Remove(taskId);

        return totalPenalty;
    }

    public List<(string id, float importance)> GetTopContributingMemories(int n = 10, Dictionary<string, MemoryItem>? store = null)
    {
        if (store != null)
            return store
                .OrderByDescending(kv => kv.Value.Importance)
                .Take(n)
                .Select(kv => (kv.Key, kv.Value.Importance))
                .ToList();

        var memoryScores = new Dictionary<string, float>();
        foreach (var ev in _events)
        {
            var perMem = ev.TaskSuccess / MathF.Max(1f, ev.ContributedMemories.Count);
            foreach (var memId in ev.ContributedMemories)
            {
                if (!memoryScores.TryGetValue(memId, out var _))
                    memoryScores[memId] = 0f;
                memoryScores[memId] += perMem;
            }
        }

        return memoryScores
            .OrderByDescending(kv => kv.Value)
            .Take(n)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }

    public Dictionary<string, object> GetStats()
    {
        return new Dictionary<string, object>
        {
            ["total_credits_assigned"] = _totalCredits,
            ["total_assignments"] = _totalAssignments,
            ["current_alpha"] = _alpha,
            ["tracked_tasks"] = _events.Count,
            ["forced_memories"] = _forced.Count,
            ["active_task_memories"] = _taskMemories.Count
        };
    }

    private static float ComputeContribution(string memoryContent, string taskOutput)
    {
        return TrigramJaccard(memoryContent, taskOutput);
    }

    private bool IsForced(string memoryId) => _forced.Contains(memoryId);

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

        var intersection = trigramsA.Count(t => trigramsB.Contains(t));
        var union = trigramsA.Count + trigramsB.Count - intersection;

        return union > 0 ? (float)intersection / union : 0f;
    }
}

public class RetentionPolicy
{
    private readonly float _keepThreshold;
    private readonly float _compressThreshold;
    private readonly int _maxMemories;

    public RetentionPolicy(float keepThreshold = 0.5f, float compressThreshold = 0.15f, int maxMemories = 1000)
    {
        _keepThreshold = keepThreshold;
        _compressThreshold = compressThreshold;
        _maxMemories = maxMemories;
    }

    public string Decide(MemoryItem memory, double? now = null)
    {
        var score = memory.RetentionScore(now);
        if (score >= _keepThreshold)
            return "RETAIN";
        if (score >= _compressThreshold)
            return "COMPRESS";
        return "FORGET";
    }

    public Dictionary<string, float> DifferentialDecay(Dictionary<string, MemoryItem> memories)
    {
        var multipliers = new Dictionary<string, float>();
        var now = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;

        foreach (var (id, memory) in memories)
        {
            var ageDays = (float)((now - memory.CreatedAt) / (24 * 3600));
            var halfLifeDays = 2f;

            if (memory.AccessCount >= 10)
                halfLifeDays = 30f;
            else if (memory.Importance >= 2f)
                halfLifeDays = 14f;
            else if (memory.AccessCount >= 3)
                halfLifeDays = 7f;
            else if (memory.IsCold(now))
                halfLifeDays = 0.5f;

            var decay = MathF.Pow(0.5f, ageDays / halfLifeDays);
            multipliers[id] = Math.Clamp(decay, 0.01f, 1f);
        }

        return multipliers;
    }

    public (List<string> retained, List<string> compressed, List<string> forgotten) ApplyPolicy(
        Dictionary<string, MemoryItem> store, double? now = null)
    {
        var retained = new List<string>();
        var compressed = new List<string>();
        var forgotten = new List<string>();

        foreach (var (id, memory) in store)
        {
            var decision = Decide(memory, now);
            switch (decision)
            {
                case "RETAIN":
                    retained.Add(id);
                    break;
                case "COMPRESS":
                    compressed.Add(id);
                    break;
                case "FORGET":
                    forgotten.Add(id);
                    break;
            }
        }

        return (retained, compressed, forgotten);
    }

    public Dictionary<string, object> GetPolicyStats(Dictionary<string, MemoryItem> store)
    {
        var (retained, compressed, forgotten) = ApplyPolicy(store);
        var total = store.Count;
        var retentionRate = total > 0 ? (float)retained.Count / total : 0f;

        return new Dictionary<string, object>
        {
            ["total"] = total,
            ["retained"] = retained.Count,
            ["compressed"] = compressed.Count,
            ["forgotten"] = forgotten.Count,
            ["retention_rate"] = retentionRate
        };
    }

    public Dictionary<string, object> DecayStats(Dictionary<string, MemoryItem> memories)
    {
        var decay = DifferentialDecay(memories);
        var total = memories.Count;
        var totalMultiplier = 0f;
        var fastDecay = 0;
        var slowDecay = 0;

        foreach (var (_, mult) in decay)
        {
            totalMultiplier += mult;
            if (mult < 0.3f)
                fastDecay++;
            if (mult > 0.7f)
                slowDecay++;
        }

        var avgMultiplier = total > 0 ? totalMultiplier / total : 0f;

        return new Dictionary<string, object>
        {
            ["total"] = total,
            ["fast_decay"] = fastDecay,
            ["slow_decay"] = slowDecay,
            ["avg_multiplier"] = avgMultiplier
        };
    }
}

public class RetrievalUrgency
{
    public const float HIGH_THRESHOLD = 0.6f;
    public const float MEDIUM_THRESHOLD = 0.4f;

    public float Compute(
        int contextSwitches = 0,
        float queryComplexity = 0.5f,
        float topicNovelty = 0.5f,
        string userEmotion = "neutral",
        int sessionDepth = 0)
    {
        var switchScore = MathF.Min(0.30f, contextSwitches * 0.15f);
        var complexityScore = queryComplexity * 0.25f;
        var noveltyScore = topicNovelty * 0.25f;
        var emotionScore = string.Equals(userEmotion, "upset", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(userEmotion, "negative", StringComparison.OrdinalIgnoreCase)
            ? 0.10f
            : 0.05f;
        var depthScore = MathF.Min(0.15f, sessionDepth * 0.01f);

        return switchScore + complexityScore + noveltyScore + emotionScore + depthScore;
    }
}

public class TokenBudget
{
    private readonly int _baseTokens;
    private readonly int _maxTokens;
    private readonly int _complexityBonus;
    private readonly bool _useTypeWeights;

    public TokenBudget(int baseTokens = 2048, int maxTokens = 8192, int complexityBonus = 4096, bool useTypeWeights = true)
    {
        _baseTokens = baseTokens;
        _maxTokens = maxTokens;
        _complexityBonus = complexityBonus;
        _useTypeWeights = useTypeWeights;
    }

    public (int budget, int perMemory) Allocate(float taskComplexity = 0.5f, int numMemories = 10, List<string>? memoryTypes = null)
    {
        var budget = _baseTokens + (int)(_complexityBonus * taskComplexity);
        budget = Math.Clamp(budget, _baseTokens, _maxTokens);

        var divisor = Math.Max(1, numMemories);
        var perMemory = budget / divisor;

        if (_useTypeWeights && memoryTypes != null && memoryTypes.Count == divisor)
        {
            var typeWeights = new Dictionary<string, float>
            {
                ["fact"] = 0.8f,
                ["event"] = 1.2f,
                ["preference"] = 0.7f,
                ["procedure"] = 1.0f
            };

            var totalWeight = 0f;
            foreach (var t in memoryTypes)
                totalWeight += typeWeights.TryGetValue(t, out var w) ? w : 1.0f;

            if (totalWeight > 0f)
                perMemory = (int)(budget / totalWeight * (typeWeights.TryGetValue(memoryTypes[0], out var _) ? 1f : 1f));
        }

        return (budget, Math.Max(1, perMemory));
    }

    public float EstimateComplexity(string query)
    {
        if (string.IsNullOrEmpty(query))
            return 0.1f;

        var length = query.Length;
        var lengthScore = MathF.Min(1f, length / 500f) * 0.4f;

        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var entityCount = words.Count(w =>
            char.IsUpper(w.Length > 0 ? w[0] : '\0') ||
            double.TryParse(w, out _));

        var entityScore = MathF.Min(1f, entityCount / 10f) * 0.1f;

        var complexKeywords = new[] { "how", "why", "explain", "compare", "difference", "analyze", "evaluate", "synthesize", "contrast", "summarize" };
        var keywordHits = complexKeywords.Count(kw => query.Contains(kw, StringComparison.OrdinalIgnoreCase));
        var keywordScore = MathF.Min(1f, keywordHits / 5f) * 0.5f;

        return Math.Clamp(lengthScore + entityScore + keywordScore, 0.1f, 1f);
    }

    public (string context, int tokens) FormatContextBudget(List<MemoryItem> memories, float taskComplexity = 0.5f)
    {
        if (memories.Count == 0)
            return ("", 0);

        var (budget, perMemory) = Allocate(taskComplexity, memories.Count);
        var charBudget = perMemory * 3;

        var parts = new List<string>();
        var totalChars = 0;
        var estimatedTokens = 0;

        for (var i = 0; i < memories.Count; i++)
        {
            var mem = memories[i];
            var entry = $"[{i + 1}] {mem.Content}";
            parts.Add(entry);
            totalChars += entry.Length;
            estimatedTokens += entry.Length / 3;

            if (totalChars >= charBudget * memories.Count)
                break;
        }

        return (string.Join("\n", parts), estimatedTokens);
    }
}

public class MemPOOptimizer
{
    private static MemPOOptimizer? _instance;
    private static readonly object _instanceLock = new();

    private readonly Dictionary<string, MemoryItem> _store = [];
    private readonly CreditAssigner _creditAssigner;
    private readonly RetentionPolicy _retentionPolicy;
    private readonly TokenBudget _tokenBudget;

    public MemPOOptimizer(float keepThreshold = 0.5f, float compressThreshold = 0.15f, int maxMemories = 1000, float alpha = 0.15f)
    {
        _creditAssigner = new CreditAssigner(alpha);
        _retentionPolicy = new RetentionPolicy(keepThreshold, compressThreshold, maxMemories);
        _tokenBudget = new TokenBudget();
    }

    public static MemPOOptimizer GetMempoOptimizer(
        float keepThreshold = 0.5f,
        float compressThreshold = 0.15f,
        int maxMemories = 1000,
        float alpha = 0.15f)
    {
        if (_instance == null)
        {
            lock (_instanceLock)
            {
                _instance ??= new MemPOOptimizer(keepThreshold, compressThreshold, maxMemories, alpha);
            }
        }
        return _instance;
    }

    public string AddMemory(string content, Dictionary<string, object>? metadata = null)
    {
        var schemaType = SchemaValidator.ClassifySchema(content);
        var (valid, reason) = SchemaValidator.Validate(content, schemaType);
        var importance = valid ? 0.5f : 0.1f;
        var now = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;

        var mergedMetadata = new Dictionary<string, object>
        {
            ["schema_valid"] = valid,
            ["schema_type"] = schemaType,
            ["validation_reason"] = reason
        };

        if (metadata != null)
        {
            foreach (var (k, v) in metadata)
                mergedMetadata[k] = v;
        }

        var memory = new MemoryItem(
            Content: content,
            Importance: importance,
            AccessCount: 0,
            LastAccessed: now,
            CreatedAt: now,
            CreditHistory: [],
            Metadata: mergedMetadata
        );

        var id = Guid.NewGuid().ToString("N")[..12];
        _store[id] = memory;
        return id;
    }

    public MemoryItem? GetMemory(string memId)
    {
        return _store.TryGetValue(memId, out var item) ? item : null;
    }

    public void LogAccess(string memId, string taskId = "default")
    {
        if (_store.TryGetValue(memId, out var memory))
        {
            _store[memId] = memory.MarkAccessed();
            _creditAssigner.LogAccess(memId, taskId);
        }
    }

    public float OnTaskComplete(string taskId = "default", float success = 1f, string taskOutput = "")
    {
        return _creditAssigner.AssignCredit(taskId, success, taskOutput, _store);
    }

    public float OnTaskFail(string taskId = "default")
    {
        return _creditAssigner.PenalizeTaskFailure(taskId, _store);
    }

    public OptimizationStats Optimize(bool forceForgetOverflow = true)
    {
        var startTime = DateTime.UtcNow;
        var (retained, compressed, forgotten) = _retentionPolicy.ApplyPolicy(_store);

        foreach (var id in compressed)
        {
            if (_store.TryGetValue(id, out var mem))
            {
                var updated = mem with { Metadata = new Dictionary<string, object>(mem.Metadata) { ["compressed"] = true, ["importance_original"] = mem.Importance } };
                _store[id] = updated;
            }
        }

        foreach (var id in forgotten)
            _store.Remove(id);

        if (forceForgetOverflow && _store.Count > 1000)
        {
            var toRemove = _store
                .OrderBy(kv => kv.Value.RetentionScore())
                .Take(_store.Count - 1000)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var id in toRemove)
            {
                _store.Remove(id);
                if (!forgotten.Contains(id))
                    forgotten.Add(id);
            }

            retained = _store.Keys.Where(k => !forgotten.Contains(k)).ToList();
        }

        var total = _store.Count + forgotten.Count;
        var avgImportance = _store.Count > 0 ? _store.Values.Average(m => m.Importance) : 0f;
        var avgRetentionScore = _store.Count > 0 ? _store.Values.Average(m => m.RetentionScore()) : 0f;
        var retentionRate = total > 0 ? (float)_store.Count / total : 0f;
        var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;

        return new OptimizationStats(
            TotalMemories: total,
            Retained: _store.Count,
            Compressed: compressed.Count,
            Forgotten: forgotten.Count,
            RetentionRate: retentionRate,
            AvgImportance: avgImportance,
            AvgRetentionScore: avgRetentionScore,
            ProcessingTimeMs: elapsed
        );
    }

    public (string context, int tokens) BuildContext(string query = "", float taskComplexity = 0.5f, int maxMemories = 20)
    {
        var top = _store
            .OrderByDescending(kv => kv.Value.RetentionScore())
            .Take(maxMemories)
            .Select(kv => kv.Value)
            .ToList();

        return _tokenBudget.FormatContextBudget(top, taskComplexity);
    }

    public List<(string id, MemoryItem memory)> GetTopMemories(int n = 10)
    {
        return _store
            .OrderByDescending(kv => kv.Value.RetentionScore())
            .Take(n)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }

    public Dictionary<string, object> GetStats()
    {
        var creditStats = _creditAssigner.GetStats();
        var policyStats = _retentionPolicy.GetPolicyStats(_store);
        var decayStats = _retentionPolicy.DecayStats(_store);

        return new Dictionary<string, object>
        {
            ["total_memories"] = _store.Count,
            ["credit_assigner"] = creditStats,
            ["retention_policy"] = policyStats,
            ["decay_stats"] = decayStats
        };
    }
}
