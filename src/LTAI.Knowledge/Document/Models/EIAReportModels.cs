using System.Text.Json.Serialization;

namespace LTAI.Knowledge.Document.Models;

public sealed class DocumentStyles
{
    [JsonPropertyName("styles")]
    public Dictionary<string, StyleDef> Styles { get; set; } = new();

    [JsonPropertyName("base_style")]
    public string BaseStyle { get; set; } = "Normal";
}

public sealed class StyleDef
{
    [JsonPropertyName("font")]
    public string Font { get; set; } = "宋体";

    [JsonPropertyName("size")]
    public double Size { get; set; } = 12;

    [JsonPropertyName("bold")]
    public bool Bold { get; set; }

    [JsonPropertyName("italic")]
    public bool Italic { get; set; }

    [JsonPropertyName("underline")]
    public bool Underline { get; set; }

    [JsonPropertyName("color")]
    public string Color { get; set; } = "000000";

    [JsonPropertyName("align")]
    public string Align { get; set; } = "left";

    [JsonPropertyName("indent")]
    public string Indent { get; set; } = "";

    [JsonPropertyName("line_spacing")]
    public double LineSpacing { get; set; }

    [JsonPropertyName("space_before")]
    public double SpaceBefore { get; set; }

    [JsonPropertyName("space_after")]
    public double SpaceAfter { get; set; }
}

public sealed class ReportSection
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "Normal";

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("style")]
    public StyleDef? Style { get; set; }

    [JsonPropertyName("page_break_before")]
    public bool PageBreakBefore { get; set; }
}

public sealed class TableSection
{
    [JsonPropertyName("headers")]
    public List<string> Headers { get; set; } = new();

    [JsonPropertyName("rows")]
    public List<List<string>> Rows { get; set; } = new();

    [JsonPropertyName("style")]
    public StyleDef? Style { get; set; }

    [JsonPropertyName("caption")]
    public string Caption { get; set; } = "";
}

public sealed class ReportDocument
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("metadata")]
    public Dictionary<string, string> Metadata { get; set; } = new();

    [JsonPropertyName("sections")]
    public List<ReportSection> Sections { get; set; } = new();

    [JsonPropertyName("tables")]
    public List<TableSection> Tables { get; set; } = new();
}

public sealed class StyleFingerprint
{
    [JsonPropertyName("source_file")]
    public string SourceFile { get; set; } = "";

    [JsonPropertyName("styles")]
    public DocumentStyles Styles { get; set; } = new();

    [JsonPropertyName("extracted_at")]
    public DateTime ExtractedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("section_count")]
    public int SectionCount { get; set; }

    [JsonPropertyName("page_setup")]
    public PageSetupFingerprint? PageSetup { get; set; }
}

public sealed class PageSetupFingerprint
{
    [JsonPropertyName("page_width")]
    public double PageWidth { get; set; } = 11906;

    [JsonPropertyName("page_height")]
    public double PageHeight { get; set; } = 16838;

    [JsonPropertyName("margin_top")]
    public double MarginTop { get; set; } = 1440;

    [JsonPropertyName("margin_bottom")]
    public double MarginBottom { get; set; } = 1440;

    [JsonPropertyName("margin_left")]
    public double MarginLeft { get; set; } = 1800;

    [JsonPropertyName("margin_right")]
    public double MarginRight { get; set; } = 1800;
}
