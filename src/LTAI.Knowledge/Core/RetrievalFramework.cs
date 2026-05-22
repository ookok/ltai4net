using System.Text.RegularExpressions;

namespace LTAI.Knowledge.Core;

public enum QueryShape { ExactLookup, PolicyVersioned, SemanticConcept, MultiHop, ComparativeAnalysis,
    TemporalQuery, SpatialQuery, NumericCalculation, AggregationSummary, ProceduralHowTo, Unknown }

public sealed class RetrievalStrategy
{
    public QueryShape Shape { get; set; }
    public string Method { get; set; } = "";
    public string Fallback { get; set; } = "";
    public int TopK { get; set; } = 5;
    public double Threshold { get; set; } = 0.3;
    public string Warning { get; set; } = "";
    public string? SystemPromptHint { get; set; }
}

public sealed class RetrievalFramework
{
    private static readonly Lazy<RetrievalFramework> _instance = new(() => new RetrievalFramework());
    public static RetrievalFramework Instance => _instance.Value;

    private readonly Dictionary<QueryShape, RetrievalStrategy> _strategies = new()
    {
        [QueryShape.ExactLookup] = new() { Shape = QueryShape.ExactLookup, Method = "fts5", Fallback = "vector", TopK = 3, Threshold = 0.8, Warning = "Exact match only; fall back to semantic if not found", SystemPromptHint = "Search exact terms only" },
        [QueryShape.PolicyVersioned] = new() { Shape = QueryShape.PolicyVersioned, Method = "fts5+filter", Fallback = "vector+filter", TopK = 5, Threshold = 0.6, Warning = "Check effective date of policies", SystemPromptHint = "Include effective dates and version numbers" },
        [QueryShape.SemanticConcept] = new() { Shape = QueryShape.SemanticConcept, Method = "vector", Fallback = "fts5", TopK = 10, Threshold = 0.3, Warning = "Broad semantic search", SystemPromptHint = "Use semantic similarity" },
        [QueryShape.MultiHop] = new() { Shape = QueryShape.MultiHop, Method = "iterative", Fallback = "kg+vector", TopK = 8, Threshold = 0.2, Warning = "Multi-step reasoning required", SystemPromptHint = "Chain multiple retrieval steps" },
        [QueryShape.ComparativeAnalysis] = new() { Shape = QueryShape.ComparativeAnalysis, Method = "parallel", Fallback = "sequential", TopK = 10, Threshold = 0.3, Warning = "Compare across sources", SystemPromptHint = "Retrieve from multiple perspectives" },
        [QueryShape.TemporalQuery] = new() { Shape = QueryShape.TemporalQuery, Method = "filtered", Fallback = "vector", TopK = 8, Threshold = 0.4, Warning = "Time-sensitive query", SystemPromptHint = "Prioritize recent information" },
        [QueryShape.SpatialQuery] = new() { Shape = QueryShape.SpatialQuery, Method = "geospatial", Fallback = "vector", TopK = 5, Threshold = 0.4, Warning = "Spatial data needed", SystemPromptHint = "Include location context" },
        [QueryShape.NumericCalculation] = new() { Shape = QueryShape.NumericCalculation, Method = "structured", Fallback = "vector", TopK = 5, Threshold = 0.5, Warning = "Requires precise numeric data", SystemPromptHint = "Focus on tables and structured data" },
        [QueryShape.AggregationSummary] = new() { Shape = QueryShape.AggregationSummary, Method = "batch", Fallback = "iterative", TopK = 20, Threshold = 0.2, Warning = "Large-scale retrieval", SystemPromptHint = "Summarize across documents" },
        [QueryShape.ProceduralHowTo] = new() { Shape = QueryShape.ProceduralHowTo, Method = "structured", Fallback = "vector", TopK = 5, Threshold = 0.4, Warning = "Step-by-step instructions", SystemPromptHint = "Prioritize sequential/ordered content" },
        [QueryShape.Unknown] = new() { Shape = QueryShape.Unknown, Method = "hybrid", Fallback = "vector", TopK = 10, Threshold = 0.3, Warning = "General query", SystemPromptHint = null },
    };

    private readonly Dictionary<QueryShape, List<string>> _outcomes = new();
    private readonly Dictionary<QueryShape, int> _counts = new();

    private RetrievalFramework() { }

    public QueryShape Classify(string query)
    {
        var q = query.ToLower();

        if (Regex.IsMatch(q, @"\b(GB|HJ|ISO|标准)\d")) return QueryShape.PolicyVersioned;
        if (Regex.IsMatch(q, @"\b(什么\s*是|定义|概念|含义|what\s+is|define)")) return QueryShape.ExactLookup;
        if (Regex.IsMatch(q, @"\b(比较|对比|vs|versus|相比|区别|compare|difference)")) return QueryShape.ComparativeAnalysis;
        if (Regex.IsMatch(q, @"\b(如何|怎么|how\s+to|步骤|step|procedure)")) return QueryShape.ProceduralHowTo;
        if (Regex.IsMatch(q, @"\b(多少|数值|计算|calculate|compute|formula|公式)")) return QueryShape.NumericCalculation;
        if (Regex.IsMatch(q, @"\b(总结|汇总|概述|summary|overview|statistics)")) return QueryShape.AggregationSummary;
        if (Regex.IsMatch(q, @"\b(附近|位置|坐标|在哪|where|location|坐标|缓冲区)")) return QueryShape.SpatialQuery;
        if (Regex.IsMatch(q, @"\b(历史|过去|去年|上个|previous|history|trend|变化趋势)")) return QueryShape.TemporalQuery;
        if (Regex.IsMatch(q, @"\b(然后|接着|导致|影响|chain|relate|connect|after)")) return QueryShape.MultiHop;
        if (Regex.IsMatch(q, @"\b(什么|哪些|类型|分类|list|所有)")) return QueryShape.AggregationSummary;

        return QueryShape.SemanticConcept;
    }

    public RetrievalStrategy GetStrategy(string query)
    {
        var shape = Classify(query);
        return _strategies.GetValueOrDefault(shape, _strategies[QueryShape.Unknown]);
    }

    public void RecordOutcome(string query, string method, bool success, double latencyMs)
    {
        var shape = Classify(query);
        lock (_outcomes)
        {
            if (!_outcomes.ContainsKey(shape)) _outcomes[shape] = new();
            _outcomes[shape].Add(success ? "success" : $"fail:{method}");
            _counts[shape] = _counts.GetValueOrDefault(shape) + 1;
        }
    }

    public Dictionary<string, object> GetStats()
    {
        var stats = new Dictionary<string, object>();
        foreach (var shape in Enum.GetValues<QueryShape>())
        {
            var c = _counts.GetValueOrDefault(shape);
            var outcomes = _outcomes.GetValueOrDefault(shape) ?? new();
            var successRate = c > 0 ? (double)outcomes.Count(o => o == "success") / c : 0;
            stats[shape.ToString()] = new { count = c, success_rate = Math.Round(successRate, 2), strategy = _strategies[shape].Method };
        }
        return stats;
    }

    public static RetrievalStrategy GetForShape(QueryShape shape) => Instance._strategies[shape];
}
