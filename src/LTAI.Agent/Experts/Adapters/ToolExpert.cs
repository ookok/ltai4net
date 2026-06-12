using LTAI.AI;

namespace LTAI.Agent.Experts.Adapters;

/// <summary>
/// Wraps <see cref="ToolRegistry"/> as an <see cref="IExpertModule"/>.
/// Returns tool capability metadata — the Router uses this to decide
/// which tools to expose based on the user's intent.
/// </summary>
public sealed class ToolExpert : IExpertModule
{
    private readonly EmbeddingClient _embedder;

    public string ExpertId => "tool/expert";
    public ExpertDomain Domain => ExpertDomain.Tool;
    public string CapabilityDescription =>
        "工具专家：匹配可用的工具能力。支持文件操作/代码分析/Git/Shell/Docker/" +
        "Office 文档/数据库/Web 搜索/图表生成/系统监控等 18 个域。适用场景：需要执行操作/运行命令/查询数据。";
    public IReadOnlyList<string> KnowledgeTags => new[] { "tool", "execute", "action", "command" };
    public float MinConfidence => 0.30f; // Tools: structured pattern matching, medium-high similarity

    public ToolExpert(EmbeddingClient embedder)
    {
        _embedder = embedder;
    }

    public async Task<ExpertResponse> QueryAsync(ExpertQuery query, CancellationToken ct = default)
    {
        if (!ToolRegistry.IsInitialized)
        {
            return new ExpertResponse(ExpertId, string.Empty, 0f,
                [], new ProvenanceInfo("ToolRegistry", null),
                NoAnswer: true, ClarifyQuestion: "工具注册表未初始化。");
        }

        var results = await ToolRegistry.SearchTopKAsync(
            query.Query, _embedder, k: query.MaxResults * 2, ct: ct).ConfigureAwait(false);

        if (results.Count == 0)
        {
            return new ExpertResponse(ExpertId, string.Empty, 0f,
                [], new ProvenanceInfo("ToolRegistry", DateTime.UtcNow),
                NoAnswer: true, ClarifyQuestion: "未找到匹配的工具。");
        }

        var lines = results.Select(r =>
        {
            var desc = r.Description.Length > 200 ? r.Description[..200] : r.Description;
            return $"- **{r.Name}** ({r.Domain}): {desc}";
        });
        var content = "## Matching Tools\n\n" + string.Join('\n', lines);

        var citations = results.Select((r, i) =>
            new Citation($"tool-{i}", r.Name, r.Domain, CitationType.ToolResult, 0.7f)).ToList();

        return new ExpertResponse(ExpertId, content, 0.70f, citations,
            new ProvenanceInfo("ToolRegistry", DateTime.UtcNow));
    }
}
