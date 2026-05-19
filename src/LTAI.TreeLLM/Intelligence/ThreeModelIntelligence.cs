using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using LTAI.TreeLLM.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.TreeLLM.Intelligence;

public sealed partial class ThreeModelIntelligence
{
    private static readonly Lazy<ThreeModelIntelligence> _instanceLazy = new(() => new ThreeModelIntelligence());
    public static ThreeModelIntelligence Instance => _instanceLazy.Value;

    private readonly ConcurrentDictionary<string, ReflexRule> _reflexes = new();
    private readonly object _lock = new();
    private readonly List<(string Query, List<string> Needs)> _trajectory = new();
    private readonly List<object> _dreamQueue = new();
    private readonly ILogger<ThreeModelIntelligence>? _logger;

    private static readonly Dictionary<string, (double Valence, double Arousal, double Dominance)> VadLexiconCn = new()
    {
        ["愤怒"] = (0.12, 0.85, 0.55),
        ["快乐"] = (0.88, 0.72, 0.60),
        ["悲伤"] = (0.15, 0.25, 0.20),
        ["紧张"] = (0.30, 0.80, 0.35),
        ["平静"] = (0.82, 0.10, 0.65),
        ["好奇"] = (0.72, 0.55, 0.58),
        ["困惑"] = (0.28, 0.50, 0.25),
        ["满足"] = (0.85, 0.20, 0.62),
        ["兴奋"] = (0.80, 0.90, 0.55),
        ["焦虑"] = (0.10, 0.82, 0.22),
        ["期待"] = (0.75, 0.52, 0.48),
        ["无奈"] = (0.22, 0.18, 0.15),
        ["惊喜"] = (0.90, 0.88, 0.50),
        ["烦躁"] = (0.16, 0.78, 0.30),
        ["轻松"] = (0.84, 0.15, 0.70),
        ["害怕"] = (0.10, 0.88, 0.18),
        ["羞愧"] = (0.14, 0.45, 0.15),
        ["感激"] = (0.82, 0.40, 0.45),
    };

    private static readonly Dictionary<string, (double Valence, double Arousal, double Dominance)> VadLexiconEn = new()
    {
        ["angry"] = (0.12, 0.85, 0.55),
        ["happy"] = (0.88, 0.72, 0.60),
        ["sad"] = (0.15, 0.25, 0.20),
        ["anxious"] = (0.10, 0.82, 0.22),
        ["excited"] = (0.80, 0.90, 0.55),
        ["calm"] = (0.82, 0.10, 0.65),
        ["frustrated"] = (0.16, 0.78, 0.30),
        ["confused"] = (0.28, 0.50, 0.25),
        ["grateful"] = (0.82, 0.40, 0.45),
    };

    private readonly ConcurrentQueue<EmotionVector> _emotionHistory = new();

    public ThreeModelIntelligence(ILogger<ThreeModelIntelligence>? logger = null)
    {
        _logger = logger;
    }

    public string? SpinalReflex(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        if (_reflexes.TryGetValue(query, out var exactMatch))
        {
            exactMatch.HitCount++;
            exactMatch.LastHit = DateTime.UtcNow;
            return exactMatch.Response;
        }

        foreach (var (pattern, rule) in _reflexes)
        {
            if (query.Contains(pattern, StringComparison.Ordinal))
            {
                rule.HitCount++;
                rule.LastHit = DateTime.UtcNow;
                return rule.Response;
            }
        }

        var queryNormal = Normalize(query);
        foreach (var (pattern, rule) in _reflexes)
        {
            if (Normalize(pattern).Contains(queryNormal, StringComparison.Ordinal) ||
                queryNormal.Contains(Normalize(pattern), StringComparison.Ordinal))
            {
                rule.HitCount++;
                rule.LastHit = DateTime.UtcNow;
                return rule.Response;
            }
        }

        return null;
    }

    public void AddReflex(string pattern, string response)
    {
        _reflexes.AddOrUpdate(pattern,
            _ => new ReflexRule { Pattern = pattern, Response = response, HitCount = 0, LastHit = DateTime.UtcNow },
            (_, existing) =>
            {
                existing.Response = response;
                existing.HitCount = 0;
                existing.LastHit = DateTime.UtcNow;
                return existing;
            });
    }

    public TriageResult Triage(string query)
    {
        var complexity = 0.0;

        if (query.Length > 500) complexity += 0.2;
        else if (query.Length > 200) complexity += 0.1;

        if (query.Contains("```")) complexity += 0.15;

        var sentences = Regex.Split(query, @"[。！？.!?\n]+");
        if (sentences.Length > 2) complexity += 0.15;

        if (ContainsAny(query, ["规划", "plan", "设计", "design", "分析", "analyze", "步骤", "step", "方案", "approach"]))
            complexity += 0.2;

        if (query.Contains('?') || query.Contains('？') || query.Contains('!') || query.Contains('！'))
            complexity += 0.1;

        complexity = Math.Clamp(complexity, 0.0, 1.0);

        var label = complexity < 0.3 ? "reflex" : complexity < 0.6 ? "fast" : "reasoning";
        var emotion = _DetectEmotion(query);
        var matchedReflex = SpinalReflex(query);

        return new TriageResult
        {
            Complexity = complexity,
            Label = label,
            Emotion = emotion,
            MatchedReflex = matchedReflex,
            Confidence = 1.0 - complexity,
            PredictedNeeds = _PredictNeeds(query)
        };
    }

    private EmotionVector _DetectEmotion(string text)
    {
        var tokens = Tokenize(text);
        var totalV = 0.0;
        var totalA = 0.0;
        var totalD = 0.0;
        var count = 0;

        foreach (var token in tokens)
        {
            if (VadLexiconCn.TryGetValue(token, out var vad) || VadLexiconEn.TryGetValue(token, out vad))
            {
                totalV += vad.Valence;
                totalA += vad.Arousal;
                totalD += vad.Dominance;
                count++;
            }
        }

        if (count == 0)
            return new EmotionVector { Valence = 0.50, Arousal = 0.35, Dominance = 0.50 };

        var rawV = totalV / count;
        var rawA = totalA / count;
        var rawD = totalD / count;

        const double blend = 0.6;
        const double center = 0.5;

        var v = rawV * blend + center * (1.0 - blend);
        var a = rawA * blend + center * (1.0 - blend);
        var d = rawD * blend + center * (1.0 - blend);

        var ev = new EmotionVector
        {
            Valence = Math.Clamp(v, 0.0, 1.0),
            Arousal = Math.Clamp(a, 0.0, 1.0),
            Dominance = Math.Clamp(d, 0.0, 1.0)
        };

        _emotionHistory.Enqueue(ev);
        while (_emotionHistory.Count > 50) _emotionHistory.TryDequeue(out _);

        return ev;
    }

    private List<string> _PredictNeeds(string query)
    {
        var queryTokens = new HashSet<string>(Tokenize(query));
        var bestJaccard = 0.0;
        List<string>? bestNeeds = null;

        lock (_trajectory)
        {
            foreach (var (storedQuery, needs) in _trajectory)
            {
                var sim = _JaccardWordSimilarity(query, storedQuery);
                if (sim > bestJaccard)
                {
                    bestJaccard = sim;
                    bestNeeds = needs;
                }
            }

            if (bestJaccard > 0.5 && bestNeeds != null)
                return bestNeeds;
        }

        var fallback = new List<string>();
        if (query.Contains("代码") || query.Contains("code")) fallback.Add("code_tool");
        if (query.Contains("文件") || query.Contains("file")) fallback.Add("file_access");
        if (query.Contains("搜索") || query.Contains("search")) fallback.Add("web_search");
        if (fallback.Count == 0) fallback.Add("general_chat");
        return fallback;
    }

    public void RecordTrajectory(string query, List<string> actualNeeds)
    {
        lock (_trajectory)
        {
            _trajectory.Add((query, actualNeeds));
            if (_trajectory.Count > 200)
                _trajectory.RemoveRange(0, _trajectory.Count - 200);
        }
    }

    public Dictionary<string, object> EmotionModifier(string query)
    {
        var emotion = _DetectEmotion(query);
        var result = new Dictionary<string, object>();

        if (emotion.IsUrgent)
        {
            result["tone"] = "urgent";
            result["temperatureAdjust"] = -0.1;
            result["skipPreload"] = true;
        }
        else if (emotion.IsNegative)
        {
            result["tone"] = "empathetic";
            result["temperatureAdjust"] = +0.05;
            result["maxTokensOverride"] = 4096;
        }
        else if (emotion.IsConfused)
        {
            result["tone"] = "clarifying";
            result["askClarification"] = true;
            result["temperatureAdjust"] = 0.0;
        }
        else
        {
            result["tone"] = "neutral";
            result["temperatureAdjust"] = 0.0;
        }

        result["valence"] = emotion.Valence;
        result["arousal"] = emotion.Arousal;
        result["dominance"] = emotion.Dominance;

        return result;
    }

    public EmotionVector GlobalEmotionalState()
    {
        var history = _emotionHistory.ToArray();
        if (history.Length == 0)
            return new EmotionVector { Valence = 0.50, Arousal = 0.35, Dominance = 0.50 };

        return new EmotionVector
        {
            Valence = history.Average(e => e.Valence),
            Arousal = history.Average(e => e.Arousal),
            Dominance = history.Average(e => e.Dominance)
        };
    }

    public async Task<List<string>> Dream(Func<string, Task<string>>? hubChatFn = null)
    {
        var insights = new List<string>();

        if (_reflexes.Count > 0)
        {
            var coldReflexes = _reflexes.Values.Where(r => r.IsCold).ToList();
            foreach (var cold in coldReflexes)
            {
                if (hubChatFn != null)
                {
                    try
                    {
                        var improved = await hubChatFn(
                            $"Improve this reflex pattern-response pair. Pattern: \"{cold.Pattern}\" Response: \"{cold.Response}\". Return just the improved response text.");
                        cold.Response = improved;
                        cold.HitCount = 0;
                        cold.LastHit = DateTime.UtcNow;
                        insights.Add($"Improved cold reflex: {cold.Pattern}");
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Dream: failed to improve reflex {Pattern}", cold.Pattern);
                    }
                }
            }
        }

        if (hubChatFn != null && _reflexes.Count > 0)
        {
            var patterns = _reflexes.Keys.Take(20).ToList();
            try
            {
                var discovery = await hubChatFn(
                    $"Given these existing reflex patterns: {string.Join(", ", patterns)}, suggest up to 3 new pattern-response pairs for related queries. Format: pattern|response per line.");
                foreach (var line in discovery.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var parts = line.Split('|', 2);
                    if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]) && !string.IsNullOrWhiteSpace(parts[1]))
                    {
                        AddReflex(parts[0].Trim(), parts[1].Trim());
                        insights.Add($"Discovered reflex: {parts[0].Trim()}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Dream: failed to discover new patterns");
            }
        }

        if (_reflexes.Count > 100)
        {
            var toRemove = _reflexes.OrderBy(r => r.Value.HitCount).ThenBy(r => r.Value.LastHit)
                .Take(_reflexes.Count - 100).Select(r => r.Key).ToList();
            foreach (var key in toRemove)
                _reflexes.TryRemove(key, out _);
            insights.Add($"Pruned {toRemove.Count} low-hit reflexes");
        }

        return insights;
    }

    private double _JaccardWordSimilarity(string a, string b)
    {
        var setA = new HashSet<string>(Tokenize(a));
        var setB = new HashSet<string>(Tokenize(b));
        if (setA.Count == 0 && setB.Count == 0) return 0.0;
        var intersection = setA.Intersect(setB).Count();
        var union = setA.Union(setB).Count();
        return union == 0 ? 0.0 : (double)intersection / union;
    }

    private static HashSet<string> Tokenize(string text)
    {
        var tokens = new HashSet<string>();

        var cnMatches = CnWordRegex().Matches(text);
        foreach (Match m in cnMatches)
            tokens.Add(m.Value);

        text = CnWordRegex().Replace(text, " ");

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var lower = word.ToLowerInvariant().Trim(',', '.', '!', '?', '，', '。', '！', '？', ';', '；', ':', '：');
            if (lower.Length >= 2)
                tokens.Add(lower);
        }

        return tokens;
    }

    private static string Normalize(string text)
    {
        return Regex.Replace(text, @"\s+", "").ToLowerInvariant();
    }

    private static bool ContainsAny(string text, string[] keywords)
    {
        var lower = text.ToLowerInvariant();
        return keywords.Any(kw => lower.Contains(kw.ToLowerInvariant()));
    }

    [GeneratedRegex(@"[\u4e00-\u9fff]+")]
    private static partial Regex CnWordRegex();
}
