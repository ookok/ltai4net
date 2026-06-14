using System.ComponentModel;
using System.Text;
using LTAI.AI;

namespace LTAI.Agent.Tools;

/// <summary>
/// Mermaid diagram generator + SVG renderer (via QuickChart.io, no API key needed).
/// Mermaid text output works in GitHub, Notion, Obsidian, mermaid.live.
/// Set renderSvg=true to also generate SVG file on disk.
/// </summary>
[ToolDomain("flowchart")]
public sealed class FlowchartTools
{
    private readonly IHttpClientFactory? _httpF;
    public FlowchartTools(IHttpClientFactory? httpF = null) => _httpF = httpF;

    [Description("生成流程图。支持 Markdown 风格节点和连线语法，可渲染为 SVG。\n"
        + "适用场景：绘制业务流程、算法流程图、系统架构流程、决策树。\n"
        + "不适用场景：时序图（请用 SequenceDiagram）、类图（请用 ClassDiagram）、甘特图（请用 GanttChart）。\n"
        + "关键参数：nodes — 节点定义数组；edges — 连线定义数组；title — 图表标题；renderSvg — 是否保存 SVG 文件。")]
    [ToolExample("画一个登录流程的流程图")]
    [ToolExample("生成用户注册的流程图")]
    public async Task<string> Flowchart(string direction = "TB", string? nodes = null, string? edges = null,
        string? title = null, bool renderSvg = false)
        => await WrapAsync("flowchart " + direction, title, renderSvg, nodes, edges).ConfigureAwait(false);

    [Description("生成时序图/序列图。展示对象之间的消息交互顺序，可渲染为 SVG。\n"
        + "适用场景：绘制 API 调用时序、展示系统间消息交互、用户操作流程的时间顺序。\n"
        + "不适用场景：流程图（请用 Flowchart）、类图（请用 ClassDiagram）、ER 图（请用 ErDiagram）。\n"
        + "关键参数：messages — 消息序列数组；title — 图表标题；renderSvg — 是否保存 SVG。")]
    [ToolExample("画一个用户登录的时序图")]
    public async Task<string> SequenceDiagram(string messages, string? title = null, bool renderSvg = false)
        => await WrapAsync("sequenceDiagram", title, renderSvg, messages).ConfigureAwait(false);

    [Description("生成 UML 类图。展示类结构、属性和方法。\n"
        + "适用场景：设计系统类结构、展示继承关系、记录领域模型。\n"
        + "不适用场景：流程图（请用 Flowchart）、ER 图（请用 ErDiagram）。\n"
        + "关键参数：classes — 类定义数组；relationships — 关系定义数组；renderSvg — 是否保存 SVG。")]
    [ToolExample("画一个订单系统的类图")]
    public async Task<string> ClassDiagram(string classes, string? relationships = null, bool renderSvg = false)
        => await WrapAsync("classDiagram", null, renderSvg, classes, relationships).ConfigureAwait(false);

    [Description("生成甘特图。展示项目任务的时间线和依赖关系。\n"
        + "适用场景：项目进度规划、任务时间线展示、里程碑追踪。\n"
        + "不适用场景：流程图（请用 Flowchart）、时序图（请用 SequenceDiagram）。\n"
        + "关键参数：tasks — 任务数组（名称、开始、结束）；title — 图表标题；dateFormat — 日期格式。")]
    [ToolExample("画一个项目进度的甘特图")]
    public async Task<string> GanttChart(string tasks, string? title = null, string dateFormat = "YYYY-MM-DD", bool renderSvg = false)
        => await WrapAsync("gantt", title, renderSvg, $"dateFormat {dateFormat}\n{tasks}").ConfigureAwait(false);

    [Description("生成 ER 实体关系图。展示数据库表之间的关联关系。\n"
        + "适用场景：数据库设计、展示表间关系、领域模型建模。\n"
        + "不适用场景：类图（请用 ClassDiagram）、流程图（请用 Flowchart）。\n"
        + "关键参数：relationships — 关系定义数组；renderSvg — 是否保存 SVG。")]
    [ToolExample("画一个数据库的 ER 图")]
    public async Task<string> ErDiagram(string relationships, bool renderSvg = false)
        => await WrapAsync("erDiagram", null, renderSvg, relationships).ConfigureAwait(false);

    // ─── Build Mermaid + optional SVG ───

    private async Task<string> WrapAsync(string header, string? title, bool renderSvg, params string?[] sections)
    {
        var sb = new StringBuilder();
        sb.AppendLine("```mermaid");
        sb.AppendLine(header);
        if (!string.IsNullOrEmpty(title)) sb.AppendLine($"  title {title}");
        foreach (var sec in sections)
        {
            if (string.IsNullOrEmpty(sec)) continue;
            foreach (var line in sec.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                if (!string.IsNullOrWhiteSpace(line)) sb.AppendLine($"  {line.Trim()}");
        }
        sb.AppendLine("```");

        var mermaid = sb.ToString();
        if (!renderSvg || _httpF == null) return mermaid;

        try
        {
            var url = "https://quickchart.io/mermaid?format=svg&width=900&height=700&graph=" + Uri.EscapeDataString(mermaid);
            var http = _httpF.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(15);
            var svgBytes = await http.GetByteArrayAsync(url).ConfigureAwait(false);
            var svgPath = Path.Combine(Path.GetTempPath(), $"ltai_diagram_{Guid.NewGuid():N}.svg");
            await File.WriteAllBytesAsync(svgPath, svgBytes).ConfigureAwait(false);
            // Auto-clean temp file after 5 minutes
            _ = Task.Run(async () => { try { await Task.Delay(TimeSpan.FromMinutes(5)).ConfigureAwait(false); File.Delete(svgPath); } catch { } });
            return $"{mermaid}\n\n📊 SVG saved: `{svgPath}` ({(svgBytes.Length / 1024.0):F1} KB)";
        }
        catch (Exception ex)
        {
            return $"{mermaid}\n\n⚠️ SVG render failed: {ex.Message}";
        }
    }
}
