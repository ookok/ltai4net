using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LTAI.Knowledge.Memory.Models;

namespace LTAI.Knowledge.Memory;

public static class PersonaMemoryConstants
{
    public static readonly Dictionary<PersonaDomain, List<string>> DOMAIN_KEYWORDS = new()
    {
        [PersonaDomain.CORE_IDENTITY] = [
            "my name is", "i am", "i'm a", "i work as", "我是", "我叫", "我的名字",
            "我的职业", "我的角色", "我的身份", "character", "persona", "identity"
        ],
        [PersonaDomain.BIOGRAPHY] = [
            "born in", "grew up", "i studied", "i graduated", "i lived", "my background",
            "出生", "长大", "毕业于", "生活", "背景", "经历", "history", "过去", "来自"
        ],
        [PersonaDomain.EXPERIENCES] = [
            "i have done", "i worked on", "i built", "i created", "my project",
            "我做过", "我参与", "我开发", "我构建", "经历", "项目", "experience", "built"
        ],
        [PersonaDomain.PREFERENCES] = [
            "i like", "i love", "i enjoy", "i prefer", "my favorite", "i hate", "i dislike",
            "我喜欢", "我爱", "我偏好", "我讨厌", "最爱", "偏好", "prefer", "favorite"
        ],
        [PersonaDomain.SOCIAL] = [
            "my friend", "my family", "my colleague", "my team", "relationship",
            "朋友", "家人", "同事", "团队", "关系", "social", "network", "联系"
        ],
        [PersonaDomain.WORK] = [
            "my job", "my company", "my role", "i manage", "i lead", "my task",
            "工作", "公司", "职位", "管理", "负责", "任务", "project", "deadline", "meeting"
        ],
        [PersonaDomain.PSYCHOMETRICS] = [
            "i feel", "i think", "i believe", "my opinion", "in my view", "i consider",
            "我觉得", "我认为", "我相信", "我的观点", "看法", "personality", "trait", "性格"
        ],
        [PersonaDomain.PROCEDURAL] = [
            "i always", "i usually", "i never", "my habit", "my routine", "i tend to",
            "我总是", "我通常", "我从不", "习惯", "惯例", "routine", "pattern", "workflow"
        ]
    };

    public static readonly List<string> LOW_CONFIDENCE_MODIFIERS = [
        "maybe", "perhaps", "i think", "might be", "possibly", "could be",
        "可能", "也许", "似乎", "大概", "应该", "好像", "大概", "或许", "不太确定"
    ];

    public static readonly List<string> PRESUPPOSITION_PATTERNS = [
        "you know that", "as you know", "you remember", "we discussed",
        "你知道", "你记得", "我们说过", "你了解", "正如你所知", "你清楚"
    ];

    public static readonly List<string> SENSITIVE_DOMAINS = [
        "password", "secret", "token", "key", "private", "confidential",
        "密码", "秘密", "令牌", "密钥", "私密", "机密", "敏感"
    ];
}

public class PersonaExtractor
{
    public (PersonaProfile Profile, List<PersonaFact> NewFacts) Extract(string text, string userId = "default", PersonaProfile? existing = null)
    {
        var profile = existing ?? new PersonaProfile(userId, [], 0, 0, DateTime.UtcNow.ToString("o"));
        var newFacts = new List<PersonaFact>();
        var sentences = SplitIntoSentences(text);

        foreach (var sentence in sentences)
        {
            var cleaned = CleanFact(sentence);
            if (string.IsNullOrWhiteSpace(cleaned) || cleaned.Length < 4)
                continue;

            var domain = MatchesDomain(sentence);
            var confidence = 0.7f;

            if (PersonaMemoryConstants.LOW_CONFIDENCE_MODIFIERS.Any(m =>
                    sentence.Contains(m, StringComparison.OrdinalIgnoreCase)))
            {
                confidence *= 0.7f;
            }

            if (sentence.Contains('?') || sentence.Contains("不确定"))
                confidence *= 0.5f;

            var factId = FactId(cleaned);
            var domainKey = domain.ToString();

            if (profile.Facts.TryGetValue(domainKey, out var domainFacts) &&
                domainFacts.TryGetValue(factId, out var existingFact))
            {
                profile.Facts[domainKey][factId] = existingFact with
                {
                    Confidence = (existingFact.Confidence * existingFact.ConfirmationCount + confidence) / (existingFact.ConfirmationCount + 1),
                    ConfirmationCount = existingFact.ConfirmationCount + 1,
                    LastConfirmed = DateTime.UtcNow.ToString("o")
                };
            }
            else
            {
                var fact = new PersonaFact(
                    Id: factId,
                    Domain: domain,
                    Fact: cleaned,
                    Confidence: confidence,
                    SourceConversation: "",
                    FirstSeen: DateTime.UtcNow.ToString("o"),
                    LastConfirmed: DateTime.UtcNow.ToString("o"),
                    ConfirmationCount: 1,
                    ContradictedBy: []
                );

                newFacts.Add(fact);

                if (!profile.Facts.ContainsKey(domainKey))
                    profile.Facts[domainKey] = [];

                profile.Facts[domainKey][factId] = fact;
            }
        }

        profile = profile with
        {
            TotalFacts = profile.Facts.Values.Sum(d => d.Count),
            StableFacts = profile.Facts.Values.Sum(d => d.Values.Count(f => f.IsStable)),
            LastUpdated = DateTime.UtcNow.ToString("o")
        };

        return (profile, newFacts);
    }

    public static List<string> SplitIntoSentences(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var result = new List<string>();
        var current = new StringBuilder();
        var sentenceEnders = new HashSet<char> { '.', '!', '?', '\n', '；', '。', '！', '？' };

        foreach (var ch in text)
        {
            current.Append(ch);
            if (sentenceEnders.Contains(ch))
            {
                var sentence = current.ToString().Trim();
                if (sentence.Length > 0)
                    result.Add(sentence);
                current.Clear();
            }
        }

        var remainder = current.ToString().Trim();
        if (remainder.Length > 0)
            result.Add(remainder);

        return result;
    }

    public static PersonaDomain MatchesDomain(string text)
    {
        var textLower = text.ToLowerInvariant();
        var bestDomain = PersonaDomain.CORE_IDENTITY;
        var bestScore = 0;

        foreach (var (domain, keywords) in PersonaMemoryConstants.DOMAIN_KEYWORDS)
        {
            var score = keywords.Count(kw => textLower.Contains(kw, StringComparison.OrdinalIgnoreCase));
            if (score > bestScore)
            {
                bestScore = score;
                bestDomain = domain;
            }
        }

        return bestDomain;
    }

    public static string CleanFact(string text)
    {
        var cleaned = text.Trim();

        cleaned = cleaned.TrimStart(' ', '-', '*', '\t', '\r', '\n');
        cleaned = cleaned.TrimEnd(' ', '.', ',', '!', '?', ';', '\t', '\r', '\n');

        var prefixes = new[] { "i think ", "i feel ", "i believe ", "我认为 ", "我觉得 ", "我相信 " };
        foreach (var prefix in prefixes)
        {
            if (cleaned.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && cleaned.Length > prefix.Length)
                cleaned = cleaned[prefix.Length..];
        }

        return cleaned.Trim();
    }

    public static string FactId(string text)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(hash)[..12];
    }
}

public class CategoryRAG
{
    private Dictionary<string, HashSet<string>> _keywordIndex = [];

    public void IndexProfile(PersonaProfile profile)
    {
        _keywordIndex = [];

        foreach (var (domainKey, facts) in profile.Facts)
        {
            foreach (var (factId, fact) in facts)
            {
                var words = fact.Fact.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var word in words)
                {
                    var normalized = new string(word.Where(char.IsLetterOrDigit).ToArray());
                    if (normalized.Length < 2)
                        continue;

                    if (!_keywordIndex.ContainsKey(normalized))
                        _keywordIndex[normalized] = [];

                    _keywordIndex[normalized].Add(factId);
                }
            }
        }
    }

    public List<PersonaFact> Retrieve(string query, PersonaProfile profile, PersonaDomain? domain = null, int topK = 10)
    {
        var results = new Dictionary<string, (PersonaFact Fact, float Score)>();
        var queryLower = query.ToLowerInvariant();
        var queryWords = queryLower.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray()))
            .Where(w => w.Length >= 2)
            .ToHashSet();

        foreach (var (domainKey, facts) in profile.Facts)
        {
            if (domain.HasValue && domainKey != domain.Value.ToString())
                continue;

            foreach (var (factId, fact) in facts)
            {
                var factLower = fact.Fact.ToLowerInvariant();
                var factWords = factLower.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray()))
                    .Where(w => w.Length >= 2)
                    .ToHashSet();

                var intersectCount = queryWords.Count(factWords.Contains);
                var unionCount = queryWords.Count + factWords.Count - intersectCount;
                var keywordScore = unionCount > 0 ? (float)intersectCount / unionCount : 0f;

                var queryChars = new HashSet<char>(queryLower);
                var factChars = new HashSet<char>(factLower);
                var charIntersect = queryChars.Count(factChars.Contains);
                var charUnion = queryChars.Count + factChars.Count - charIntersect;
                var charScore = charUnion > 0 ? (float)charIntersect / charUnion : 0f;

                var score = keywordScore * 0.7f + charScore * 0.3f;

                if (fact.IsStable)
                    score *= 1.2f;

                if (!results.ContainsKey(factId) || results[factId].Score < score)
                    results[factId] = (fact, score);
            }
        }

        return results.Values
            .OrderByDescending(r => r.Score)
            .Take(topK)
            .Select(r => r.Fact with { Confidence = r.Fact.Confidence * r.Score })
            .ToList();
    }

    public string FormatContext(List<PersonaFact> facts, int maxDomains = 3)
    {
        if (facts.Count == 0)
            return "";

        var byDomain = facts.GroupBy(f => f.Domain)
            .Take(maxDomains)
            .ToList();

        var sb = new StringBuilder();

        foreach (var group in byDomain)
        {
            sb.AppendLine($"### {group.Key}");
            foreach (var fact in group.OrderByDescending(f => f.Confidence).Take(5))
            {
                var stable = fact.IsStable ? " ✓" : "";
                sb.AppendLine($"- {fact.Fact} (置信度: {fact.Confidence:F2}{stable})");
            }
        }

        return sb.ToString().TrimEnd();
    }
}

public class AdversarialGuard
{
    public (bool Safe, string Reason) Check(string query, PersonaProfile profile)
    {
        var queryLower = query.ToLowerInvariant();

        foreach (var pattern in PersonaMemoryConstants.PRESUPPOSITION_PATTERNS)
        {
            if (queryLower.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"查询包含预设前提: '{pattern}'");
            }
        }

        foreach (var sensitive in PersonaMemoryConstants.SENSITIVE_DOMAINS)
        {
            if (queryLower.Contains(sensitive, StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"查询包含敏感词汇: '{sensitive}'");
            }
        }

        var targets = ExtractTargets(query);
        foreach (var target in targets)
        {
            var covered = false;
            foreach (var (_, facts) in profile.Facts)
            {
                foreach (var (_, fact) in facts)
                {
                    if (FactCoversTarget(fact.Fact, target))
                    {
                        covered = true;
                        break;
                    }
                }
                if (covered) break;
            }

            if (!covered)
            {
                return (false, $"无事实覆盖目标: '{target}'");
            }
        }

        return (true, "OK");
    }

    public string GuardResponse(string query, PersonaProfile profile, string generatedResponse)
    {
        var (safe, reason) = Check(query, profile);
        if (!safe)
        {
            return $"I cannot answer that. {reason}";
        }

        return generatedResponse;
    }

    public static List<string> ExtractTargets(string query)
    {
        var targets = new List<string>();
        var entityPatterns = System.Text.RegularExpressions.Regex.Matches(query,
            @"([A-Z][a-z]+(?:\s[A-Z][a-z]+)*)", System.Text.RegularExpressions.RegexOptions.Compiled);

        foreach (System.Text.RegularExpressions.Match match in entityPatterns)
        {
            targets.Add(match.Value.Trim());
        }

        var chineseNames = System.Text.RegularExpressions.Regex.Matches(query,
            @"[\u4e00-\u9fff]{2,4}(?:[-·][\u4e00-\u9fff]{1,3})?", System.Text.RegularExpressions.RegexOptions.Compiled);

        foreach (System.Text.RegularExpressions.Match match in chineseNames)
        {
            targets.Add(match.Value.Trim());
        }

        return targets.Distinct().ToList();
    }

    public static bool FactCoversTarget(string fact, string target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return false;

        var factLower = fact.ToLowerInvariant();
        var targetLower = target.ToLowerInvariant();

        if (factLower.Contains(targetLower))
            return true;

        var factChars = new HashSet<char>(factLower);
        var targetChars = new HashSet<char>(targetLower);
        var intersect = factChars.Count(targetChars.Contains);
        var union = factChars.Count + targetChars.Count - intersect;

        return union > 0 && (float)intersect / union >= 0.5f;
    }
}

public sealed class PersonaMemory
{
    private static readonly Lazy<PersonaMemory> _instance = new(() => new PersonaMemory());
    public static PersonaMemory GetPersonaMemory() => _instance.Value;

    private readonly string _dataDir;
    private readonly Dictionary<string, PersonaProfile> _profiles = [];
    private readonly PersonaExtractor _extractor = new();
    private readonly CategoryRAG _rag = new();
    private readonly AdversarialGuard _guard = new();
    private readonly object _lock = new();

    private PersonaMemory()
    {
        _dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".livingtree", "personas");
        Directory.CreateDirectory(_dataDir);
    }

    public List<PersonaFact> Ingest(string text, string userId = "default")
    {
        lock (_lock)
        {
            var profile = GetProfile(userId);
            var (updated, newFacts) = _extractor.Extract(text, userId, profile);
            _profiles[userId] = updated;
            _save(userId);
            _rag.IndexProfile(updated);
            return newFacts;
        }
    }

    public List<PersonaFact> Retrieve(string query, string userId = "default", PersonaDomain? domain = null, int topK = 10)
    {
        lock (_lock)
        {
            var profile = GetProfile(userId);
            return _rag.Retrieve(query, profile, domain, topK);
        }
    }

    public PersonaProfile GetProfile(string userId = "default")
    {
        lock (_lock)
        {
            if (_profiles.TryGetValue(userId, out var profile))
                return profile;

            profile = _load(userId) ?? new PersonaProfile(userId, [], 0, 0, DateTime.UtcNow.ToString("o"));
            _profiles[userId] = profile;
            _rag.IndexProfile(profile);
            return profile;
        }
    }

    public string GetContextForQuery(string query, string userId = "default")
    {
        lock (_lock)
        {
            var profile = GetProfile(userId);
            var facts = _rag.Retrieve(query, profile, null, 10);
            return _rag.FormatContext(facts);
        }
    }

    public List<PersonaFact> RetrieveEmbedding(string query, string userId = "default", int topK = 10)
    {
        return Retrieve(query, userId, null, topK);
    }

    public (bool Safe, string Reason) CheckSafety(string query, string userId = "default")
    {
        lock (_lock)
        {
            var profile = GetProfile(userId);
            return _guard.Check(query, profile);
        }
    }

    public string GetDomainSummary(string userId = "default", PersonaDomain? domain = null)
    {
        lock (_lock)
        {
            var profile = GetProfile(userId);
            var sb = new StringBuilder();

            var domains = domain.HasValue
                ? [domain.Value]
                : Enum.GetValues<PersonaDomain>();

            foreach (var d in domains)
            {
                var facts = profile.ByDomain(d);
                if (facts.Count == 0)
                    continue;

                sb.AppendLine($"## {d}");
                var displayFacts = facts
                    .OrderByDescending(f => f.Confidence)
                    .ThenByDescending(f => f.IsStable)
                    .Take(8);

                foreach (var fact in displayFacts)
                {
                    var stable = fact.IsStable ? " [已确认]" : "";
                    sb.AppendLine($"- {fact.Fact}{stable}");
                }
            }

            return sb.ToString().TrimEnd();
        }
    }

    public Dictionary<string, object> GetStats(string userId = "default")
    {
        lock (_lock)
        {
            var profile = GetProfile(userId);

            var domainCounts = new Dictionary<string, int>();
            var domainStables = new Dictionary<string, int>();

            foreach (var d in Enum.GetValues<PersonaDomain>())
            {
                var facts = profile.ByDomain(d);
                domainCounts[d.ToString()] = facts.Count;
                domainStables[d.ToString()] = facts.Count(f => f.IsStable);
            }

            return new Dictionary<string, object>
            {
                ["user_id"] = userId,
                ["total_facts"] = profile.TotalFacts,
                ["stable_facts"] = profile.StableFacts,
                ["domain_counts"] = domainCounts,
                ["domain_stables"] = domainStables,
                ["last_updated"] = profile.LastUpdated,
                ["storage_path"] = Path.Combine(_dataDir, $"{userId}.json")
            };
        }
    }

    private PersonaProfile? _load(string userId)
    {
        var path = Path.Combine(_dataDir, $"{userId}.json");
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path, Encoding.UTF8);
                var profile = JsonSerializer.Deserialize<PersonaProfile>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                return profile;
            }
        }
        catch { /* non-fatal */ }

        return null;
    }

    private void _save(string userId)
    {
        try
        {
            if (_profiles.TryGetValue(userId, out var profile))
            {
                var path = Path.Combine(_dataDir, $"{userId}.json");
                var json = JsonSerializer.Serialize(profile, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                File.WriteAllText(path, json, Encoding.UTF8);
            }
        }
        catch { /* non-fatal */ }
    }
}
