using LTAI.Agent.Vector;

namespace LTAI.Agent.Experts.Adapters;

/// <summary>
/// Wraps <see cref="CgGraph"/> (TreeSitter-based code graph) as an <see cref="IExpertModule"/>.
/// Supports call-chain traversal, type inheritance, and symbol lookups.
/// Returns NoAnswer if the code graph has not been built yet.
/// </summary>
public sealed class CgGraphExpert : IExpertModule
{
    private readonly CgGraph _cgGraph;

    public string ExpertId => "codegraph/expert";
    public ExpertDomain Domain => ExpertDomain.CodeGraph;
    public string CapabilityDescription =>
        "代码图谱专家：支持代码调用链追踪、类型继承分析、符号定义查询。" +
        "按 namespace/module 组织索引，支持 12+ 语言。适用场景：bug 定位/影响分析/API 用法/依赖链。";
    public IReadOnlyList<string> KnowledgeTags => new[] { "code", "callgraph", "symbols", "dependency", "type" };
    public float MinConfidence => 0.35f;

    public CgGraphExpert(CgGraph cgGraph)
    {
        _cgGraph = cgGraph;
    }

    public async Task<ExpertResponse> QueryAsync(ExpertQuery query, CancellationToken ct = default)
    {
        var result = await _cgGraph.QueryAsync(query.Query, topK: query.MaxResults, ct: ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(result))
        {
            return new ExpertResponse(ExpertId, string.Empty, 0f,
                [], new ProvenanceInfo("cg.db", DateTime.UtcNow),
                NoAnswer: true, ClarifyQuestion: "代码图谱未构建或无匹配的代码结构。请先执行代码索引。");
        }

        var citations = new[] { new Citation("cg-0", "Code graph result", "cg.db", CitationType.Code) };
        return new ExpertResponse(ExpertId, result, 0.80f, citations,
            new ProvenanceInfo("cg.db", DateTime.UtcNow));
    }
}
