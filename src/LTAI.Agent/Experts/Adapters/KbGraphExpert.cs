using LTAI.Agent.Formats;
using LTAI.Agent.Vector;
using LTAI.Agent.Vector.GraphRAG;

namespace LTAI.Agent.Experts.Adapters;

/// <summary>
/// Wraps <see cref="KbGraph"/> (SQLite FTS5 + CTE BFS) as an <see cref="IExpertModule"/>.
/// Handles entity-linked subgraph queries and returns structured triples.
/// </summary>
public sealed class KbGraphExpert : IExpertModule
{
    private readonly KbGraph _kbGraph;
    private readonly EntityLinker _entityLinker;
    private readonly SubgraphExtractor _subgraphExtractor;

    public string ExpertId => "kg/expert";
    public ExpertDomain Domain => ExpertDomain.KG;
    public string CapabilityDescription =>
        "知识图谱专家：支持实体关系查询、路径推理、属性聚合、BFS 子图遍历（2-hop）。" +
        "覆盖实体、概念、事实、文档关系。适用场景：谁是/什么关系/依赖链/时间序列事件。";
    public IReadOnlyList<string> KnowledgeTags => new[] { "entity", "relation", "graph", "knowledge", "facts" };
    public float MinConfidence => 0.30f; // KG: entity/relation queries produce medium similarity

    public KbGraphExpert(KbGraph kbGraph, KgStore kgStore)
    {
        _kbGraph = kbGraph;
        _entityLinker = new EntityLinker(kgStore);
        _subgraphExtractor = new SubgraphExtractor(kgStore);
    }

    public async Task<ExpertResponse> QueryAsync(ExpertQuery query, CancellationToken ct = default)
    {
        var results = await _kbGraph.QueryAsync(
            query.Query, topK: query.MaxResults, expandGraph: true,
            ct: ct, format: ResultFormat.Markdown).ConfigureAwait(false);

        if (results.Count == 0)
        {
            return new ExpertResponse(ExpertId, string.Empty, 0f,
                [], new ProvenanceInfo("kg.db", DateTime.UtcNow),
                NoAnswer: true, ClarifyQuestion: "未在知识图谱中找到匹配结果，请提供更具体的关键词。");
        }

        var citations = results.Select((r, i) =>
            new Citation($"kg-{i}", $"KG result {i + 1}", "kg.db", CitationType.Fact)).ToList();

        var content = string.Join("\n\n---\n\n", results);
        return new ExpertResponse(ExpertId, content, 0.85f, citations,
            new ProvenanceInfo("kg.db", DateTime.UtcNow));
    }
}
