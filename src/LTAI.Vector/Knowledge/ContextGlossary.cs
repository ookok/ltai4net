namespace LTAI.Vector.Knowledge;

public sealed class DomainTerm
{
    public string Term { get; set; } = "";
    public string Category { get; set; } = "";
    public string Definition { get; set; } = "";
    public List<string> Aliases { get; set; } = new();
    public List<string> RelatedTerms { get; set; } = new();
    public List<string> Examples { get; set; } = new();
    public int Priority { get; set; }
}

public sealed class ContextGlossary
{
    private static readonly Lazy<ContextGlossary> _instance = new(() => new ContextGlossary());
    public static ContextGlossary Instance => _instance.Value;

    private readonly Dictionary<string, DomainTerm> _terms = new();
    private readonly Dictionary<string, HashSet<string>> _aliasIndex = new();
    private readonly Dictionary<string, HashSet<string>> _relationGraph = new();

    private ContextGlossary()
    {
        SeedDefaults();
    }

    public void Register(DomainTerm term)
    {
        _terms[term.Term.ToLower()] = term;
        foreach (var alias in term.Aliases)
        {
            var key = alias.ToLower();
            if (!_aliasIndex.ContainsKey(key)) _aliasIndex[key] = new();
            _aliasIndex[key].Add(term.Term);
        }
        foreach (var related in term.RelatedTerms)
        {
            var key = term.Term.ToLower();
            if (!_relationGraph.ContainsKey(key)) _relationGraph[key] = new();
            _relationGraph[key].Add(related.ToLower());
        }
    }

    public DomainTerm? Get(string term)
    {
        var key = term.ToLower();
        if (_terms.TryGetValue(key, out var t)) return t;
        if (_aliasIndex.TryGetValue(key, out var terms))
            return terms.Select(t => _terms.GetValueOrDefault(t)).FirstOrDefault(t => t != null);
        return null;
    }

    public List<DomainTerm> Search(string query, string? category = null)
    {
        var q = query.ToLower();
        var results = _terms.Values.Where(t =>
            t.Term.ToLower().Contains(q) ||
            t.Definition.ToLower().Contains(q) ||
            t.Aliases.Any(a => a.ToLower().Contains(q)));

        if (!string.IsNullOrEmpty(category))
            results = results.Where(t => t.Category == category);

        return results.OrderByDescending(t => t.Priority).ThenBy(t => t.Term).Take(20).ToList();
    }

    public List<DomainTerm> IncrementalSearch(string prefix)
    {
        var p = prefix.ToLower();
        return _terms.Values
            .Where(t => t.Term.ToLower().StartsWith(p))
            .OrderBy(t => t.Term)
            .Take(10).ToList();
    }

    public List<DomainTerm> GetByCategory(string category) =>
        _terms.Values.Where(t => t.Category == category).OrderBy(t => t.Term).ToList();

    public List<string> GetCategories() =>
        _terms.Values.Select(t => t.Category).Distinct().OrderBy(c => c).ToList();

    public string ExportForAgent(string query)
    {
        var terms = Search(query);
        if (terms.Count == 0) return "";
        return "## 术语表\n" + string.Join("\n", terms.Select(t =>
            $"- **{t.Term}** [{t.Category}]: {t.Definition}" +
            (t.Aliases.Count > 0 ? $" (别名: {string.Join(", ", t.Aliases)})" : "")));
    }

    public Dictionary<string, object> GetStats() => new()
    {
        ["terms"] = _terms.Count,
        ["categories"] = GetCategories().Count,
        ["aliases"] = _aliasIndex.Count,
        ["relations"] = _relationGraph.Values.Sum(v => v.Count)
    };

    private void SeedDefaults()
    {
        var defaults = new[]
        {
            new DomainTerm { Term = "RAG", Category = "AI", Definition = "检索增强生成", Aliases = new(){"检索增强", "Retrieval-Augmented Generation"}, Priority = 10 },
            new DomainTerm { Term = "CoT", Category = "AI", Definition = "思维链推理", Aliases = new(){"Chain of Thought", "思维链"}, RelatedTerms = new(){"RAG", "LLM"}, Priority = 10 },
            new DomainTerm { Term = "GB3095", Category = "EIA", Definition = "环境空气质量标准(2012)", Aliases = new(){"大气标准", "环境空气质量标准"}, Priority = 9 },
            new DomainTerm { Term = "HJ2.2", Category = "EIA", Definition = "大气环境影响评价技术导则(2018)", Aliases = new(){"大气导则"}, RelatedTerms = new(){"GB3095", "gaussian_plume"}, Priority = 9 },
            new DomainTerm { Term = "GRPO", Category = "AI", Definition = "群组相对策略优化", RelatedTerms = new(){"RL", "PPO"}, Priority = 8 },
            new DomainTerm { Term = "A2A", Category = "Agent", Definition = "Agent-to-Agent协议", Priority = 8 },
            new DomainTerm { Term = "MCP", Category = "Agent", Definition = "模型上下文协议", Priority = 8 },
            new DomainTerm { Term = "CodeAct", Category = "Agent", Definition = "代码即行动模式", RelatedTerms = new(){"Hyperlight", "MCP"}, Priority = 8 },
            new DomainTerm { Term = "VAD", Category = "AI", Definition = "效价-唤醒度-支配度情绪模型", Priority = 7 },
            new DomainTerm { Term = "GWP100", Category = "EIA", Definition = "全球变暖潜势(100年)", RelatedTerms = new(){"CO2", "GHG", "IPCC"}, Priority = 7 },
            new DomainTerm { Term = "GCJ02", Category = "GIS", Definition = "国测局坐标系(火星坐标)", RelatedTerms = new(){"WGS84", "CGCS2000"}, Priority = 7 },
            new DomainTerm { Term = "WGS84", Category = "GIS", Definition = "世界大地测量系统1984", Aliases = new(){"GPS坐标"}, Priority = 7 },
        };

        foreach (var t in defaults)
            Register(t);
    }
}
