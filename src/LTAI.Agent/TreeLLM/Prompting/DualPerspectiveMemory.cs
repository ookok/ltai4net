using LTAI.Knowledge.Core;
using LTAI.Knowledge.Core.Models;

namespace LTAI.Agent.Prompting;

public sealed class DualPerspectiveMemory
{
    private readonly AgenticRAG _agenticRAG;
    private readonly StructMemory _structMemory;

    private static readonly Dictionary<string, double> SelfPerspectiveWeights = new()
    {
        ["personal_preference"] = 1.5,
        ["habit_signal"] = 1.3,
        ["domain_knowledge"] = 1.0,
        ["factual_knowledge"] = 0.8,
        ["general_knowledge"] = 0.5
    };

    private static readonly Dictionary<string, double> ThirdPartyPerspectiveWeights = new()
    {
        ["personal_preference"] = 0.3,
        ["habit_signal"] = 0.3,
        ["domain_knowledge"] = 0.8,
        ["factual_knowledge"] = 1.3,
        ["general_knowledge"] = 0.9
    };

    public DualPerspectiveMemory(AgenticRAG agenticRAG, StructMemory structMemory)
    {
        _agenticRAG = agenticRAG;
        _structMemory = structMemory;
    }

    public async Task<PerspectiveResult> RetrieveSelfPerspective(
        string sessionId, string query,
        PerspectiveOptions? options = null)
    {
        var opts = options ?? new PerspectiveOptions();

        var memoryEvents = await RetrieveMemoryEvents(sessionId, query, opts).ConfigureAwait(false);
        var knowledgeDocs = _agenticRAG.Search(query, RAGMode.Iterative,
            domain: opts.Domain ?? "personal_preference");

        var reweightedDocs = ReweightDocs(knowledgeDocs, SelfPerspectiveWeights);
        var reweightedMemory = ReweightMemoryEvents(memoryEvents, SelfPerspectiveWeights);

        return new PerspectiveResult(
            PerspectiveMode.Self,
            query,
            reweightedDocs.OrderByDescending(d => d.Score).Take(opts.MaxResults).ToList(),
            reweightedMemory.OrderByDescending(e => e.Item2).Take(opts.MaxMemoryEvents).ToList(),
            GenerateSelfPerspectiveSummary(reweightedDocs, reweightedMemory, query));
    }

    public async Task<PerspectiveResult> RetrieveThirdPartyPerspective(
        string sessionId, string query,
        PerspectiveOptions? options = null)
    {
        var opts = options ?? new PerspectiveOptions();

        var memoryEvents = await RetrieveMemoryEvents(sessionId, query, opts).ConfigureAwait(false);
        var knowledgeDocs = _agenticRAG.Search(query, RAGMode.Iterative,
            domain: opts.Domain ?? "general_knowledge");

        var reweightedDocs = ReweightDocs(knowledgeDocs, ThirdPartyPerspectiveWeights);
        var reweightedMemory = ReweightMemoryEvents(memoryEvents, ThirdPartyPerspectiveWeights);

        return new PerspectiveResult(
            PerspectiveMode.ThirdParty,
            query,
            reweightedDocs.OrderByDescending(d => d.Score).Take(opts.MaxResults).ToList(),
            reweightedMemory.OrderByDescending(e => e.Item2).Take(opts.MaxMemoryEvents).ToList(),
            GenerateThirdPartyPerspectiveSummary(reweightedDocs, reweightedMemory, query));
    }

    public async Task<DualPerspectiveResult> RetrieveDualPerspective(
        string sessionId, string query,
        PerspectiveOptions? options = null)
    {
        var self = await RetrieveSelfPerspective(sessionId, query, options).ConfigureAwait(false);
        var thirdParty = await RetrieveThirdPartyPerspective(sessionId, query, options).ConfigureAwait(false);

        return new DualPerspectiveResult
        {
            Query = query,
            SelfPerspective = self,
            ThirdPartyPerspective = thirdParty,
            DivergenceScore = ComputePerspectiveDivergence(self, thirdParty)
        };
    }

    private async Task<List<EventEntry>> RetrieveMemoryEvents(
        string sessionId, string query, PerspectiveOptions opts)
    {
        try
        {
            var result = await _structMemory.RetrieveForQuery(query).ConfigureAwait(false);
            return result.Events?.Take(opts.MaxMemoryEvents * 2).ToList() ?? new();
        }
        catch
        {
            return new();
        }
    }

    private static List<KnowledgeSearchResult> ReweightDocs(
        List<KnowledgeSearchResult> docs,
        Dictionary<string, double> weights)
    {
        return docs.Select(d =>
        {
            var weight = weights.GetValueOrDefault(d.Domain ?? "general_knowledge", 0.5);
            return new KnowledgeSearchResult
            {
                Id = d.Id,
                Title = d.Title,
                Content = d.Content,
                Domain = d.Domain ?? string.Empty,
                Score = d.Score * weight,
                Source = d.Source
            };
        }).Where(d => d.Score > 0.05).ToList();
    }

    private static List<(EventEntry Entry, double Score)> ReweightMemoryEvents(
        List<EventEntry> events, Dictionary<string, double> weights)
    {
        return events.Select(e =>
        {
            var domainWeight = weights.GetValueOrDefault(e.PersonaDomain ?? "general_knowledge", 0.5);
            var emotionalAdjust = 1.0 + Math.Abs(e.EmotionalValence) * 0.3;
            var score = domainWeight * emotionalAdjust;
            return (e, score);
        }).Where(t => t.score > 0).ToList();
    }

    private static string GenerateSelfPerspectiveSummary(
        List<KnowledgeSearchResult> docs,
        List<(EventEntry Entry, double Score)> memoryEvents,
        string query)
    {
        var parts = new List<string>();
        parts.Add("## 自我视角 (Self Perspective)");
        parts.Add("此视角聚焦于用户的个人身份、偏好和历史行为模式。");
        parts.Add($"查询: {query}");
        parts.Add("");

        if (memoryEvents.Count > 0)
        {
            parts.Add("### 相关个人记忆");
            foreach (var (evt, score) in memoryEvents.Take(3))
            {
                var content = evt.TextForRetrieval();
                if (content.Length > 100) content = content[..100] + "...";
                parts.Add($"- [{evt.Role}] (相关度:{score:F2}) {content}");
            }
            parts.Add("");
        }

        if (docs.Count > 0)
        {
            parts.Add("### 偏好相关文档");
            foreach (var doc in docs.Take(3))
            {
                var summary = PromptBuilder.HeuristicSummarize(doc.Content, 150);
                parts.Add($"- (相关度:{doc.Score:F2}) {summary}");
            }
        }

        return string.Join("\n", parts);
    }

    private static string GenerateThirdPartyPerspectiveSummary(
        List<KnowledgeSearchResult> docs,
        List<(EventEntry Entry, double Score)> memoryEvents,
        string query)
    {
        var parts = new List<string>();
        parts.Add("## 第三方视角 (Third-Party Perspective)");
        parts.Add("此视角聚焦于客观事实和领域知识，过滤个人偏好。");
        parts.Add($"查询: {query}");
        parts.Add("");

        if (docs.Count > 0)
        {
            parts.Add("### 客观知识文档");
            foreach (var doc in docs.Take(3))
            {
                var summary = PromptBuilder.HeuristicSummarize(doc.Content, 150);
                parts.Add($"- (相关度:{doc.Score:F2}) {summary}");
            }
            parts.Add("");
        }

        if (memoryEvents.Count > 0)
        {
            parts.Add("### 可公开的事件记忆");
            foreach (var (evt, score) in memoryEvents
                .Where(e => Math.Abs(e.Entry.EmotionalValence) < 0.5)
                .Take(2))
            {
                var content = evt.TextForRetrieval();
                if (content.Length > 100) content = content[..100] + "...";
                parts.Add($"- [{evt.Role}] {content}");
            }
        }

        return string.Join("\n", parts);
    }

    private static double ComputePerspectiveDivergence(
        PerspectiveResult self, PerspectiveResult thirdParty)
    {
        var selfSet = new HashSet<string>(self.Docs.Select(d => d.Id));
        var tpSet = new HashSet<string>(thirdParty.Docs.Select(d => d.Id));
        var intersection = selfSet.Intersect(tpSet).Count();
        var union = selfSet.Union(tpSet).Count();

        if (union == 0) return 0;

        var jaccard = (double)intersection / union;

        var selfAvgScore = self.Docs.Count > 0 ? self.Docs.Average(d => d.Score) : 0;
        var tpAvgScore = thirdParty.Docs.Count > 0 ? thirdParty.Docs.Average(d => d.Score) : 0;

        var scoreRatio = Math.Abs(selfAvgScore - tpAvgScore);

        return (1.0 - jaccard) * 0.6 + scoreRatio * 0.4;
    }
}

public enum PerspectiveMode
{
    Self,
    ThirdParty
}

public sealed class PerspectiveOptions
{
    public string? Domain { get; set; }
    public int MaxResults { get; set; } = 10;
    public int MaxMemoryEvents { get; set; } = 8;
}

public sealed record PerspectiveResult(
    PerspectiveMode Mode,
    string Query,
    List<KnowledgeSearchResult> Docs,
    List<(EventEntry Entry, double Score)> MemoryEvents,
    string Summary)
{
    public double AvgDocScore => Docs.Count > 0 ? Docs.Average(d => d.Score) : 0;
    public int TotalEvidenceCount => Docs.Count + MemoryEvents.Count;
}

public sealed class DualPerspectiveResult
{
    public string Query { get; set; } = "";
    public PerspectiveResult SelfPerspective { get; set; } = new(
        PerspectiveMode.Self, "", new(), new(), "");
    public PerspectiveResult ThirdPartyPerspective { get; set; } = new(
        PerspectiveMode.ThirdParty, "", new(), new(), "");
    public double DivergenceScore { get; set; }

    public bool HighlyDivergent => DivergenceScore > 0.6;
    public bool ModeratelyDivergent => DivergenceScore is > 0.3 and <= 0.6;
    public bool LowDivergence => DivergenceScore <= 0.3;
}
