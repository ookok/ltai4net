namespace LTAI.Capability.Skills;

public enum CapabilityBucket { Reasoning, Code, Document, Knowledge, Network, Tool, Evolution, Integration, Quality, System }

public enum SkillMaturity { Experimental, Stable, Core }

public record SkillEntry(string ModuleName, CapabilityBucket Bucket, string Description,
    List<string> Keywords, SkillMaturity Maturity, List<string> Dependencies, bool EnabledByDefault);

public sealed class SkillCatalog
{
    private readonly Dictionary<CapabilityBucket, List<SkillEntry>> _buckets = new();
    private readonly Dictionary<string, SkillEntry> _index = new();

    public SkillCatalog()
    {
        InitializeDefaults();
    }

    private void InitializeDefaults()
    {
        var entries = new List<SkillEntry>
        {
            new("math_reasoner", CapabilityBucket.Reasoning, "Mathematical problem solving", new(){"math","计算","求解","方程"}, SkillMaturity.Core, new(), true),
            new("logic_engine", CapabilityBucket.Reasoning, "Formal logic reasoning", new(){"logic","逻辑","推理","if-then"}, SkillMaturity.Core, new(), true),
            new("dialectical_reasoner", CapabilityBucket.Reasoning, "Dialectical analysis", new(){"辩证","正反合","thesis","论题"}, SkillMaturity.Core, new(), true),
            new("code_analyzer", CapabilityBucket.Code, "Multi-language code analysis", new(){"code","代码","analyze","检查"}, SkillMaturity.Core, new(), true),
            new("code_review", CapabilityBucket.Code, "Git-based code review", new(){"review","审查","diff","git"}, SkillMaturity.Stable, new(){"code_analyzer"}, true),
            new("doc_processor", CapabilityBucket.Document, "Document text extraction", new(){"doc","文档","pdf","word","excel"}, SkillMaturity.Core, new(), true),
            new("doc_engine", CapabilityBucket.Document, "EIA document generation", new(){"报告","环评","generate","template"}, SkillMaturity.Stable, new(){"doc_processor"}, true),
            new("knowledge_search", CapabilityBucket.Knowledge, "Knowledge base search", new(){"knowledge","知识","search","检索"}, SkillMaturity.Core, new(), true),
            new("knowledge_graph", CapabilityBucket.Knowledge, "Knowledge graph query", new(){"图谱","graph","entity","关系"}, SkillMaturity.Stable, new(){"knowledge_search"}, true),
            new("web_search", CapabilityBucket.Network, "Web search engine", new(){"search","搜索","web","internet"}, SkillMaturity.Core, new(), true),
            new("browser_agent", CapabilityBucket.Network, "Browser automation", new(){"browser","浏览器","爬取","screenshot"}, SkillMaturity.Stable, new(){"web_search"}, true),
            new("tool_synthesis", CapabilityBucket.Tool, "LLM tool synthesis", new(){"tool","工具","synthesize","生成"}, SkillMaturity.Experimental, new(), true),
            new("light_crawler", CapabilityBucket.Network, "Lightweight web crawler", new(){"crawl","爬虫","spider","抓取"}, SkillMaturity.Stable, new(){"web_search"}, true),
            new("self_evolution", CapabilityBucket.Evolution, "Self-evolution engine", new(){"evolve","进化","self","自改进"}, SkillMaturity.Experimental, new(){"code_analyzer","code_review"}, true),
            new("knowledge_forager", CapabilityBucket.Knowledge, "Autonomous knowledge gathering", new(){"forage","觅食","patrol","巡逻"}, SkillMaturity.Experimental, new(){"knowledge_search","browser_agent"}, true),
            new("telegram_notify", CapabilityBucket.Integration, "Telegram notification", new(){"telegram","通知","bot"}, SkillMaturity.Stable, new(), true),
            new("wework_notify", CapabilityBucket.Integration, "WeChat Work notification", new(){"微信","企业微信","wework","通知"}, SkillMaturity.Stable, new(), true),
            new("quality_checker", CapabilityBucket.Quality, "Response quality check", new(){"quality","质量","check","审查"}, SkillMaturity.Stable, new(), true),
            new("system_health", CapabilityBucket.System, "System health monitoring", new(){"health","健康","status","监控"}, SkillMaturity.Stable, new(), true),
        };

        foreach (var entry in entries) AddEntry(entry);
    }

    private void AddEntry(SkillEntry entry)
    {
        if (!_buckets.ContainsKey(entry.Bucket)) _buckets[entry.Bucket] = new List<SkillEntry>();
        _buckets[entry.Bucket].Add(entry);
        _index[entry.ModuleName] = entry;
    }

    public List<SkillEntry> GetBucket(CapabilityBucket bucket) =>
        _buckets.GetValueOrDefault(bucket) ?? new List<SkillEntry>();

    public SkillEntry? GetSkill(string moduleName) =>
        _index.GetValueOrDefault(moduleName);

    public List<SkillEntry> Search(string query)
    {
        var lower = query.ToLowerInvariant();
        return _index.Values
            .Where(e => e.ModuleName.Contains(lower, StringComparison.OrdinalIgnoreCase) ||
                        e.Description.Contains(lower, StringComparison.OrdinalIgnoreCase) ||
                        e.Keywords.Any(k => k.Contains(lower, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(e => e.Maturity == SkillMaturity.Core ? 3 :
                                    e.Maturity == SkillMaturity.Stable ? 2 : 1)
            .ToList();
    }

    public List<(CapabilityBucket Bucket, double Score)> GetRoutingPriority(string taskDescription)
    {
        var lower = taskDescription.ToLowerInvariant();
        var scores = new Dictionary<CapabilityBucket, double>();
        var keywords = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Concat(lower.Split('\n'))
            .Distinct()
            .ToList();

        foreach (var (bucket, entries) in _buckets)
        {
            var score = 0.0;
            foreach (var entry in entries)
            {
                if (entry.Keywords.Any(k => lower.Contains(k, StringComparison.OrdinalIgnoreCase)))
                    score += 2.0;
                if (keywords.Any(k => entry.ModuleName.Contains(k, StringComparison.OrdinalIgnoreCase)))
                    score += 1.0;
            }
            if (score > 0) scores[bucket] = score;
        }

        return scores.OrderByDescending(s => s.Value).Select(s => (s.Key, s.Value)).ToList();
    }

    public List<SkillEntry> SuggestSkills(string task, int topK = 5)
    {
        var lower = task.ToLowerInvariant();
        return _index.Values
            .Select(e => new { Entry = e, Score = e.Keywords.Sum(k => lower.Contains(k, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0) +
                                                 (e.Description.Contains(lower, StringComparison.OrdinalIgnoreCase) ? 0.5 : 0) })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .Select(x => x.Entry)
            .ToList();
    }

    public Dictionary<string, int> GetBucketSummary()
    {
        return _buckets.ToDictionary(k => k.Key.ToString(), v => v.Value.Count);
    }

    public Dictionary<string, object> ExportManifest()
    {
        return new Dictionary<string, object>
        {
            ["total_skills"] = _index.Count,
            ["buckets"] = _buckets.ToDictionary(
                k => k.Key.ToString().ToLowerInvariant(),
                v => (object)v.Value.Select(e => new {
                    name = e.ModuleName,
                    maturity = e.Maturity.ToString().ToLowerInvariant(),
                    enabled = e.EnabledByDefault
                }).ToList()
            )
        };
    }
}
