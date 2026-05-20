using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using LTAI.Document.Models;

namespace LTAI.Document;

public sealed class EIAReportBuilder
{
    public void BuildFromJson(string jsonPath, string outputPath)
    {
        var json = global::System.IO.File.ReadAllText(jsonPath);
        var report = JsonSerializer.Deserialize<ReportDocument>(json);
        if (report == null) throw new InvalidOperationException("Failed to parse report JSON");

        Build(report, outputPath);
    }

    public void Build(ReportDocument report, string outputPath)
    {
        var dir = global::System.IO.Path.GetDirectoryName(outputPath);
        if (dir != null) global::System.IO.Directory.CreateDirectory(dir);

        using var wordDoc = WordprocessingDocument.Create(outputPath, WordprocessingDocumentType.Document);
        var mainPart = wordDoc.AddMainDocumentPart();
        mainPart.Document = new DocumentFormat.OpenXml.Wordprocessing.Document();
        var body = mainPart.Document.AppendChild(new Body());

        ApplyPageSetup(body, report);

        var sectionIndex = 0;
        foreach (var section in report.Sections)
        {
            if (section.PageBreakBefore && sectionIndex > 0)
                body.AppendChild(new Paragraph(new Run(new Break { Type = BreakValues.Page })));

            var para = body.AppendChild(new Paragraph());
            var run = para.AppendChild(new Run());
            run.AppendChild(new Text(section.Text) { Space = SpaceProcessingModeValues.Preserve });

            ApplyParagraphStyle(para, section.Style ?? new StyleDef());
            ApplyRunStyle(run, section.Style ?? new StyleDef(), section.Type);
            sectionIndex++;
        }

        foreach (var table in report.Tables)
        {
            body.AppendChild(new Paragraph());
            BuildTable(body, table);
        }

        mainPart.Document.Save();
    }

    private static void ApplyPageSetup(Body body, ReportDocument report)
    {
        var sectProps = new SectionProperties();
        var pageSize = new PageSize
        {
            Width = 11906, Height = 16838,
            Orient = PageOrientationValues.Portrait
        };
        var margins = new PageMargin
        {
            Top = 1440, Bottom = 1440,
            Left = 1800, Right = 1800,
            Header = 720, Footer = 720
        };

        sectProps.Append(pageSize);
        sectProps.Append(margins);
        body.Append(sectProps);
    }

    private static void ApplyParagraphStyle(Paragraph para, StyleDef style)
    {
        var props = para.ParagraphProperties ?? new ParagraphProperties();

        if (style.Align == "center")
            props.Append(new Justification { Val = JustificationValues.Center });
        else if (style.Align == "right")
            props.Append(new Justification { Val = JustificationValues.Right });
        else if (style.Align == "both")
            props.Append(new Justification { Val = JustificationValues.Both });

        if (style.Indent == "firstLine2Char")
        {
            props.Append(new Indentation { FirstLineChars = 200 });
        }
        else if (!string.IsNullOrEmpty(style.Indent))
        {
            var indentVal = int.TryParse(style.Indent, out var i) ? i : 0;
            props.Append(new Indentation { FirstLine = (i > 0 ? indentVal.ToString() : null) });
        }

        if (style.LineSpacing > 0)
            props.Append(new SpacingBetweenLines { Line = ((int)(style.LineSpacing * 20)).ToString(), LineRule = LineSpacingRuleValues.Auto });

        if (style.SpaceBefore > 0)
            props.Append(new SpacingBetweenLines { Before = ((int)(style.SpaceBefore * 20)).ToString() });

        if (style.SpaceAfter > 0)
            props.Append(new SpacingBetweenLines { After = ((int)(style.SpaceAfter * 20)).ToString() });

        para.ParagraphProperties = props;
    }

    private static void ApplyRunStyle(Run run, StyleDef style, string sectionType)
    {
        var props = run.RunProperties ?? new RunProperties();

        if (!string.IsNullOrEmpty(style.Font))
        {
            props.Append(new RunFonts
            {
                Ascii = style.Font,
                HighAnsi = style.Font,
                EastAsia = style.Font,
                ComplexScript = style.Font
            });
        }
        else if (sectionType.StartsWith("Heading"))
        {
            props.Append(new RunFonts { Ascii = "黑体", HighAnsi = "黑体", EastAsia = "黑体" });
        }
        else
        {
            props.Append(new RunFonts { Ascii = "宋体", HighAnsi = "宋体", EastAsia = "宋体" });
        }

        if (style.Size > 0)
            props.Append(new FontSize { Val = (style.Size * 2).ToString() });
        else if (sectionType.StartsWith("Heading"))
            props.Append(new FontSize { Val = "32" });
        else
            props.Append(new FontSize { Val = "24" });

        if (style.Bold)
            props.Append(new Bold());
        if (style.Italic)
            props.Append(new Italic());
        if (style.Underline)
            props.Append(new Underline { Val = UnderlineValues.Single });
        if (!string.IsNullOrEmpty(style.Color) && style.Color != "000000")
            props.Append(new Color { Val = style.Color });

        run.RunProperties = props;
    }

    private static void BuildTable(Body body, TableSection table)
    {
        var tbl = body.AppendChild(new Table());
        var tblProps = new TableProperties(
            new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 6 },
                new BottomBorder { Val = BorderValues.Single, Size = 6 },
                new LeftBorder { Val = BorderValues.Single, Size = 6 },
                new RightBorder { Val = BorderValues.Single, Size = 6 },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 6 },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 6 }
            ),
            new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct }
        );
        tbl.Append(tblProps);

        if (!string.IsNullOrEmpty(table.Caption))
        {
            var captionRow = new TableRow();
            var captionCell = new TableCell(new Paragraph(new Run(new Text(table.Caption))));
            captionRow.Append(captionCell);
            tbl.Append(captionRow);
        }

        if (table.Headers.Count > 0)
        {
            var headerRow = new TableRow();
            foreach (var header in table.Headers)
            {
                var cell = new TableCell();
                var para = new Paragraph();
                var run = new Run(new Text(header));
                run.RunProperties = new RunProperties(
                    new Bold(),
                    new RunFonts { Ascii = "黑体", EastAsia = "黑体" }
                );
                para.Append(run);
                cell.Append(para);
                cell.Append(new TableCellProperties(
                    new Shading { Fill = "D9D9D9", Val = ShadingPatternValues.Clear }
                ));
                headerRow.Append(cell);
            }
            tbl.Append(headerRow);
        }

        foreach (var row in table.Rows)
        {
            var tr = new TableRow();
            foreach (var cell in row)
            {
                var tc = new TableCell();
                var para = new Paragraph();
                var run = new Run(new Text(cell));
                run.RunProperties = new RunProperties(
                    new RunFonts { Ascii = "宋体", EastAsia = "宋体" },
                    new FontSize { Val = "21" }
                );
                para.Append(run);
                tc.Append(para);
                tr.Append(tc);
            }
            tbl.Append(tr);
        }
    }
}
