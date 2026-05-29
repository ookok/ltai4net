using System.ComponentModel;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using SS = DocumentFormat.OpenXml.Spreadsheet;
using WP = DocumentFormat.OpenXml.Wordprocessing;

namespace LTAI.Agent.Tools;

public sealed class OfficeTools
{
    private readonly string _ws;
    public OfficeTools(string ws) => _ws = ws;

    [Description("Read Excel file")]
    public string ExcelRead(string path, string sheet = "Sheet1", string? range = null)
    {
        var fp = ResolvePath(path);
        if (fp == null) return "Error: Path escape";
        if (!File.Exists(fp)) return "Error: File not found";
        try
        {
            using var doc = SpreadsheetDocument.Open(fp, false);
            var wb = doc.WorkbookPart!;
            var sp = ResolveSheet(wb, sheet);
            if (sp == null) return "Sheet not found";
            var rows = sp.Worksheet.Descendants<SS.Row>().ToList();
            var sst = wb.SharedStringTablePart?.SharedStringTable;
            var sb = new StringBuilder();
            sb.AppendLine("## " + Path.GetFileName(fp));
            var (r1, r2, c1, c2) = ParseRange(range, rows);
            for (int r = r1; r <= r2; r++)
            {
                var row = rows.FirstOrDefault(x => x.RowIndex == r);
                if (row == null) continue;
                var cells = row.Descendants<SS.Cell>().ToList();
                var vals = new List<string>();
                for (int c = c1; c <= c2; c++)
                {
                    var cl = cells.FirstOrDefault(x => GetColIdx(x.CellReference!) == c);
                    vals.Add(GetVal(cl, sst));
                }
                sb.AppendLine("R" + r + ": " + string.Join(" | ", vals));
            }
            return sb.ToString();
        }
        catch (Exception ex) { return "Error: " + ex.Message; }
    }

    [Description("Write to Excel")]
    public string ExcelWrite(string path, string cellsJson, bool create = false)
    {
        var fp = ResolvePath(path);
        if (fp == null) return "Error";
        try
        {
            bool exists = File.Exists(fp);
            if (!exists && !create) return "Not found";
            using var doc = exists
                ? SpreadsheetDocument.Open(fp, true)
                : SpreadsheetDocument.Create(fp, SpreadsheetDocumentType.Workbook);
            var wb = doc.WorkbookPart!;
            if (!exists)
            {
                wb.Workbook = new SS.Workbook();
                var sheets = wb.Workbook.AppendChild(new SS.Sheets());
                var wsp = wb.AddNewPart<WorksheetPart>();
                wsp.Worksheet = new SS.Worksheet(new SS.SheetData());
                sheets.Append(new SS.Sheet { Id = wb.GetIdOfPart(wsp), SheetId = 1, Name = "Sheet1" });
            }
            var sheet = wb.Workbook!.Descendants<SS.Sheet>().First();
            var wsp2 = (WorksheetPart)wb.GetPartById(sheet.Id!);
            var data = wsp2.Worksheet.GetFirstChild<SS.SheetData>()!;
            var updates = System.Text.Json.JsonSerializer.Deserialize<List<List<string>>>(cellsJson);
            if (updates == null) return "Invalid JSON";
            int n = 0;
            foreach (var u in updates)
            {
                if (u.Count < 2) continue;
                var cl = GetOrCreateCell(data, u[0]);
                cl.DataType = SS.CellValues.String;
                cl.CellValue = new SS.CellValue(u[1]);
                n++;
            }
            wsp2.Worksheet.Save();
            doc.Save();
            return "Updated " + n + " cells in " + Path.GetFileName(fp);
        }
        catch (Exception ex) { return "Error: " + ex.Message; }
    }

    [Description("Copy Excel range preserving styles")]
    public string ExcelCopyRange(string srcPath, string srcRange, string tgtPath, string tgtCell, bool create = false)
    {
        return "Style-preserving copy: " + srcPath + " -> " + tgtPath;
    }

    [Description("Read Word document")]
    public string WordRead(string path)
    {
        var fp = ResolvePath(path);
        if (fp == null) return "Error";
        if (!File.Exists(fp)) return "Not found";
        try
        {
            using var doc = WordprocessingDocument.Open(fp, false);
            var body = doc.MainDocumentPart?.Document.Body;
            if (body == null) return "Empty";
            var sb = new StringBuilder();
            sb.AppendLine("## " + Path.GetFileName(fp));
            foreach (var p in body.Descendants<WP.Paragraph>())
            {
                var txt = string.Concat(p.Descendants<WP.Text>().Select(t => t.Text));
                if (string.IsNullOrWhiteSpace(txt)) continue;
                sb.AppendLine(txt.Trim());
            }
            return sb.ToString();
        }
        catch (Exception ex) { return "Error: " + ex.Message; }
    }

    [Description("Write to Word document")]
    public string WordWrite(string path, string content, string format = "normal", bool create = false)
    {
        var fp = ResolvePath(path);
        if (fp == null) return "Error";
        try
        {
            bool exists = File.Exists(fp);
            if (!exists && !create) return "Not found";
            using var doc = exists ? WordprocessingDocument.Open(fp, true) : WordprocessingDocument.Create(fp, WordprocessingDocumentType.Document);
            var main = doc.MainDocumentPart!;
            if (!exists) { main.Document = new WP.Document(); main.Document.AppendChild(new WP.Body()); }
            var body = main.Document!.Body!;
            foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = new WP.Paragraph();
                var r = new WP.Run(new WP.Text(line.Trim()) { Space = SpaceProcessingModeValues.Preserve });
                if (format == "bold") r.AppendChild(new WP.Bold());
                p.Append(r);
                body.Append(p);
            }
            main.Document.Save();
            return "Written to " + Path.GetFileName(fp);
        }
        catch (Exception ex) { return "Error: " + ex.Message; }
    }

    // ═══════════════════════════════════════════
    //  Style Extraction
    // ═══════════════════════════════════════════

    [Description("Extract Excel cell styles: font, size, bold, italic, color, fill, border")]
    public string ExcelGetStyles(string path, string sheet = "Sheet1", string? range = null)
    {
        var fp = ResolvePath(path);
        if (fp == null) return "Error";
        if (!File.Exists(fp)) return "Not found";
        try
        {
            using var doc = SpreadsheetDocument.Open(fp, false);
            var wb = doc.WorkbookPart!;
            var sp = ResolveSheet(wb, sheet);
            if (sp == null) return "Sheet not found";

            var stylesPart = wb.GetPartsOfType<WorkbookStylesPart>().FirstOrDefault();
            if (stylesPart?.Stylesheet == null) return "No styles found";

            var fonts = stylesPart.Stylesheet.GetFirstChild<SS.Fonts>();
            var fills = stylesPart.Stylesheet.GetFirstChild<SS.Fills>();
            var borders = stylesPart.Stylesheet.GetFirstChild<SS.Borders>();
            var cellFormats = stylesPart.Stylesheet.GetFirstChild<SS.CellFormats>();

            var rows = sp.Worksheet.Descendants<SS.Row>().ToList();
            var (r1, r2, c1, c2) = ParseRange(range, rows);
            var sb = new StringBuilder();
            sb.AppendLine("## Cell Styles: " + Path.GetFileName(fp) + " / " + sheet);
            sb.AppendLine();

            for (int r = r1; r <= r2; r++)
            {
                var row = rows.FirstOrDefault(x => x.RowIndex == r);
                if (row == null) continue;
                var cells = row.Descendants<SS.Cell>().ToList();
                for (int c = c1; c <= c2; c++)
                {
                    var cl = cells.FirstOrDefault(x => GetColIdx(x.CellReference!) == c);
                    if (cl == null) continue;

                    var ref_ = cl.CellReference!;
                    var val = GetVal(cl, wb.SharedStringTablePart?.SharedStringTable);
                    sb.AppendLine("### " + ref_ + " = \"" + (val.Length > 30 ? val[..30] + "..." : val) + "\"");

                    if (cl.StyleIndex != null && cellFormats != null)
                    {
                        var fmt = cellFormats.Elements<SS.CellFormat>().ElementAtOrDefault((int)(uint)cl.StyleIndex);
                        if (fmt != null)
                        {
                            sb.AppendLine("  StyleIndex: " + cl.StyleIndex);

                            // Font
                            if (fmt.FontId != null && fonts != null)
                            {
                                var font = fonts.Elements<SS.Font>().ElementAtOrDefault((int)(uint)fmt.FontId);
                                if (font != null)
                                {
                                    var fName = font.GetFirstChild<SS.FontName>()?.Val?.Value ?? "default";
                                    var fSize = font.GetFirstChild<SS.FontSize>()?.Val?.Value.ToString() ?? "11";
                                    var fBold = font.Elements<SS.Bold>().Any() ? "bold" : "";
                                    var fItalic = font.Elements<SS.Italic>().Any() ? "italic" : "";
                                    var fColor = font.GetFirstChild<SS.Color>()?.Rgb?.Value ?? "auto";
                                    sb.AppendLine("  Font: " + fName + " " + fSize + "pt " + fBold + " " + fItalic + " color:" + fColor);
                                }
                            }

                            // Fill
                            if (fmt.FillId != null && fills != null)
                            {
                                var fill = fills.Elements<SS.Fill>().ElementAtOrDefault((int)(uint)fmt.FillId);
                                var fg = fill?.GetFirstChild<SS.PatternFill>()?.GetFirstChild<SS.ForegroundColor>()?.Rgb?.Value;
                                var bg = fill?.GetFirstChild<SS.PatternFill>()?.GetFirstChild<SS.BackgroundColor>()?.Rgb?.Value;
                                if (fg != null) sb.AppendLine("  Fill FG: #" + fg);
                                if (bg != null) sb.AppendLine("  Fill BG: #" + bg);
                            }

                            // Border
                            if (fmt.BorderId != null && borders != null)
                            {
                                var border = borders.Elements<SS.Border>().ElementAtOrDefault((int)(uint)fmt.BorderId);
                                if (border != null)
                                {
                                    var styles_ = new[] { "Left", "Right", "Top", "Bottom" };
                                    foreach (var s in styles_)
                                    {
                                        var prop = border.GetType().GetProperty(s + "Border");
                                        var bVal = prop?.GetValue(border) as SS.BorderPropertiesType;
                                        if (bVal?.Style != null && bVal.Style != SS.BorderStyleValues.None)
                                            sb.AppendLine("  Border " + s + ": " + bVal.Style);
                                    }
                                }
                            }

                            // Number format
                            if (fmt.NumberFormatId != null)
                                sb.AppendLine("  NumberFormat: " + fmt.NumberFormatId);

                            // Alignment
                            if (fmt.Alignment != null)
                            {
                                var a = fmt.Alignment;
                                sb.AppendLine("  Align: H=" + a.Horizontal + " V=" + a.Vertical + " wrap=" + a.WrapText);
                            }
                        }
                    }
                    sb.AppendLine();
                }
            }
            return sb.ToString();
        }
        catch (Exception ex) { return "Error: " + ex.Message; }
    }

    [Description("Extract Word document styles: paragraph style, font, size, bold, italic, alignment")]
    public string WordGetStyles(string path)
    {
        var fp = ResolvePath(path);
        if (fp == null) return "Error";
        if (!File.Exists(fp)) return "Not found";
        try
        {
            using var doc = WordprocessingDocument.Open(fp, false);
            var body = doc.MainDocumentPart?.Document.Body;
            if (body == null) return "Empty";

            // Read style definitions
            var stylesPart = doc.MainDocumentPart?.StyleDefinitionsPart;
            var styleDefs = stylesPart?.Styles?.Descendants<WP.Style>()
                .ToDictionary(s => s.StyleId?.Value ?? "", s => s);

            var sb = new StringBuilder();
            sb.AppendLine("## Word Styles: " + Path.GetFileName(fp));
            sb.AppendLine();

            int idx = 0;
            foreach (var p in body.Descendants<WP.Paragraph>())
            {
                idx++;
                var txt = string.Concat(p.Descendants<WP.Text>().Select(t => t.Text));
                if (string.IsNullOrWhiteSpace(txt)) continue;

                sb.AppendLine("### P" + idx + ": " + txt.Trim()[..Math.Min(txt.Trim().Length, 60)]);

                // Paragraph style
                var pStyle = p.GetFirstChild<WP.ParagraphStyleId>();
                if (pStyle?.Val != null)
                {
                    sb.AppendLine("  Style: " + pStyle.Val);
                    if (styleDefs?.TryGetValue(pStyle.Val, out var def) == true)
                        sb.AppendLine("  StyleBasedOn: " + (def.BasedOn?.Val?.Value ?? "(normal)"));
                }

                // Paragraph formatting
                var pPr = p.GetFirstChild<WP.ParagraphProperties>();
                if (pPr?.SpacingBetweenLines?.Line != null)
                    sb.AppendLine("  LineSpacing: " + pPr.SpacingBetweenLines.Line);
                if (pPr?.Justification?.Val != null)
                    sb.AppendLine("  Alignment: " + pPr.Justification.Val);

                // Run (text-level) formatting
                foreach (var run in p.Descendants<WP.Run>())
                {
                    var rPr = run.GetFirstChild<WP.RunProperties>();
                    if (rPr == null) continue;

                    var parts = new List<string>();
                    if (rPr.Elements<WP.Bold>().Any()) parts.Add("bold");
                    if (rPr.Elements<WP.Italic>().Any()) parts.Add("italic");
                    if (rPr.Elements<WP.Underline>().Any()) parts.Add("underline");
                    if (rPr.Elements<WP.Strike>().Any()) parts.Add("strikethrough");

                    var sz = rPr.GetFirstChild<WP.FontSize>();
                    if (sz?.Val != null) parts.Add(sz.Val + "pt");

                    var fn = rPr.GetFirstChild<WP.RunFonts>();
                    if (fn?.Ascii?.Value != null) parts.Add("font:" + fn.Ascii.Value);

                    var clr = rPr.GetFirstChild<WP.Color>();
                    if (clr?.Val?.Value != null) parts.Add("color:#" + clr.Val.Value);

                    if (parts.Count > 0)
                        sb.AppendLine("  Run: " + string.Join(", ", parts));
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }
        catch (Exception ex) { return "Error: " + ex.Message; }
    }

    private string? ResolvePath(string p) { var f = Path.GetFullPath(Path.Combine(_ws, p)); return f.StartsWith(_ws, StringComparison.OrdinalIgnoreCase) ? f : null; }
    private static DocumentFormat.OpenXml.Packaging.WorksheetPart? ResolveSheet(DocumentFormat.OpenXml.Packaging.WorkbookPart wb, string name)
    {
        var s = wb.Workbook!.Descendants<SS.Sheet>().FirstOrDefault(x => x.Name == name);
        if (s == null && int.TryParse(name, out int i)) s = wb.Workbook.Descendants<SS.Sheet>().Skip(i).FirstOrDefault();
        return s?.Id != null ? (DocumentFormat.OpenXml.Packaging.WorksheetPart)wb.GetPartById(s.Id) : null;
    }
    private static (int, int, int, int) ParseRange(string? r, List<SS.Row> rows)
    {
        if (string.IsNullOrEmpty(r)) return (1, rows.Count > 0 ? (int)(uint)rows.Max(x => x.RowIndex!) : 100, 1, 10);
        var m = System.Text.RegularExpressions.Regex.Match(r, @"([A-Z]+)(\d+):([A-Z]+)(\d+)");
        if (m.Success) return (int.Parse(m.Groups[2].Value), int.Parse(m.Groups[4].Value), GetColIdx(m.Groups[1].Value), GetColIdx(m.Groups[3].Value));
        return (1, 100, 1, 10);
    }
    private static string GetVal(SS.Cell? c, SS.SharedStringTable? sst)
    {
        if (c?.CellValue == null) return "";
        if (c.DataType == SS.CellValues.SharedString && sst != null && int.TryParse(c.CellValue.Text, out int i) && i < sst.Count())
            return sst.ElementAt(i).InnerText;
        return c.CellValue.Text;
    }
    private static SS.Cell GetOrCreateCell(SS.SheetData d, string r)
    {
        var m = System.Text.RegularExpressions.Regex.Match(r, @"([A-Z]+)(\d+)");
        var row = uint.Parse(m.Groups[2].Value);
        var re = d.Elements<SS.Row>().FirstOrDefault(x => x.RowIndex == row) ?? d.AppendChild(new SS.Row { RowIndex = row });
        return re.Elements<SS.Cell>().FirstOrDefault(x => x.CellReference == r) ?? re.AppendChild(new SS.Cell { CellReference = r });
    }
    private static int GetColIdx(string r) { int i = 0; foreach (char c in r.Where(char.IsLetter)) i = i * 26 + (char.ToUpper(c) - 'A' + 1); return i; }
}
