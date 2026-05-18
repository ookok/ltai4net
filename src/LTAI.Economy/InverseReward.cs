using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using LTAI.Economy.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.Economy;

public sealed class InverseRewardModel
{
    private const int MaxHistory = 200;
    private const double MinConfidenceDelta = 0.01;
    private const double PatternCooldown = 0.05;
    private const int TopKForSuggestion = 5;
    private const double RecencyDecay = 0.90;
    private const double MinWeight = 0.0;
    private const double MaxWeight = 1.0;

    private static readonly Dictionary<string, double> SignalWeights = new()
    {
        ["accepted"] = 0.10,
        ["rejected"] = 0.15,
        ["corrected"] = 0.20,
        ["praised"] = 0.08,
        ["ignored"] = 0.03
    };

    private static readonly Dictionary<string, string> CorrectionPatterns = new()
    {
        [@"(简单|简洁|简明|简化|simpler?|simple|concise)"] = "prefers simplicity over complexity",
        [@"(详细|详尽|细节|具体|detailed?|detail|thorough)"] = "prefers detail over brevity",
        [@"(快|速度|快速|迅速|faster?|fast|quick|speed)"] = "prefers speed over depth",
        [@"(准确|精确|质量|高质量|accurate|quality|precise|正确)"] = "prefers accuracy over speed",
        [@"(代码|实现|示例|example|code|implement)"] = "prefers concrete code examples",
        [@"(解释|说明|解释一下|explain|为什么|原因)"] = "prefers explanation over direct answer",
        [@"(中文|Chinese|用中文|说中文)"] = "prefers Chinese language output",
        [@"(英文|English|用英文|说英文)"] = "prefers English language output",
        [@"(友好|温柔|礼貌|友善|gentle|polite|friendly)"] = "prefers friendly tone",
        [@"(直接|干脆|直接说|brief|straight|direct)"] = "prefers direct communication",
        [@"(安全|保守|safe|conservative|cautious)"] = "prefers safety over risk",
        [@"(大胆|冒险|尝试|bold|creative|创新)"] = "prefers creativity over safety",
        [@"(可视化|图表|graph|chart|visual|diagram)"] = "prefers visual representations",
        [@"(文本|文字|text|read|阅读)"] = "prefers text output over visuals",
        [@"(安静|少说|静默|silent|quiet|minimal)"] = "prefers minimal verbosity",
        [@"(多模态|multimodal|声音|audio|视频|video)"] = "prefers multimodal responses",
        [@"(记忆|记得|记住|remember|memory|history)"] = "prefers memory-aware responses",
        [@"(隐私|保密|private|secret|privacy)"] = "prefers privacy-conscious behavior"
    };

    private static readonly Dictionary<string, string> PreferenceInverse = new()
    {
        ["prefers simplicity over complexity"] = "prefers detail over brevity",
        ["prefers detail over brevity"] = "prefers simplicity over complexity",
        ["prefers speed over depth"] = "prefers accuracy over speed",
        ["prefers accuracy over speed"] = "prefers speed over depth",
        ["prefers concrete code examples"] = "prefers explanation over direct answer",
        ["prefers explanation over direct answer"] = "prefers concrete code examples",
        ["prefers Chinese language output"] = "prefers English language output",
        ["prefers English language output"] = "prefers Chinese language output",
        ["prefers friendly tone"] = "prefers direct communication",
        ["prefers direct communication"] = "prefers friendly tone",
        ["prefers safety over risk"] = "prefers creativity over safety",
        ["prefers creativity over safety"] = "prefers safety over risk",
        ["prefers visual representations"] = "prefers text output over visuals",
        ["prefers text output over visuals"] = "prefers visual representations"
    };

    private static readonly Dictionary<string, string[]> PreferenceKeywords = new()
    {
        ["prefers simplicity over complexity"] = new[] { "简单", "简洁", "简明", "简化", "simple", "simpler", "simplicity", "concise" },
        ["prefers detail over brevity"] = new[] { "详细", "详尽", "细节", "具体", "detailed", "detail", "thorough" },
        ["prefers speed over depth"] = new[] { "快", "速度", "快速", "迅速", "fast", "faster", "quick", "speed" },
        ["prefers accuracy over speed"] = new[] { "准确", "精确", "质量", "高质量", "accurate", "quality", "precise", "正确" },
        ["prefers concrete code examples"] = new[] { "代码", "实现", "示例", "example", "code", "implement" },
        ["prefers explanation over direct answer"] = new[] { "解释", "说明", "解释一下", "explain", "为什么", "原因" },
        ["prefers Chinese language output"] = new[] { "中文", "chinese", "用中文", "说中文" },
        ["prefers English language output"] = new[] { "英文", "english", "用英文", "说英文" },
        ["prefers friendly tone"] = new[] { "友好", "温柔", "礼貌", "友善", "gentle", "polite", "friendly" },
        ["prefers direct communication"] = new[] { "直接", "干脆", "直接说", "brief", "straight", "direct" },
        ["prefers safety over risk"] = new[] { "安全", "保守", "safe", "conservative", "cautious" },
        ["prefers creativity over safety"] = new[] { "大胆", "冒险", "尝试", "bold", "creative", "创新" },
        ["prefers visual representations"] = new[] { "可视化", "图表", "graph", "chart", "visual", "diagram" },
        ["prefers text output over visuals"] = new[] { "文本", "文字", "text", "read", "阅读" },
        ["prefers minimal verbosity"] = new[] { "安静", "少说", "静默", "silent", "quiet", "minimal" },
        ["prefers multimodal responses"] = new[] { "多模态", "multimodal", "声音", "audio", "视频", "video" },
        ["prefers memory-aware responses"] = new[] { "记忆", "记得", "记住", "remember", "memory", "history" },
        ["prefers privacy-conscious behavior"] = new[] { "隐私", "保密", "private", "secret", "privacy" }
    };

    private static readonly Lazy<InverseRewardModel> _instance = new(() =>
    {
        var model = new InverseRewardModel();
        model.Load();
        return model;
    });

    public static InverseRewardModel Instance => _instance.Value;

    private readonly ILogger<InverseRewardModel>? _logger;
    private readonly ConcurrentDictionary<string, double> _preferences = new();
    private readonly ConcurrentDictionary<string, int> _signalCounts = new();
    private readonly ConcurrentDictionary<string, List<double>> _correctionPatterns = new();
    private readonly ConcurrentDictionary<string, PreferenceSignal> _recentSignals = new();
    private readonly object _lock = new();

    public InverseRewardModel(ILogger<InverseRewardModel>? logger = null)
    {
        _logger = logger;
    }

    public PreferenceSignal Observe(string signalType, string context, string correctionText = "")
    {
        var normalizedSignal = signalType.ToLowerInvariant();
        if (!SignalWeights.TryGetValue(normalizedSignal, out var weight))
        {
            _logger?.LogWarning("InverseReward: unknown signal type {SignalType}", signalType);
            return new PreferenceSignal
            {
                SignalType = signalType,
                Context = context,
                InferredPreference = "",
                Confidence = 0,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0
            };
        }

        var matchedPreference = MatchPreferenceFromText(context);
        string inferredPreference;
        double confidence;

        switch (normalizedSignal)
        {
            case "accepted":
                inferredPreference = matchedPreference;
                confidence = 0.3;
                ApplyWeight(inferredPreference, SignalWeights["accepted"]);
                break;

            case "rejected":
                {
                    var inverse = GetInversePreference(matchedPreference) ?? matchedPreference;
                    inferredPreference = inverse;
                    confidence = 0.3;
                    ApplyWeight(inverse, SignalWeights["rejected"]);
                    break;
                }

            case "corrected":
                {
                    var extracted = ExtractPreferenceFromCorrection(correctionText);
                    inferredPreference = extracted ?? matchedPreference;
                    var patternKey = inferredPreference;
                    var timestamps = _correctionPatterns.GetOrAdd(patternKey, _ => new List<double>());
                    lock (timestamps)
                    {
                        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
                        var hourlyCount = timestamps.Count(t => now - t < 3600.0) + 1;
                        confidence = hourlyCount switch
                        {
                            1 => 0.3,
                            2 => 0.55,
                            _ => 0.75
                        };
                        timestamps.Add(now);
                        while (timestamps.Count > MaxHistory)
                            timestamps.RemoveAt(0);
                    }
                    ApplyWeight(inferredPreference, SignalWeights["corrected"]);
                    break;
                }

            case "praised":
                inferredPreference = matchedPreference;
                confidence = 0.3;
                ApplyWeight(inferredPreference, SignalWeights["praised"]);
                break;

            case "ignored":
                inferredPreference = matchedPreference;
                confidence = 0.1;
                ApplyWeight(inferredPreference, -SignalWeights["ignored"]);
                break;

            default:
                inferredPreference = matchedPreference;
                confidence = 0.2;
                break;
        }

        _signalCounts.AddOrUpdate(normalizedSignal, 1, (_, c) => c + 1);

        DecayAllWeights();

        var signal = new PreferenceSignal
        {
            SignalType = signalType,
            Context = context,
            InferredPreference = inferredPreference,
            Confidence = confidence,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0
        };

        var key = $"{signal.Timestamp}_{Guid.NewGuid():N}";
        _recentSignals.TryAdd(key, signal);
        while (_recentSignals.Count > MaxHistory)
        {
            var oldest = _recentSignals.OrderBy(kv =>
            {
                var colon = kv.Key.IndexOf('_');
                return colon >= 0 && double.TryParse(kv.Key.AsSpan(0, colon), out var ts) ? ts : double.MaxValue;
            }).FirstOrDefault();
            if (!string.IsNullOrEmpty(oldest.Key))
                _recentSignals.TryRemove(oldest.Key, out _);
            else
                break;
        }

        _logger?.LogDebug("InverseReward: observed {SignalType} → {Preference} ({Confidence:F2})",
            signalType, inferredPreference, confidence);

        return signal;
    }

    public double GetReward(string actionDescription, string context = "")
    {
        var combined = (actionDescription + " " + context).ToLowerInvariant();
        var preferences = _preferences.ToArray();

        if (preferences.Length == 0)
            return 0.5;

        double totalScore = 0;
        double totalWeight = 0;

        foreach (var (prefKey, prefWeight) in preferences)
        {
            if (prefWeight <= MinWeight)
                continue;

            if (!PreferenceKeywords.TryGetValue(prefKey, out var keywords))
                continue;

            var matchCount = keywords.Count(k => combined.Contains(k, StringComparison.OrdinalIgnoreCase));
            if (matchCount > 0)
            {
                var matchRatio = Math.Min(1.0, (double)matchCount / keywords.Length);
                totalScore += prefWeight * matchRatio;
                totalWeight += prefWeight;
            }
        }

        if (totalWeight <= 0)
            return 0.5;

        return Math.Clamp(totalScore / Math.Max(totalWeight, 0.01), 0.0, 1.0);
    }

    public List<(string Action, double Score)> RankActions(IReadOnlyList<string> actions, string context = "")
    {
        var scored = new List<(string Action, double Score)>(actions.Count);

        foreach (var action in actions)
        {
            var score = GetReward(action, context);
            scored.Add((action, score));
        }

        scored.Sort((a, b) => b.Score.CompareTo(a.Score));

        _logger?.LogDebug("InverseReward: ranked {Count} actions, top score {TopScore:F3}",
            scored.Count, scored.Count > 0 ? scored[0].Score : 0);

        return scored;
    }

    public IReadOnlyDictionary<string, double> GetPreferenceProfile()
    {
        var snapshot = new Dictionary<string, double>();
        foreach (var (key, value) in _preferences)
        {
            if (value > MinConfidenceDelta)
                snapshot[key] = Math.Round(value, 4);
        }
        return snapshot;
    }

    public IReadOnlyDictionary<string, object> Stats()
    {
        var totalSignals = _signalCounts.Values.Sum();
        var topPreferences = _preferences
            .Where(kv => kv.Value > MinConfidenceDelta)
            .OrderByDescending(kv => kv.Value)
            .Take(TopKForSuggestion)
            .Select(kv => new Dictionary<string, object>
            {
                ["preference"] = kv.Key,
                ["weight"] = Math.Round(kv.Value, 4)
            })
            .ToList();

        var recentSignalTypes = _recentSignals.Values
            .GroupBy(s => s.SignalType)
            .ToDictionary(g => g.Key, g => g.Count());

        return new Dictionary<string, object>
        {
            ["total_signals"] = totalSignals,
            ["learned_preferences_count"] = _preferences.Count(kv => kv.Value > MinConfidenceDelta),
            ["top_preferences"] = topPreferences,
            ["recent_signal_types"] = recentSignalTypes,
            ["signal_counts"] = _signalCounts.ToDictionary(kv => kv.Key, kv => (object)kv.Value),
            ["correction_pattern_count"] = _correctionPatterns.Count
        };
    }

    public void Save(string? filePath = null)
    {
        filePath ??= GetDefaultPath();

        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var data = new PersistenceData
            {
                Preferences = _preferences.Where(kv => kv.Value > MinConfidenceDelta)
                    .ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 6)),
                SignalCounts = _signalCounts.ToDictionary(kv => kv.Key, kv => kv.Value),
                CorrectionPatterns = _correctionPatterns.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value.OrderByDescending(t => t).Take(MaxHistory).ToList()),
                SavedAt = DateTimeOffset.UtcNow.ToString("o")
            };

            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filePath, json);

            _logger?.LogInformation("InverseReward: saved to {Path}", filePath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "InverseReward: failed to save to {Path}", filePath);
        }
    }

    public void Load(string? filePath = null)
    {
        filePath ??= GetDefaultPath();

        if (!File.Exists(filePath))
        {
            _logger?.LogDebug("InverseReward: no saved state at {Path}", filePath);
            return;
        }

        try
        {
            var json = File.ReadAllText(filePath);
            var data = JsonSerializer.Deserialize<PersistenceData>(json);
            if (data == null)
                return;

            _preferences.Clear();
            foreach (var (key, value) in data.Preferences)
                _preferences.TryAdd(key, value);

            _signalCounts.Clear();
            foreach (var (key, value) in data.SignalCounts)
                _signalCounts.TryAdd(key, value);

            _correctionPatterns.Clear();
            foreach (var (key, value) in data.CorrectionPatterns)
                _correctionPatterns.TryAdd(key, new List<double>(value));

            _logger?.LogInformation("InverseReward: loaded {PrefCount} preferences from {Path}",
                data.Preferences.Count, filePath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "InverseReward: failed to load from {Path}", filePath);
        }
    }

    private static string GetDefaultPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".livingtree",
            "inverse_reward_prefs.json");
    }

    private static string MatchPreferenceFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var lower = text.ToLowerInvariant();

        foreach (var (prefKey, keywords) in PreferenceKeywords)
        {
            foreach (var kw in keywords)
            {
                if (lower.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    return prefKey;
            }
        }

        return "";
    }

    private static string? ExtractPreferenceFromCorrection(string correctionText)
    {
        if (string.IsNullOrWhiteSpace(correctionText))
            return null;

        foreach (var (pattern, preference) in CorrectionPatterns)
        {
            if (Regex.IsMatch(correctionText, pattern, RegexOptions.IgnoreCase))
                return preference;
        }

        return null;
    }

    private static string? GetInversePreference(string preference)
    {
        return PreferenceInverse.TryGetValue(preference, out var inverse) ? inverse : null;
    }

    private void ApplyWeight(string preference, double delta)
    {
        if (string.IsNullOrEmpty(preference))
            return;

        var current = _preferences.GetOrAdd(preference, 0.5);
        var updated = Math.Clamp(current + delta, MinWeight, MaxWeight);
        _preferences.TryUpdate(preference, updated, current);
    }

    private void DecayAllWeights()
    {
        foreach (var key in _preferences.Keys.ToList())
        {
            if (_preferences.TryGetValue(key, out var value))
            {
                var decayed = value * RecencyDecay;
                if (decayed < MinConfidenceDelta)
                    _preferences.TryRemove(key, out _);
                else
                    _preferences.TryUpdate(key, decayed, value);
            }
        }
    }

    private sealed class PersistenceData
    {
        public Dictionary<string, double> Preferences { get; set; } = new();
        public Dictionary<string, int> SignalCounts { get; set; } = new();
        public Dictionary<string, List<double>> CorrectionPatterns { get; set; } = new();
        public string SavedAt { get; set; } = "";
    }
}
