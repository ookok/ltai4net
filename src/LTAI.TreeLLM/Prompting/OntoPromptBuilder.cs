namespace LTAI.TreeLLM.Prompting;

public sealed class OntoPromptBuilder
{
    private static readonly Lazy<OntoPromptBuilder> _instance = new(() => new OntoPromptBuilder());
    public static OntoPromptBuilder Instance => _instance.Value;

    private static readonly HashSet<string> StopWords = new()
    {
        "the", "a", "an", "is", "are", "was", "were", "be", "been", "being",
        "have", "has", "had", "do", "does", "did", "will", "would", "could",
        "should", "may", "might", "can", "shall", "to", "of", "in", "for",
        "on", "with", "at", "by", "from", "as", "into", "through", "during",
        "before", "after", "above", "below", "between", "and", "but", "or",
        "not", "this", "that", "these", "those", "it", "its", "的", "是",
        "在", "了", "和", "与", "或", "不", "这", "那", "我", "你", "他", "她", "它", "们"
    };

    private OntoPromptBuilder() { }

    public Dictionary<string, object> Build(string userInput, string? skillContext = null, string? glossaryContext = null)
    {
        var keywords = ExtractKeywords(userInput);
        var conceptChain = new List<string>();
        var entitiesUsed = new List<string>();

        foreach (var kw in keywords.Take(5))
        {
            conceptChain.Add(kw);
            entitiesUsed.Add(kw);
        }

        var systemPrompt = BuildSystemSection(keywords, skillContext, glossaryContext);
        var userPrompt = BuildUserSection(userInput, keywords);

        return new Dictionary<string, object>
        {
            ["system_prompt"] = systemPrompt,
            ["user_prompt"] = userPrompt,
            ["concept_chain"] = conceptChain,
            ["entities_used"] = entitiesUsed,
            ["keywords"] = keywords
        };
    }

    public string BuildSkillContext(List<string> skillNames)
    {
        if (skillNames.Count == 0) return "";
        var parts = new List<string> { "## Available Skills" };
        foreach (var s in skillNames.Take(10))
            parts.Add($"- {s}");
        return string.Join("\n", parts);
    }

    public string BuildGlossaryContext(Dictionary<string, string> terms)
    {
        if (terms.Count == 0) return "";
        var parts = new List<string> { "## Glossary" };
        foreach (var (k, v) in terms.Take(10))
            parts.Add($"- **{k}**: {v}");
        return string.Join("\n", parts);
    }

    public string EnrichPromptTemplate(string template, Dictionary<string, string> variables)
    {
        var result = template;
        foreach (var (k, v) in variables)
            result = result.Replace($"{{{k}}}", v);
        return result;
    }

    public List<string> GetSuggestedSkills(string userInput, List<string> availableSkills)
    {
        var keywords = ExtractKeywords(userInput);
        return availableSkills
            .Where(s => keywords.Any(k => s.ToLower().Contains(k.ToLower())))
            .Take(5).ToList();
    }

    private string BuildSystemSection(List<string> keywords, string? skillContext, string? glossaryContext)
    {
        var parts = new List<string> { "You are an AI assistant with domain expertise." };
        if (keywords.Count > 0)
            parts.Add($"Key concepts: {string.Join(", ", keywords.Take(8))}");
        if (!string.IsNullOrEmpty(skillContext))
            parts.Add(skillContext);
        if (!string.IsNullOrEmpty(glossaryContext))
            parts.Add(glossaryContext);
        return string.Join("\n\n", parts);
    }

    private static string BuildUserSection(string input, List<string> keywords)
    {
        if (keywords.Count == 0) return input;
        return $"[Context: {string.Join(", ", keywords.Take(5))}]\n\n{input}";
    }

    public static List<string> ExtractKeywords(string text)
    {
        var words = text.ToLower()
            .Split(new[] { ' ', '\n', '\t', ',', '.', '!', '?', ';', ':', '(', ')', '[', ']', '{', '}', '"', '\'' },
                   StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim().TrimEnd('s'))
            .Where(w => w.Length > 2 && !StopWords.Contains(w))
            .GroupBy(w => w)
            .OrderByDescending(g => g.Count())
            .Take(15)
            .Select(g => g.Key)
            .ToList();

        var chineseWords = System.Text.RegularExpressions.Regex.Matches(text, @"[\u4e00-\u9fff]{2,}")
            .Select(m => m.Value)
            .Where(w => !StopWords.Contains(w))
            .GroupBy(w => w)
            .OrderByDescending(g => g.Count())
            .Take(10)
            .Select(g => g.Key);

        return words.Concat(chineseWords).Distinct().Take(15).ToList();
    }
}
