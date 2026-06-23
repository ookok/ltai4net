using System.ComponentModel;
using LTAI.AI;
using LTAI.Agent.Memory;

namespace LTAI.Agent.Tools;

[ToolDomain("memory")]
public sealed class RefsSearchTool
{
    private readonly RefsSearchIndex? _index;

    public RefsSearchTool(RefsSearchIndex? index = null)
    {
        _index = index;
    }

    [Description("搜索历史 refs 内容（已压缩卸载到 .livingtree/refs/ 的旧消息和工具结果）。返回匹配的 refs 文件列表及内容摘要。")]
    [return: Description("匹配的 refs 搜索结果，每行一个文件：文件名 | 内容摘要 | 工具名 | 相关性分")]
    public async Task<string> SearchRefs(
        [Description("搜索关键词（FTS5 全文搜索语法）")] string query,
        [Description("返回结果数量上限")] int topK = 5)
    {
        if (_index == null) return "RefsSearchIndex not available";

        try
        {
            var results = await _index.SearchAsync(query, topK).ConfigureAwait(false);
            if (results.Count == 0) return "No matching refs found.";

            var lines = results.Select(r =>
                $"{r.Filename} | {r.ContentSnippet} | {r.ToolName} | score={r.Rank:F3}");
            return "## Refs Search Results\n\n" + string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            return $"Search failed: {ex.Message}";
        }
    }

    [Description("对 .livingtree/refs/ 目录重建全文搜索索引")]
    [return: Description("索引重建结果")]
    public async Task<string> RebuildIndex()
    {
        if (_index == null) return "RefsSearchIndex not available";
        await _index.IndexDirectoryAsync().ConfigureAwait(false);
        return "Refs index rebuilt.";
    }
}
