using System;
using System.ComponentModel;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LTAI.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Tools;

[ToolDomain("knowledge")]
public sealed class ClusterSummarizer
{
    private readonly IChatClient _llm;
    private readonly ILogger<ClusterSummarizer>? _logger;

    public ClusterSummarizer(IChatClient llm, ILogger<ClusterSummarizer>? logger = null)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _logger = logger;
    }

    [Description("将多条检索结果按主题聚类并生成摘要。当你获取到多条搜索/检索结果后，调用此工具可以让 LLM 先按主题分组、每组写一段摘要，使最终回答更有条理。适用于：知识图谱检索结果、代码搜索结果、文档片段、网页搜索结果等。")]
    [ToolExample("帮我总结一下关于性能优化的搜索结果")]
    [ToolExample("将这篇文章的知识点分主题整理")]
    public async Task<string> SummarizeAsync(
        [Description("用户的原始问题，用于辅助聚类")] string query,
        [Description("待聚类的检索结果文本。每条占一行，包含知识点/文档摘要/代码片段等。至少 2 条才有聚类意义。")] string items,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(items))
            return "没有提供待聚类的内容。";

        var lines = items.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        _logger?.LogInformation("ClusterSummarizer: clustering {Count} items for query \"{Q}\"",
            lines.Length, query);

        var prompt = BuildPrompt(query, items);
        var response = await _llm.GetResponseAsync([
            new ChatMessage(ChatRole.System, """
            你是一个知识组织助手。将检索结果按主题聚类，每组起一个标题并写一段摘要。
            - 按语义主题自然分组，不要强行拆散相关结果
            - 每组以 Markdown 三级标题开头（### 主题名）
            - 主题名简洁准确（2-5 个字）
            - 每组写 2-5 句话的摘要，概括该组核心内容
            - 如果所有结果属于同一主题，只需写一个组
            - 不相关的单条结果可单独成组
            """),
            new ChatMessage(ChatRole.User, prompt)
        ], cancellationToken: ct);

        var result = response.Text?.Trim();
        if (string.IsNullOrEmpty(result))
        {
            _logger?.LogWarning("ClusterSummarizer: LLM returned empty result");
            return "聚类摘要生成失败。";
        }

        _logger?.LogInformation("ClusterSummarizer: generated {Length} chars", result.Length);
        return result;
    }

    private static string BuildPrompt(string query, string items)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 原始查询");
        sb.AppendLine(query);
        sb.AppendLine();
        sb.AppendLine("## 检索结果");
        sb.AppendLine(items);
        sb.AppendLine();
        sb.AppendLine("请将以上检索结果按主题分组，每组写一段摘要。");
        return sb.ToString();
    }
}
