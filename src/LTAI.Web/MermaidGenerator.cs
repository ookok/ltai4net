using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Web;

/// LLM-driven Mermaid DSL generator.
/// Converts natural language descriptions to Mermaid diagram syntax.
/// Supports: flowchart, sequence, class, state, Gantt, pie, ER, mindmap.
public sealed class MermaidGenerator
{
    private readonly IChatClient _llm;
    private readonly ILogger<MermaidGenerator> _logger;

    public static readonly Dictionary<string, string> DiagramTypes = new()
    {
        ["flowchart"] = "flowchart TD/LR",
        ["process_flow"] = "flowchart TD",
        ["sequence"] = "sequenceDiagram",
        ["class"] = "classDiagram",
        ["state"] = "stateDiagram-v2",
        ["gantt"] = "gantt",
        ["pie"] = "pie",
        ["er"] = "erDiagram",
        ["mindmap"] = "mindmap",
        ["timeline"] = "timeline",
        ["c4"] = "C4Context",
        ["graph"] = "flowchart TD"
    };

    private const string SystemPrompt =
@"You are a Mermaid.js diagram expert. Generate ONLY valid Mermaid diagram syntax.

Rules:
- Output ONLY the Mermaid code, no markdown fences, no explanations
- Use the appropriate diagram type specified
- Keep node labels short and clear (Chinese or English)
- Use proper Mermaid syntax: nodes, edges, subgraphs, styling
- For flowcharts: use [] for process, () for rounded, {} for diamond/decision, >] for async
- For sequences: use -> for solid, --> for dashed, ->> for async
- For classes: use standard UML with attributes and methods
- Ensure the output is valid Mermaid.js v11 syntax
- Do NOT wrap output in ```mermaid code fences";

    public MermaidGenerator(IChatClient llm, ILogger<MermaidGenerator>? logger = null)
    {
        _llm = llm;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MermaidGenerator>.Instance;
    }

    /// Generate Mermaid DSL from natural language description
    public async Task<MermaidResult> GenerateAsync(
        string description, string type = "flowchart", CancellationToken ct = default)
    {
        var diagramType = DiagramTypes.GetValueOrDefault(type, "flowchart TD");
        var prompt = $"Generate a {diagramType} diagram for: {description}";

        try
        {
            var response = await _llm.GetResponseAsync(
                new List<ChatMessage>
                {
                    new(ChatRole.System, SystemPrompt),
                    new(ChatRole.User, prompt)
                },
                new ChatOptions { Temperature = 0.3f, MaxOutputTokens = 2000 },
                ct).ConfigureAwait(false);

            var mermaid = CleanMermaidOutput(response.Text ?? "");

            _logger.LogInformation("Mermaid generated: type={Type}, len={Len}", type, mermaid.Length);

            return new MermaidResult
            {
                Success = mermaid.Length > 10,
                MermaidCode = mermaid,
                DiagramType = diagramType,
                Html = BuildMermaidHtml(mermaid, type)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Mermaid generation failed for: {Desc}", description[..Math.Min(description.Length, 100)]);
            return new MermaidResult
            {
                Success = false,
                Error = ex.Message,
                DiagramType = diagramType,
                Html = FallbackAscii(description, type)
            };
        }
    }

    /// Render Mermaid code to standalone HTML (browser-side rendering via CDN)
    public static string BuildMermaidHtml(string mermaidCode, string diagramType)
    {
        var escaped = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            .Encode(mermaidCode);

        return $@"<!DOCTYPE html><html><head><meta charset='utf-8'><style>
body{{margin:0;display:flex;justify-content:center;align-items:center;min-height:100vh;background:#fff;}}
.mermaid{{max-width:100%;}}</style></head><body>
<pre class='mermaid'>{mermaidCode}</pre>
<script src='https://cdn.jsdelivr.net/npm/mermaid@11/dist/mermaid.min.js'></script>
<script>mermaid.initialize({{startOnLoad:true,theme:'default',securityLevel:'loose'}});</script>
</body></html>";
    }

    /// Render Mermaid as a div embeddable in other HTML
    public static string BuildMermaidEmbed(string mermaidCode, string diagramType)
    {
        var displayType = diagramType switch
        {
            "flowchart" or "process_flow" or "graph" => "graph TD",
            _ => diagramType
        };

        return $@"<div class='mermaid' style='background:#fff;padding:16px;border-radius:8px;max-width:100%;overflow-x:auto'>
{displayType}
{mermaidCode}
</div>
<script src='https://cdn.jsdelivr.net/npm/mermaid@11/dist/mermaid.min.js'></script>
<script>mermaid.initialize({{startOnLoad:true,theme:'default'}});</script>";
    }

    private static string CleanMermaidOutput(string raw)
    {
        var text = raw.Trim();

        // Remove markdown code fences
        if (text.StartsWith("```mermaid", StringComparison.OrdinalIgnoreCase))
            text = text["```mermaid".Length..].Trim();
        else if (text.StartsWith("```", StringComparison.OrdinalIgnoreCase))
            text = text[3..].Trim();
        if (text.EndsWith("```", StringComparison.OrdinalIgnoreCase))
            text = text[..^3].Trim();

        // Remove leading diagram type declaration if present (we add it ourselves)
        foreach (var (_, dt) in DiagramTypes)
        {
            if (text.StartsWith(dt, StringComparison.OrdinalIgnoreCase))
            {
                text = text[dt.Length..].Trim();
                break;
            }
        }

        return text;
    }

    private static string FallbackAscii(string description, string type)
    {
        var title = description.Length > 40 ? description[..40] : description;
        return $@"<pre style='font-family:monospace;padding:16px;background:#f6f8fa;border-radius:8px'>
┌────────────────────────────────────┐
│  {title.PadRight(34)}│
├────────────────────────────────────┤
│  Diagram generation failed.         │
│  Type: {type,-24} │
│  Please retry with a clearer        │
│  description or specify nodes.      │
└────────────────────────────────────┘
</pre>";
    }
}

public sealed record MermaidResult
{
    public bool Success { get; init; }
    public string MermaidCode { get; init; } = "";
    public string DiagramType { get; init; } = "";
    public string Html { get; init; } = "";
    public string? Error { get; init; }
}
