using System.Text;
using DocumentFormat.OpenXml.Packaging;
using SS = DocumentFormat.OpenXml.Spreadsheet;
using WP = DocumentFormat.OpenXml.Wordprocessing;
using P = DocumentFormat.OpenXml.Presentation;
using D = DocumentFormat.OpenXml.Drawing;

namespace LTAI.Agent.Tools;

/// <summary>
/// Static text extraction helpers for Office documents.
/// Used by KbGraph indexer to ingest Office file content.
/// </summary>
public static class OfficeDocumentReader
{
    public static string ExtractWordText(string path)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart?.Document.Body;
        if (body == null) return "";

        var sb = new StringBuilder();
        foreach (var p in body.Descendants<WP.Paragraph>())
        {
            var txt = string.Concat(p.Descendants<WP.Text>().Select(t => t.Text));
            if (!string.IsNullOrWhiteSpace(txt))
                sb.AppendLine(txt.Trim());
        }
        return sb.ToString();
    }

    public static string ExtractExcelText(string path)
    {
        using var doc = SpreadsheetDocument.Open(path, false);
        var wb = doc.WorkbookPart!;
        var sb = new StringBuilder();

        foreach (var sheet in wb.Workbook!.Descendants<SS.Sheet>())
        {
            var sp = sheet.Id?.Value != null
                ? (WorksheetPart)wb.GetPartById(sheet.Id)
                : null;
            if (sp == null) continue;

            var rows = sp.Worksheet.Descendants<SS.Row>().ToList();
            if (rows.Count == 0) continue;

            var sst = wb.SharedStringTablePart?.SharedStringTable;
            sb.AppendLine($"## {sheet.Name}");

            foreach (var row in rows)
            {
                var vals = row.Descendants<SS.Cell>()
                    .Select(c => GetVal(c, sst))
                    .Where(v => !string.IsNullOrEmpty(v));
                var line = string.Join(" | ", vals);
                if (!string.IsNullOrWhiteSpace(line))
                    sb.AppendLine(line);
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static string ExtractPptText(string path)
    {
        using var doc = PresentationDocument.Open(path, false);
        var pres = doc.PresentationPart!;
        var sb = new StringBuilder();
        int slideNum = 0;

        foreach (var slidePart in pres.SlideParts)
        {
            slideNum++;
            sb.AppendLine($"## Slide {slideNum}");
            foreach (var shape in slidePart.Slide.Descendants<P.Shape>())
            {
                var txt = string.Concat(shape.Descendants<D.Text>().Select(t => t.Text));
                if (!string.IsNullOrWhiteSpace(txt))
                    sb.AppendLine(txt.Trim());
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string GetVal(SS.Cell? c, SS.SharedStringTable? sst)
    {
        if (c?.CellValue == null) return "";
        if (c.DataType == SS.CellValues.SharedString && sst != null
            && int.TryParse(c.CellValue.Text, out int i) && i < sst.Count())
            return sst.ElementAt(i).InnerText;
        return c.CellValue.Text;
    }
}
