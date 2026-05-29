using System.ComponentModel;
using System.Text;

namespace LTAI.Agent.Tools;

/// <summary>
/// Mermaid diagram generator + SVG renderer (via QuickChart.io, no API key needed).
/// Mermaid text output works in GitHub, Notion, Obsidian, mermaid.live.
/// Set renderSvg=true to also generate SVG file on disk.
/// </summary>
public sealed class FlowchartTools
{
    private readonly IHttpClientFactory? _httpF;
    public FlowchartTools(IHttpClientFactory? httpF = null) => _httpF = httpF;

    [Description("Generate flowchart. Nodes: A[Label], edges: A-->B. renderSvg=true to also save SVG")]
    public string Flowchart(string direction = "TB", string? nodes = null, string? edges = null,
        string? title = null, bool renderSvg = false)
        => Wrap("flowchart " + direction, title, renderSvg, nodes, edges);

    [Description("Sequence diagram: A->>B: Message. renderSvg=true to save SVG")]
    public string SequenceDiagram(string messages, string? title = null, bool renderSvg = false)
        => Wrap("sequenceDiagram", title, renderSvg, messages);

    [Description("Class diagram: class Foo { +int x; +void Bar() }. renderSvg=true")]
    public string ClassDiagram(string classes, string? relationships = null, bool renderSvg = false)
        => Wrap("classDiagram", null, renderSvg, classes, relationships);

    [Description("Gantt chart: TaskName, start, end. renderSvg=true")]
    public string GanttChart(string tasks, string? title = null, string dateFormat = "YYYY-MM-DD", bool renderSvg = false)
        => Wrap("gantt", title, renderSvg, $"dateFormat {dateFormat}\n{tasks}");

    [Description("ER diagram: CUSTOMER ||--o{ ORDER : places. renderSvg=true")]
    public string ErDiagram(string relationships, bool renderSvg = false)
        => Wrap("erDiagram", null, renderSvg, relationships);

    // ─── Build Mermaid + optional SVG ───

    private string Wrap(string header, string? title, bool renderSvg, params string?[] sections)
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
            var svgBytes = http.GetByteArrayAsync(url).Result;
            var svgPath = Path.Combine(Path.GetTempPath(), $"ltai_diagram_{Guid.NewGuid():N}.svg");
            File.WriteAllBytes(svgPath, svgBytes);
            return $"{mermaid}\n\n📊 SVG saved: `{svgPath}` ({(svgBytes.Length / 1024.0):F1} KB)";
        }
        catch (Exception ex)
        {
            return $"{mermaid}\n\n⚠️ SVG render failed: {ex.Message}";
        }
    }
}
