using LTAI.Agent.Vector;

namespace LTAI.Agent.Experts.Adapters;

/// <summary>
/// Namespace-scoped code graph expert. Queries only the k-hop neighborhood
/// within a specific module/namespace, avoiding cross-module noise.
///
/// ExpertId format: "codegraph/{namespace}" (e.g. "codegraph/LTAI.Agent")
/// </summary>
public sealed class NamespacedCgGraphExpert : IExpertModule
{
    private readonly CgGraph _cgGraph;
    private readonly string _namespacePrefix;

    public string ExpertId { get; }
    public ExpertDomain Domain => ExpertDomain.CodeGraph;
    public string CapabilityDescription { get; }
    public IReadOnlyList<string> KnowledgeTags => new[] { "code", "callgraph", "symbols", _namespacePrefix };
    public float MinConfidence => 0.35f;

    public NamespacedCgGraphExpert(CgGraph cgGraph, string namespacePrefix)
    {
        _cgGraph = cgGraph;
        _namespacePrefix = namespacePrefix;
        ExpertId = $"codegraph/{namespacePrefix}";
        CapabilityDescription =
            $"代码图谱专家（{namespacePrefix} 模块）：支持调用链追踪、类型继承分析、符号定义查询。" +
            $"仅查询 {namespacePrefix} 及其子命名空间下的代码。适用场景：该模块内的 bug 定位/影响分析/API 用法。";
    }

    public async Task<ExpertResponse> QueryAsync(ExpertQuery query, CancellationToken ct = default)
    {
        var result = await _cgGraph.QueryByNamespaceAsync(
            query.Query, _namespacePrefix, topK: query.MaxResults, ct: ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(result))
        {
            return new ExpertResponse(ExpertId, string.Empty, 0f,
                [], new ProvenanceInfo("cg.db", DateTime.UtcNow),
                NoAnswer: true,
                ClarifyQuestion: $"在 {_namespacePrefix} 模块内未找到匹配的代码结构。");
        }

        var citations = new[] { new Citation($"cg-{_namespacePrefix}", $"Code ({_namespacePrefix})", "cg.db", CitationType.Code) };
        return new ExpertResponse(ExpertId, result, 0.80f, citations,
            new ProvenanceInfo("cg.db", DateTime.UtcNow));
    }
}
