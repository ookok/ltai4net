using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed record ExtractedRule
{
    public string Pattern { get; init; } = "";
    public string Response { get; init; } = "";
    public string Domain { get; init; } = "";
    public float Confidence { get; init; }
    public int UsageCount { get; init; }
    public int SuccessCount { get; init; }
    public DateTime CreatedAt { get; init; }
    public string SourceQuery { get; init; } = "";
    public List<string> KeyConcepts { get; init; } = new();
}

public sealed record RuleExtractionResult
{
    public List<ExtractedRule> Rules { get; init; } = new();
    public int PatternCount { get; init; }
    public string Summary { get; init; } = "";
}

public sealed class TeachingRuleExtractor
{
    private readonly List<ExtractedRule> _rules = new();
    private readonly CellAnswerStore _answerStore;
    private readonly ILogger<TeachingRuleExtractor> _logger;
    private readonly object _lock = new();

    private static readonly Regex[] PatternExtractors = new[]
    {
        new Regex(@"(?:what|什么是|什么叫|定义)\s*(?:is|are|是)?\s*([^\?]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"(?:how|怎么|如何|怎样)\s*(?:to|do|做)?\s*([^\?]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"(?:why|为什么|原因)\s*(?:is|are|是)?\s*([^\?]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"([a-zA-Z\u4e00-\u9fff]+)\s*(?:的|is|are)\s*(?:意思|含义|definition|meaning)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"(?:calculate|计算|求解)\s*([^\?]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"(?:explain|解释|说明)\s*([^\?]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"(?:compare|比较|区别|差异)\s*(?:between|和|与)?\s*([^\?]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"(?:list|列出|列举)\s*([^\?]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"(?:steps|步骤|方法|如何)\s*(?:to|for|做|实现)?\s*([^\?]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"(?:example|例子|示例|举例)\s*(?:of|的)?\s*([^\?]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    };

    public TeachingRuleExtractor(
        CellAnswerStore answerStore,
        ILogger<TeachingRuleExtractor>? logger = null)
    {
        _answerStore = answerStore;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TeachingRuleExtractor>.Instance;
    }

    public RuleExtractionResult ExtractFromTeaching(string query, L2TeachingResult teaching, string domain)
    {
        var rules = new List<ExtractedRule>();

        var patterns = ExtractPatterns(query);
        var concepts = ParseConcepts(teaching.KeyConcepts);

        foreach (var pattern in patterns)
        {
            var rule = new ExtractedRule
            {
                Pattern = pattern,
                Response = teaching.Answer,
                Domain = domain,
                Confidence = 0.7f,
                CreatedAt = DateTime.UtcNow,
                SourceQuery = query,
                KeyConcepts = concepts
            };

            rules.Add(rule);
        }

        var simplifiedRule = new ExtractedRule
        {
            Pattern = BuildSimplifiedPattern(query, concepts),
            Response = teaching.SimplifiedExplanation,
            Domain = domain,
            Confidence = 0.6f,
            CreatedAt = DateTime.UtcNow,
            SourceQuery = query,
            KeyConcepts = concepts
        };

        rules.Add(simplifiedRule);

        lock (_lock)
        {
            _rules.AddRange(rules);
        }

        foreach (var rule in rules.Take(2))
        {
            _answerStore.AddAnswer(domain, rule.Pattern, rule.Response, rule.Confidence);
        }

        return new RuleExtractionResult
        {
            Rules = rules,
            PatternCount = rules.Count,
            Summary = $"Extracted {rules.Count} rules from teaching: domain={domain}, concepts={string.Join(", ", concepts)}"
        };
    }

    public RuleExtractionResult BatchExtractFromExperiences(List<SynapticExperience> experiences)
    {
        var allRules = new List<ExtractedRule>();
        var teachingExperiences = experiences.Where(e => e.Type == SynapseType.Teaching).ToList();

        foreach (var exp in teachingExperiences)
        {
            try
            {
                var teaching = new L2TeachingResult
                {
                    Answer = exp.Response,
                    KeyConcepts = ExtractConceptsFromText(exp.Response)
                };

                var domain = DetectDomainFromExperience(exp);
                var result = ExtractFromTeaching(exp.Query, teaching, domain);
                allRules.AddRange(result.Rules);
            }
            catch
            {
                // Skip failed extractions
            }
        }

        return new RuleExtractionResult
        {
            Rules = allRules,
            PatternCount = allRules.Count,
            Summary = $"Batch extracted {allRules.Count} rules from {teachingExperiences.Count} teaching experiences"
        };
    }

    public Dictionary<string, object> GetStats()
    {
        lock (_lock)
        {
            var byDomain = _rules.GroupBy(r => r.Domain)
                .ToDictionary(g => g.Key, g => new { Count = g.Count(), AvgConfidence = g.Average(r => r.Confidence) });

            return new Dictionary<string, object>
            {
                ["total_rules"] = _rules.Count,
                ["by_domain"] = byDomain,
                ["avg_confidence"] = _rules.Count > 0 ? _rules.Average(r => r.Confidence) : 0f,
                ["high_confidence_rules"] = _rules.Count(r => r.Confidence >= 0.8f)
            };
        }
    }

    private static List<string> ExtractPatterns(string query)
    {
        var patterns = new List<string>();

        foreach (var regex in PatternExtractors)
        {
            var match = regex.Match(query);
            if (match.Success && match.Groups.Count > 1)
            {
                var captured = match.Groups[1].Value.Trim();
                if (captured.Length > 2 && captured.Length < 100)
                {
                    patterns.Add(BuildRegexPattern(captured));
                }
            }
        }

        if (patterns.Count == 0)
        {
            patterns.Add(BuildFallbackPattern(query));
        }

        return patterns;
    }

    private static string BuildRegexPattern(string captured)
    {
        var words = captured.Split(new[] { ' ', '\t', ',', '，' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => w.Length > 1)
            .Select(w => Regex.Escape(w))
            .Take(3);

        return string.Join(".*", words);
    }

    private static string BuildFallbackPattern(string query)
    {
        var words = query.Split(new[] { ' ', '\t', ',', '，' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => w.Length > 2)
            .Select(w => Regex.Escape(w.ToLowerInvariant()))
            .Take(3);

        return string.Join(".*", words);
    }

    private static string BuildSimplifiedPattern(string query, List<string> concepts)
    {
        var keyTerms = concepts.Take(2).Concat(
            query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 2)
                .Take(2))
            .Distinct()
            .Select(w => Regex.Escape(w.ToLowerInvariant()));

        return string.Join(".*", keyTerms);
    }

    private static List<string> ParseConcepts(string conceptsText)
    {
        if (string.IsNullOrEmpty(conceptsText))
            return new List<string>();

        return conceptsText.Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(c => c.Length > 1 && c.Length < 50)
            .Select(c => c.Trim())
            .ToList();
    }

    private static string ExtractConceptsFromText(string text)
    {
        var conceptPatterns = new[]
        {
            new Regex(@"(?:concept|概念|key|关键|核心)[\s:：]*([^\n\.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"(?:important|重要|main|主要)[\s:：]*([^\n\.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"(?:step|步骤|first|首先|second|其次)[\s:：]*([^\n\.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        };

        var concepts = new List<string>();
        foreach (var pattern in conceptPatterns)
        {
            var matches = pattern.Matches(text);
            foreach (Match match in matches)
            {
                if (match.Groups.Count > 1)
                    concepts.Add(match.Groups[1].Value.Trim());
            }
        }

        return string.Join(", ", concepts.Distinct().Take(5));
    }

    private static string DetectDomainFromExperience(SynapticExperience exp)
    {
        var lower = exp.Query.ToLowerInvariant();
        if (lower.Contains("code") || lower.Contains("函数") || lower.Contains("编程")) return "code";
        if (lower.Contains("calculate") || lower.Contains("计算") || lower.Contains("数学")) return "math";
        if (lower.Contains("physics") || lower.Contains("物理") || lower.Contains("科学")) return "science";
        if (lower.Contains("translate") || lower.Contains("翻译") || lower.Contains("语法")) return "language";
        if (lower.Contains("system") || lower.Contains("系统") || lower.Contains("配置")) return "system";
        if (lower.Contains("write") || lower.Contains("写") || lower.Contains("创意")) return "creative";
        return "general";
    }
}
