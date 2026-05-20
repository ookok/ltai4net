namespace LTAI.Vector.Knowledge;

public sealed record ExtractedTemplate(string Name, string Category, string Format, List<string> Sections);
public sealed record AutoMinedTerm(string Term, string Definition, string SourceDoc, double Confidence);

public sealed class LearningEngine
{
    private static readonly Lazy<LearningEngine> _instance = new(() => new LearningEngine());
    public static LearningEngine Instance => _instance.Value;

    private readonly Dictionary<string, ExtractedTemplate> _templates = new();
    private readonly Dictionary<string, AutoMinedTerm> _minedTerms = new();
    private readonly Dictionary<string, double> _sourceQuality = new();
    private readonly object _lock = new();

    private LearningEngine() { }

    public ExtractedTemplate? ExtractTemplate(string content, string category)
    {
        var headings = System.Text.RegularExpressions.Regex.Matches(content, @"^#{1,4}\s+(.+)$",
            System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value.Trim())
            .Take(15).ToList();

        if (headings.Count < 2) return null;

        var format = DetectFormat(content);
        var template = new ExtractedTemplate(
            category + "_template",
            category, format, headings);

        lock (_lock) { _templates[template.Name] = template; }
        return template;
    }

    public List<AutoMinedTerm> MineTerms(string content, string sourceDoc)
    {
        var terms = new List<AutoMinedTerm>();
        var defs = System.Text.RegularExpressions.Regex.Matches(content,
            @"(\w{2,10})\s*[是为指即].*?[。；;]", System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromSeconds(10));

        foreach (System.Text.RegularExpressions.Match m in defs.Take(20))
        {
            var parts = m.Value.Split(new[] { '是', '为', '指', '即' }, 2);
            if (parts.Length == 2 && parts[0].Trim().Length >= 2)
            {
                var term = new AutoMinedTerm(parts[0].Trim(), parts[1].Trim().TrimEnd('。', '；', ';'), sourceDoc, 0.5);
                terms.Add(term);
                lock (_lock) { _minedTerms[term.Term] = term; }
            }
        }

        if (terms.Count > 0)
            lock (_lock) { _sourceQuality[sourceDoc] = _sourceQuality.GetValueOrDefault(sourceDoc) * 0.9 + 0.1; }

        return terms;
    }

    public List<ExtractedTemplate> ListTemplates(string? category = null)
    {
        lock (_lock)
        {
            return string.IsNullOrEmpty(category)
                ? _templates.Values.ToList()
                : _templates.Values.Where(t => t.Category == category).ToList();
        }
    }

    public List<string> GetCategories()
    {
        lock (_lock) { return _templates.Values.Select(t => t.Category).Distinct().ToList(); }
    }

    public void RecordFeedback(string source, bool isRelevant)
    {
        lock (_lock)
        {
            var current = _sourceQuality.GetValueOrDefault(source);
            _sourceQuality[source] = current * 0.9 + (isRelevant ? 0.2 : 0.05);
        }
    }

    public Dictionary<string, object> GetStats() => new()
    {
        ["templates"] = _templates.Count,
        ["mined_terms"] = _minedTerms.Count,
        ["sources_tracked"] = _sourceQuality.Count,
        ["top_sources"] = _sourceQuality.OrderByDescending(kv => kv.Value).Take(5)
            .Select(kv => new { kv.Key, quality = Math.Round(kv.Value, 2) }).ToList()
    };

    private static string DetectFormat(string content)
    {
        if (content.Contains("环评") || content.Contains("环境影响")) return "eia";
        if (content.Contains("class ") || content.Contains("public ") || content.Contains("def ")) return "code";
        if (content.Contains("表格") || content.Contains("|--")) return "table";
        if (content.Contains("# ") && content.Contains("## ")) return "markdown";
        return "text";
    }
}
