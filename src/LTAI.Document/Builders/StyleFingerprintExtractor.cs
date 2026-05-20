using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using LTAI.Document.Models;

namespace LTAI.Document;

public sealed class StyleFingerprintExtractor
{
    public StyleFingerprint Extract(string docxPath)
    {
        var fingerprint = new StyleFingerprint
        {
            SourceFile = global::System.IO.Path.GetFileName(docxPath)
        };

        try
        {
            using var doc = WordprocessingDocument.Open(docxPath, false);
            var body = doc.MainDocumentPart?.Document?.Body;
            if (body == null) return fingerprint;

            fingerprint.Styles = new DocumentStyles { BaseStyle = "Normal" };
            var sectionCount = 0;

            foreach (var para in body.Elements<DocumentFormat.OpenXml.Wordprocessing.Paragraph>())
            {
                var style = ExtractParagraphStyle(para);
                if (style != null)
                {
                    var styleName = DetermineStyleName(para);
                    if (!fingerprint.Styles.Styles.ContainsKey(styleName))
                        fingerprint.Styles.Styles[styleName] = style;
                    sectionCount++;
                }
            }

            fingerprint.SectionCount = sectionCount;

            var sectProps = body.Elements<DocumentFormat.OpenXml.Wordprocessing.SectionProperties>().FirstOrDefault();
            if (sectProps != null)
                fingerprint.PageSetup = ExtractPageSetup(sectProps);
        }
        catch
        {
        }

        fingerprint.ExtractedAt = DateTime.UtcNow;
        return fingerprint;
    }

    public string ExtractToJson(string docxPath)
    {
        var fingerprint = Extract(docxPath);
        return JsonSerializer.Serialize(fingerprint, new JsonSerializerOptions { WriteIndented = true });
    }

    public string GenerateLlmPrompt(string fingerprintJson)
    {
        return $"""
Based on the following style fingerprint extracted from an EIA report, learn the formatting rules:
{fingerprintJson}

Generate a JSON document using these styles. For each section, specify:
- type: "Heading1", "Heading2", "Heading3", or "Normal"
- text: the content
- style: font/size/bold/align/indent/line_spacing matching the learned fingerprint

Output ONLY the JSON, no markdown or comments.
""";
    }

    private static StyleDef? ExtractParagraphStyle(DocumentFormat.OpenXml.Wordprocessing.Paragraph para)
    {
        var props = para.ParagraphProperties;
        var firstRun = para.Elements<DocumentFormat.OpenXml.Wordprocessing.Run>().FirstOrDefault();
        var runProps = firstRun?.RunProperties;
        if (runProps == null) return null;

        var style = new StyleDef();

        var font = runProps.Elements<DocumentFormat.OpenXml.Wordprocessing.RunFonts>().FirstOrDefault();
        if (font != null)
            style.Font = (!string.IsNullOrEmpty(font.EastAsia?.Value) ? font.EastAsia.Value :
                          !string.IsNullOrEmpty(font.Ascii?.Value) ? font.Ascii.Value : "宋体");

        var fontSize = runProps.Elements<DocumentFormat.OpenXml.Wordprocessing.FontSize>().FirstOrDefault();
        if (fontSize?.Val?.Value != null)
            style.Size = int.Parse(fontSize.Val.Value) / 2.0;

        style.Bold = runProps.Elements<DocumentFormat.OpenXml.Wordprocessing.Bold>().Any();
        style.Italic = runProps.Elements<DocumentFormat.OpenXml.Wordprocessing.Italic>().Any();
        style.Underline = runProps.Elements<DocumentFormat.OpenXml.Wordprocessing.Underline>().Any();

        var color = runProps.Elements<DocumentFormat.OpenXml.Wordprocessing.Color>().FirstOrDefault();
        if (color?.Val?.Value != null) style.Color = color.Val.Value;

        if (props != null)
        {
            var jc = props.Elements<DocumentFormat.OpenXml.Wordprocessing.Justification>().FirstOrDefault();
            if (jc?.Val?.Value != null)
                style.Align = jc.Val.Value.ToString().ToLower();

            var indent = props.Elements<DocumentFormat.OpenXml.Wordprocessing.Indentation>().FirstOrDefault();
            if (indent?.FirstLineChars?.Value != null)
                style.Indent = "firstLine2Char";
            else if (indent?.FirstLine?.Value != null)
                style.Indent = indent.FirstLine.Value;

            var spacing = props.Elements<DocumentFormat.OpenXml.Wordprocessing.SpacingBetweenLines>().FirstOrDefault();
            if (spacing?.Line?.Value != null)
                style.LineSpacing = int.Parse(spacing.Line.Value) / 20.0;
            if (spacing?.Before?.Value != null)
                style.SpaceBefore = int.Parse(spacing.Before.Value) / 20.0;
            if (spacing?.After?.Value != null)
                style.SpaceAfter = int.Parse(spacing.After.Value) / 20.0;
        }

        return style;
    }

    private static string DetermineStyleName(DocumentFormat.OpenXml.Wordprocessing.Paragraph para)
    {
        var pStyle = para.ParagraphProperties?.Elements<DocumentFormat.OpenXml.Wordprocessing.ParagraphStyleId>().FirstOrDefault();
        if (pStyle != null)
        {
            var val = pStyle.Val?.Value ?? "";
            if (val.Contains("Heading") || val.Contains("heading") || val.Contains("1"))
                return "Heading" + (val.Contains("1") ? "1" : val.Contains("2") ? "2" : val.Contains("3") ? "3" : "1");
            if (val.Contains("TOC")) return "TOC";
        }

        var text = para.InnerText.Trim();
        if (text.StartsWith("第") && text.Contains("章")) return "Heading1";
        if (text.StartsWith("第") && text.Contains("节") || (text.Length > 0 && char.IsDigit(text[0]) && text.Length < 20)) return "Heading2";

        return "Normal";
    }

    private static PageSetupFingerprint? ExtractPageSetup(DocumentFormat.OpenXml.Wordprocessing.SectionProperties sectProps)
    {
        var ps = new PageSetupFingerprint();

        var pageSize = sectProps.Elements<DocumentFormat.OpenXml.Wordprocessing.PageSize>().FirstOrDefault();
        if (pageSize != null)
        {
            if (pageSize.Width?.Value != null) ps.PageWidth = pageSize.Width.Value;
            if (pageSize.Height?.Value != null) ps.PageHeight = pageSize.Height.Value;
        }

        var margins = sectProps.Elements<DocumentFormat.OpenXml.Wordprocessing.PageMargin>().FirstOrDefault();
        if (margins != null)
        {
            if (margins.Top?.Value != null) ps.MarginTop = margins.Top.Value;
            if (margins.Bottom?.Value != null) ps.MarginBottom = margins.Bottom.Value;
            if (margins.Left?.Value != null) ps.MarginLeft = margins.Left.Value;
            if (margins.Right?.Value != null) ps.MarginRight = margins.Right.Value;
        }

        return ps;
    }
}
