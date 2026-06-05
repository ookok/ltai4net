using System.ComponentModel;
using System.Text.Json;
using LTAI.Agent.Vector;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Tools;

public sealed class KnowledgeAssetTool
{
    private readonly KgStore _kg;
    private readonly ILogger<KnowledgeAssetTool> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    public KnowledgeAssetTool(KgStore kg, ILogger<KnowledgeAssetTool> logger)
    {
        _kg = kg;
        _logger = logger;
    }

    [Description("提交知识点到 Wiki。创建一个「wiki」类型节点，关联到已有文档或独立存在。\n"
        + "参数：title — 知识点标题；content — 知识点内容；source — 来源（可选）；tags — 标签列表逗号分隔（可选）。\n"
        + "返回：创建的知识点 ID 和确认信息。")]
    public async Task<string> WikiCommit(
        [Description("知识点标题（简短概括）")] string title,
        [Description("知识点详细内容（支持 Markdown）")] string content,
        [Description("来源（文件路径/URL/对话 ID，可选）")] string? source = null,
        [Description("标签（逗号分隔，可选）")] string? tags = null)
    {
        try
        {
            var extId = $"wiki:{title.GetHashCode():x}";
            var props = new Dictionary<string, object?>
            {
                ["content"] = content,
                ["tags"] = tags ?? "",
            };
            var nodeId = await _kg.UpsertNode(
                extId, "wiki", title, source: source, props: props).ConfigureAwait(false);

            if (source != null)
            {
                var srcNode = await _kg.GetNodeByExtId(source).ConfigureAwait(false);
                if (srcNode != null)
                    await _kg.AddEdge(srcNode.Id, nodeId, "REFERENCES", weight: 0.7).ConfigureAwait(false);
            }

            _logger.LogInformation("Wiki commit: {Title} (id={Id})", title, nodeId);
            return $"✅ Wiki 知识点「{title}」已提交 (ID: {nodeId})";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Wiki commit failed: {Title}", title);
            return $"❌ 提交失败: {ex.Message}";
        }
    }

    [Description("搜索 Wiki 知识点。支持按标题模糊搜索和按标签过滤。\n"
        + "参数：query — 搜索关键词；tag — 按标签过滤（可选）；topK — 最多返回数（默认 10）。\n"
        + "返回：匹配的知识点列表（标题 + 内容摘要 + 标签）。")]
    public async Task<string> WikiSearch(
        [Description("搜索关键词")] string query,
        [Description("按标签过滤（可选）")] string? tag = null,
        [Description("最多返回数（默认 10）")] int topK = 10)
    {
        try
        {
            var nodes = await _kg.SearchNodesByName(query, topK).ConfigureAwait(false);
            var wikiNodes = nodes.Where(n => n.Kind == "wiki").Take(topK).ToList();

            if (wikiNodes.Count == 0)
                return "未找到匹配的 Wiki 知识点";

            var lines = new List<string> { $"找到 {wikiNodes.Count} 个 Wiki 知识点：\n" };
            foreach (var node in wikiNodes)
            {
                var props = node.GetProps();
                var contentPreview = props?.GetValueOrDefault("content")?.ToString() ?? "";
                var nodeTags = props?.GetValueOrDefault("tags")?.ToString() ?? "";
                if (tag != null && !nodeTags.Contains(tag, StringComparison.OrdinalIgnoreCase))
                    continue;

                var preview = contentPreview.Length > 200
                    ? contentPreview[..200] + "..."
                    : contentPreview;
                lines.Add($"### [{node.Name}](id:{node.Id})");
                lines.Add($"标签：{nodeTags}");
                lines.Add($"来源：{node.Source ?? "-"}");
                lines.Add($"\n{preview}\n");
            }

            return string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            return $"❌ 搜索失败: {ex.Message}";
        }
    }

    [Description("列出所有 Wiki 知识点。可选择按标签过滤。\n"
        + "参数：tag — 标签过滤（可选）；topK — 最多返回数（默认 50）。\n"
        + "返回：知识点列表（标题 + 标签 + 创建时间）。")]
    public async Task<string> WikiList(
        [Description("按标签过滤（可选）")] string? tag = null,
        [Description("最多返回数（默认 50）")] int topK = 50)
    {
        try
        {
            var nodes = await _kg.GetNodesByKind("wiki").ConfigureAwait(false);
            var filtered = nodes.AsEnumerable();

            if (tag != null)
            {
                filtered = filtered.Where(n =>
                {
                    var props = n.GetProps();
                    return props?.GetValueOrDefault("tags")?.ToString()?.Contains(tag, StringComparison.OrdinalIgnoreCase) == true;
                });
            }

            var result = filtered.Take(topK).ToList();
            if (result.Count == 0)
                return "暂无 Wiki 知识点";

            var lines = new List<string> { $"共 {result.Count} 个 Wiki 知识点：\n" };
            foreach (var node in result)
            {
                var props = node.GetProps();
                var tags = props?.GetValueOrDefault("tags") ?? "-";
                lines.Add($"- **{node.Name}** (ID: {node.Id}) 标签: {tags}  [{node.UpdatedAt}]");
            }

            return string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            return $"❌ 查询失败: {ex.Message}";
        }
    }

    [Description("从指定来源提取结构化知识点并自动提交为 Wiki。\n"
        + "参数：content — 源内容；source — 来源标识；title — 可选标题（自动生成如不提供）。\n"
        + "返回：提取的知识点数量和确认信息。")]
    public async Task<string> WikiExtract(
        [Description("源内容（文本）")] string content,
        [Description("来源标识（文件路径/URL 等）")] string source,
        [Description("可选标题（自动生成为空）")] string? title = null)
    {
        try
        {
            title ??= $"从 {Path.GetFileName(source)} 提取的知识";
            if (string.IsNullOrWhiteSpace(content))
                return "❌ 内容为空";

            var preview = content.Length > 3000 ? content[..3000] + "..." : content;
            return await WikiCommit(title, preview, source, "auto-extracted").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return $"❌ 提取失败: {ex.Message}";
        }
    }
}
