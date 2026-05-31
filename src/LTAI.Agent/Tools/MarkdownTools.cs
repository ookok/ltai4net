using System.ComponentModel;
using LTAI.AI;
using Markdig;

namespace LTAI.Agent.Tools;

[ToolDomain("core")]
public static class MarkdownTools
{
    [Description("将 Markdown 文本渲染为 HTML。\n"
        + "适用场景：预览 Markdown 渲染效果、生成 HTML 文档、内容发布准备。\n"
        + "关键参数：markdown — Markdown 文本内容。")]
    public static string RenderMarkdown(string markdown)
    {
        try
        {
            var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
            var html = Markdown.ToHtml(markdown, pipeline);
            var preview = markdown.Length > 5000 ? markdown[..5000] + "\n... [truncated]" : markdown;

            var result = $"--- Markdown Preview ({markdown.Length} chars) ---\n\n{preview}\n\n--- HTML Output ({html.Length} chars) ---\n\n{html}";
            if (result.Length > 50000)
                result = result[..50000] + "\n... [truncated]";
            return result;
        }
        catch (Exception ex)
        {
            return $"Markdown render error: {ex.GetType().Name}: {ex.Message}";
        }
    }
}
