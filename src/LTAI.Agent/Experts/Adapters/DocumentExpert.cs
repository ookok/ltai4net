using LTAI.Agent.Formats;
using LTAI.Agent.Vector;

namespace LTAI.Agent.Experts.Adapters;

/// <summary>
/// Wraps document-oriented <see cref="KbGraph"/> queries as an <see cref="IExpertModule"/>.
/// For Phase 1 uses the unified KG store; Phase 4 can split into per-doc-type
/// experts (API docs, runbooks, design docs, FAQ) with separate indices.
/// </summary>
public sealed class DocumentExpert : IExpertModule
{
    private readonly KbGraph _kbGraph;
    private readonly string _expertId;
    private readonly string _capabilityDescription;
    private readonly IReadOnlyList<string> _tags;

    public string ExpertId => _expertId;
    public ExpertDomain Domain => ExpertDomain.Document;
    public string CapabilityDescription => _capabilityDescription;
    public IReadOnlyList<string> KnowledgeTags => _tags;
    public float MinConfidence => 0.20f; // Documents: fuzzy semantic matching, lower natural similarity

    public DocumentExpert(KbGraph kbGraph, string expertId, string capabilityDescription, string[] tags)
    {
        _kbGraph = kbGraph;
        _expertId = expertId;
        _capabilityDescription = capabilityDescription;
        _tags = tags;
    }

    public async Task<ExpertResponse> QueryAsync(ExpertQuery query, CancellationToken ct = default)
    {
        var results = await _kbGraph.QueryAsync(
            query.Query, topK: query.MaxResults, expandGraph: false,
            ct: ct, format: ResultFormat.Markdown).ConfigureAwait(false);

        if (results.Count == 0)
        {
            return new ExpertResponse(ExpertId, string.Empty, 0f,
                [], new ProvenanceInfo("kg.db", DateTime.UtcNow),
                NoAnswer: true, ClarifyQuestion: "未在文档中找到匹配内容。");
        }

        var citations = results.Select((r, i) =>
            new Citation($"doc-{i}", $"Document result {i + 1}", "kg.db", CitationType.Doc)).ToList();

        var content = string.Join("\n\n---\n\n", results);
        return new ExpertResponse(ExpertId, content, 0.75f, citations,
            new ProvenanceInfo("kg.db", DateTime.UtcNow));
    }

    public static DocumentExpert CreateApiDocExpert(KbGraph kbGraph) => new(kbGraph,
        "doc/api-expert",
        "API 文档专家：检索 API 使用说明、接口定义、参数文档。适用场景：API 调用方法/参数说明/返回值类型。",
        new[] { "api", "docs", "reference" });

    public static DocumentExpert CreateRunbookExpert(KbGraph kbGraph) => new(kbGraph,
        "doc/runbook-expert",
        "运维文档专家：检索 SOP、故障排查手册、部署文档。适用场景：运维流程/故障修复/部署步骤。",
        new[] { "runbook", "ops", "sop", "troubleshooting" });

    public static DocumentExpert CreateDesignDocExpert(KbGraph kbGraph) => new(kbGraph,
        "doc/design-expert",
        "设计文档专家：检索 ADR、技术方案、架构决策记录。适用场景：技术方案/架构设计/决策依据。",
        new[] { "design", "adr", "architecture", "decision" });
}
