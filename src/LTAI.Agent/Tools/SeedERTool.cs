using System.ComponentModel;
using LTAI.AI;
using LTAI.Agent.Formats;
using LTAI.Agent.SeedER;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Tools;

/// <summary>
/// SeedER tool for agents: performs structured knowledge graph exploration
/// by following entity-relation paths instead of flat similarity search.
/// Ideal for multi-hop reasoning, traceable evidence chains, and "how does X connect to Y" questions.
/// </summary>
[ToolDomain("knowledge")]
public sealed class SeedERTool
{
    private readonly SeedER.SeedER _seeder;
    private readonly IChatClient _llm;
    private readonly ILogger<SeedERTool>? _logger;

    public SeedERTool(SeedER.SeedER seeder, IChatClient llm,
        ILogger<SeedERTool>? logger = null)
    {
        _seeder = seeder ?? throw new ArgumentNullException(nameof(seeder));
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _logger = logger;
    }

    [Description("结构化知识图谱探索。沿着实体关系路径进行多跳推理，发现查询主题与相关知识之间的连接链路。适合\"A和B有什么关系\"、\"解释一下X如何影响Y\"这类需要理解结构连接的问题。返回的每条路径都包含完整的关系链和置信度评分。")]
    [ToolExample("依赖注入容器是如何创建Controller实例的？")]
    [ToolExample("Explain how middleware pipeline processes requests in ASP.NET Core")]
    public async Task<string> ExploreAsync(
        [Description("探索查询，描述你想要了解的主题或问题")] string query,
        [Description("探索深度（1-5），越深的路径越能发现间接关系，但可能会引入噪音。默认3")] int maxDepth = 3,
        [Description("最大返回路径数。默认10")] int maxPaths = 10,
        [Description("限制关系类型（逗号分隔），如 \"calls,contains,depends_on\"。为空表示不限")] string? relations = null,
        [Description("排除的关系类型（逗号分隔），如 \"mentions,references\"")] string? excludeRelations = null,
        CancellationToken ct = default)
    {
        maxDepth = Math.Clamp(maxDepth, 1, 5);
        maxPaths = Math.Clamp(maxPaths, 1, 100);

        HashSet<string>? includeRels = null;
        if (!string.IsNullOrWhiteSpace(relations))
            includeRels = [.. relations.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];

        HashSet<string>? excludeRels = null;
        if (!string.IsNullOrWhiteSpace(excludeRelations))
            excludeRels = [.. excludeRelations.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];

        _logger?.LogInformation("SeedERTool: query=\"{Q}\" depth={D} paths={P}", query, maxDepth, maxPaths);

        var result = await _seeder.ExploreAsync(
            query,
            maxDepth: maxDepth,
            maxPaths: maxPaths,
            includeRelations: includeRels,
            excludeRelations: excludeRels,
            enableReasoning: true,
            ct: ct).ConfigureAwait(false);

        // Auto-format: TOON for LLM consumption (large / structured), Markdown for small / human-readable
        var autoFmt = result.PathsExplored > 5 || result.ReasoningPaths.Count > 3
            ? ResultFormat.Toon : ResultFormat.Markdown;

        return result.ToFullReport(autoFmt);
    }
}
