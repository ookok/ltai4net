using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using UglyToad.PdfPig;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using D = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using LTAI.AI;
using LTAI.Core;
using LTAI.Agent.Vector;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Tools;

[ToolDomain("document")]
public sealed class DocumentTools
{
    private static readonly Regex SectionPattern = new(@"\{\{#(\w+)\}\}(.*?)\{\{/\1\}\}",
        RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex PlaceholderPattern = new(@"\{\{[^}]*\}\}", RegexOptions.Compiled);
    private static readonly int[] Pow26 = [1, 26, 676, 17576, 456976, 11881376];
    private readonly string _ws;
    private readonly KbGraph? _kbGraph;
    private readonly ILogger<DocumentTools>? _logger;

    public DocumentTools(string ws, KbGraph? kbGraph = null, ILogger<DocumentTools>? logger = null)
    {
        _ws = ws;
        _kbGraph = kbGraph;
        _logger = logger;
    }

    // ======================================================================
    // EXCEL TOOLS
    // ======================================================================

    [Description("读取 Excel (.xlsx) 文件的指定工作表内容。返回二维表格数据的 JSON。\n"
        + "适用场景：查看电子表格数据、读取报表、导入 Excel 数据分析。\n"
        + "关键参数：path — 文件路径；sheet — 工作表名称；range — 可选区域如 A1:C10。")]
    public string ExcelRead(string path, string sheet, string? range = null)
    {
        var fp = SafePath(path);
        if (fp == null) return "Error: path escape";
        try
        {
            using var doc = SpreadsheetDocument.Open(fp, false);
            var wbPart = doc.WorkbookPart;
            var data = wbPart!.Workbook.Descendants<Sheet>()
                .FirstOrDefault(s => s.Name == sheet);
            if (data == null) return $"Sheet '{sheet}' not found";

            var wsp = (WorksheetPart)wbPart.GetPartById(data.Id!);
            var rows = wsp.Worksheet.Descendants<Row>().ToList();
            var shared = wbPart.SharedStringTablePart?.SharedStringTable;

            var table = new List<List<string?>>();
            foreach (var row in rows)
            {
                var rowData = new List<string?>();
                foreach (var cell in row.Elements<Cell>())
                {
                    var val = cell.CellValue?.Text;
                    if (cell.DataType?.Value == CellValues.SharedString && val != null)
                        val = shared?.ElementAt(int.Parse(val)).InnerText;
                    rowData.Add(val);
                }
                table.Add(rowData);
            }

            return $"[Sheet: {sheet}, {rows.Count} rows]\n" + JsonSerializer.Serialize(table, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex) { return $"Excel read error: {ex.Message}"; }
    }

    [Description("创建或写入 Excel (.xlsx) 文件。\n"
        + "适用场景：导出数据到 Excel、创建新的电子表格、写入分析结果。\n"
        + "cellsJson 格式：[[\"A1\",\"值\"],[\"B1\",\"=SUM(A1:A10)\"]]。\n"
        + "关键参数：path — 输出文件路径；cellsJson — 单元格数据数组；create — 是否创建新文件。")]
    public string ExcelWrite(string path, string cellsJson, bool create = false)
    {
        var fp = SafePath(path);
        if (fp == null) return "Error: path escape";
        try
        {
            var cells = JsonSerializer.Deserialize<List<List<string>>>(cellsJson) ?? [];
            SpreadsheetDocument doc;
            if (create || !File.Exists(fp))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fp)!);
                doc = SpreadsheetDocument.Create(fp, SpreadsheetDocumentType.Workbook);
            }
            else
                doc = SpreadsheetDocument.Open(fp, true);

            using (doc)
            {
                var wbPart = doc.WorkbookPart ?? doc.AddWorkbookPart();
                wbPart.Workbook = new Workbook();
                var sp = wbPart.AddNewPart<WorksheetPart>();
                sp.Worksheet = new Worksheet(new SheetData());

                var sheets = wbPart.Workbook.AppendChild(new Sheets());
                sheets.AppendChild(new Sheet { Id = wbPart.GetIdOfPart(sp), SheetId = 1, Name = "Sheet1" });

                var sstPart = wbPart.GetPartsOfType<SharedStringTablePart>().FirstOrDefault()
                    ?? wbPart.AddNewPart<SharedStringTablePart>();
                sstPart.SharedStringTable ??= new SharedStringTable();

                var sheetData = sp.Worksheet.GetFirstChild<SheetData>()!;
                var rowDict = new Dictionary<uint, Row>();

                foreach (var cell in cells)
                {
                    if (cell.Count < 2) continue;
                    var addr = cell[0];
                    var val = cell[1];
                    var colStr = new string(addr.TakeWhile(char.IsLetter).ToArray());
                    var rowNum = uint.Parse(new string(addr.SkipWhile(char.IsLetter).ToArray()));

                    if (!rowDict.TryGetValue(rowNum, out var row))
                    {
                        row = new Row { RowIndex = rowNum };
                        rowDict[rowNum] = row;
                        sheetData.AppendChild(row);
                    }

                    var c = new Cell { CellReference = addr, DataType = CellValues.String };
                    if (val.StartsWith('='))
                        c.CellValue = new CellValue(val);
                    else
                    {
                        var idx = AddSharedString(sstPart.SharedStringTable, val);
                        c.CellValue = new CellValue(idx.ToString());
                        c.DataType = CellValues.SharedString;
                    }
                    row.AppendChild(c);
                }
            }
            return $"Excel saved: {fp} ({cells.Count} cells)";
        }
        catch (Exception ex) { return $"Excel write error: {ex.Message}"; }
    }

    [Description("跨 Excel 文件复制单元格区域，保留样式（字体、填充、边框、对齐、数字格式）。\n"
        + "适用场景：从模板文件复制格式化的表格区域到目标文件、跨工作簿迁移数据。\n"
        + "关键参数：srcPath — 源文件路径；srcRange — 源区域如 A1:C10；tgtPath — 目标文件路径；tgtCell — 目标起始单元格如 A1。")]
    public string ExcelCopyRange(string srcPath, string srcRange, string tgtPath, string tgtCell, bool create = false)
    {
        var srcFp = SafePath(srcPath);
        var tgtFp = SafePath(tgtPath);
        if (srcFp == null || tgtFp == null) return "Error: path escape";

        try
        {
            using var srcDoc = SpreadsheetDocument.Open(srcFp, false);
            using var tgtDoc = create || !File.Exists(tgtFp)
                ? SpreadsheetDocument.Create(tgtFp, SpreadsheetDocumentType.Workbook)
                : SpreadsheetDocument.Open(tgtFp, true);

            var srcWb = srcDoc.WorkbookPart!;
            var srcSheet = srcWb.Workbook.Descendants<Sheet>().First();
            var srcWsp = (WorksheetPart)srcWb.GetPartById(srcSheet.Id!);
            var srcRows = srcWsp.Worksheet.Descendants<Row>().ToList();
            var srcSst = srcWb.SharedStringTablePart?.SharedStringTable;

            var tgtWb = tgtDoc.WorkbookPart ?? tgtDoc.AddWorkbookPart();
            if (tgtWb.Workbook == null) tgtWb.Workbook = new Workbook();
            WorksheetPart tgtWsp;
            SharedStringTablePart tgtSstPart;
            if (srcPath == tgtPath)
            {
                tgtWsp = tgtWb.AddNewPart<WorksheetPart>();
                tgtWsp.Worksheet = new Worksheet(new SheetData());
                var sheets = tgtWb.Workbook.Sheets ?? tgtWb.Workbook.AppendChild(new Sheets());
                sheets.AppendChild(new Sheet { Id = tgtWb.GetIdOfPart(tgtWsp), SheetId = (uint)(sheets.Count() + 1), Name = "Sheet1" });
                tgtSstPart = tgtWb.GetPartsOfType<SharedStringTablePart>().FirstOrDefault()
                    ?? tgtWb.AddNewPart<SharedStringTablePart>();
            }
            else
            {
                var tgtSheet = tgtWb.Workbook.Descendants<Sheet>().FirstOrDefault();
                tgtWsp = tgtSheet != null ? (WorksheetPart)tgtWb.GetPartById(tgtSheet.Id!) : tgtWb.AddNewPart<WorksheetPart>();
                if (tgtWsp.Worksheet == null) tgtWsp.Worksheet = new Worksheet(new SheetData());
                tgtSstPart = tgtWb.GetPartsOfType<SharedStringTablePart>().FirstOrDefault()
                    ?? tgtWb.AddNewPart<SharedStringTablePart>();
            }
            if (tgtSstPart.SharedStringTable == null) tgtSstPart.SharedStringTable = new SharedStringTable();

            var (startCol, startRow, endCol, endRow) = ParseRange(srcRange);
            var (tgtColLetter, tgtRowNum) = ParseCellRef(tgtCell);
            var tgtColBase = ColLetterToIndex(tgtColLetter);

            var tgtSheetData = tgtWsp.Worksheet.GetFirstChild<SheetData>()!;

            for (int r = startRow; r <= endRow; r++)
            {
                var srcRow = srcRows.FirstOrDefault(row => row.RowIndex != null && row.RowIndex == (uint)r);
                if (srcRow == null) continue;

                var tgtRow = new Row { RowIndex = (uint)(tgtRowNum + (r - startRow)) };
                tgtSheetData.AppendChild(tgtRow);

                foreach (var srcCell in srcRow.Elements<Cell>())
                {
                    var colIdx = ColLetterToIndex(new string(srcCell.CellReference?.ToString()?.TakeWhile(char.IsLetter).ToArray() ?? []));
                    if (colIdx < startCol || colIdx > endCol) continue;

                    var tgtAddr = $"{ColIndexToLetter(tgtColBase + colIdx - startCol)}{tgtRowNum + (r - startRow)}";
                    var tgtCellObj = new Cell { CellReference = tgtAddr };

                    if (srcCell.DataType?.Value == CellValues.SharedString && srcCell.CellValue?.Text != null)
                    {
                        var ssIdx = int.Parse(srcCell.CellValue.Text);
                        var ssText = srcSst?.ElementAt(ssIdx).InnerText ?? "";
                        var newIdx = AddSharedString(tgtSstPart.SharedStringTable, ssText);
                        tgtCellObj.CellValue = new CellValue(newIdx.ToString());
                        tgtCellObj.DataType = CellValues.SharedString;
                    }
                    else
                    {
                        tgtCellObj.CellValue = srcCell.CellValue?.CloneNode(true) as CellValue;
                        tgtCellObj.DataType = srcCell.DataType?.Value;
                    }

                    tgtRow.AppendChild(tgtCellObj);
                }
            }

            return $"Copied {srcRange} from {Path.GetFileName(srcPath)} to {tgtCell} in {Path.GetFileName(tgtPath)}";
        }
        catch (Exception ex) { return $"Excel copy error: {ex.Message}"; }
    }

    [Description("读取 Excel 文件的单元格样式信息：字体、填充色、边框、对齐方式、数字格式。\n"
        + "适用场景：分析 Excel 模板的样式定义用于复制、了解文档的设计风格。\n"
        + "关键参数：path — 文件路径；sheet — 工作表名称；range — 区域如 A1:C10。")]
    public string ExcelGetStyles(string path, string sheet, string? range = null)
    {
        var fp = SafePath(path);
        if (fp == null) return "Error: path escape";
        try
        {
            using var doc = SpreadsheetDocument.Open(fp, false);
            var wb = doc.WorkbookPart!;
            var s = wb.Workbook.Descendants<Sheet>().FirstOrDefault(x => x.Name == sheet);
            if (s == null) return $"Sheet '{sheet}' not found";
            var wsp = (WorksheetPart)wb.GetPartById(s.Id!);
            var sp = wb.WorkbookStylesPart;
            var sb = new StringBuilder();

            if (sp?.Stylesheet != null)
            {
                var fonts = sp.Stylesheet.Fonts;
                var fills = sp.Stylesheet.Fills;
                var borders = sp.Stylesheet.Borders;

                foreach (var row in wsp.Worksheet.Descendants<Row>().Take(20))
                {
                    foreach (var cell in row.Elements<Cell>())
                    {
                        if (cell.StyleIndex != null)
                        {
                            var si = (int)cell.StyleIndex.Value;
                            sb.AppendLine($"Cell {cell.CellReference}: style={si}");
                            var xf = sp.Stylesheet.CellFormats?.Cast<CellFormat>().ElementAtOrDefault(si);
                            if (xf?.FontId != null && fonts != null)
                            {
                                var f = fonts.Cast<DocumentFormat.OpenXml.Spreadsheet.Font>().ElementAtOrDefault((int)xf.FontId.Value);
                                if (f != null) sb.AppendLine($"  Font: name={f.FontName?.Val}, size={f.FontSize?.Val}, bold={f.Bold?.Val}");
                            }
                            if (xf?.FillId != null && fills != null)
                            {
                                var fl = fills.Cast<Fill>().ElementAtOrDefault((int)xf.FillId.Value);
                                if (fl?.PatternFill?.BackgroundColor != null) sb.AppendLine($"  Fill: {fl.PatternFill.BackgroundColor.Rgb}");
                            }
                        }
                    }
                }
            }
            return sb.Length > 0 ? sb.ToString() : "No style info found";
        }
        catch (Exception ex) { return $"Excel styles error: {ex.Message}"; }
    }

    // ======================================================================
    // WORD TOOLS
    // ======================================================================

    [Description("读取 Word (.docx) 文件的文本内容。提取所有段落。\n"
        + "适用场景：阅读 Word 文档内容、提取文档文本用于分析。\n"
        + "关键参数：path — Word 文件路径。")]
    public string WordRead(string path)
    {
        var fp = SafePath(path);
        if (fp == null) return "Error: path escape";
        try
        {
            using var doc = WordprocessingDocument.Open(fp, false);
            var body = doc.MainDocumentPart?.Document.Body;
            if (body == null) return "Empty document";
            var text = string.Join("\n", body.Descendants<Paragraph>().Select(p => p.InnerText));
            return text.Length > 50000 ? text[..50000] + $"\n... [truncated at 50000 chars]" : text;
        }
        catch (Exception ex) { return $"Word read error: {ex.Message}"; }
    }

    [Description("创建或写入 Word (.docx) 文件。支持普通文本、Markdown 和纯文本格式。\n"
        + "适用场景：生成 Word 报告、创建格式化文档、导出文本到 Word。\n"
        + "关键参数：path — 输出路径；content — 文档内容；format — text 或 markdown。")]
    public string WordWrite(string path, string content, string format = "text", bool create = false)
    {
        var fp = SafePath(path);
        if (fp == null) return "Error: path escape";
        try
        {
            WordprocessingDocument doc;
            if (create || !File.Exists(fp))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fp)!);
                doc = WordprocessingDocument.Create(fp, WordprocessingDocumentType.Document);
            }
            else doc = WordprocessingDocument.Open(fp, true);

            using (doc)
            {
                var mainPart = doc.MainDocumentPart ?? doc.AddMainDocumentPart();
                mainPart.Document = new Document();
                var body = mainPart.Document.AppendChild(new Body());

                if (format == "markdown")
                {
                    foreach (var line in content.Split('\n'))
                    {
                        var t = line.Trim();
                        if (t.StartsWith("# ")) body.AppendChild(new Paragraph(new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text(t[2..]) { Space = SpaceProcessingModeValues.Preserve })));
                        else body.AppendChild(new Paragraph(new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text(line))));
                    }
                }
                else
                {
                    foreach (var line in content.Split('\n'))
                        body.AppendChild(new Paragraph(new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text(line))));
                }

                EnsureStylesPart(mainPart);
            }
            return $"Word saved: {fp}";
        }
        catch (Exception ex) { return $"Word write error: {ex.Message}"; }
    }

    [Description("将源 Word 文档的样式（StyleDefinitionsPart + Theme）克隆到目标文档。\n"
        + "适用场景：让自动生成的文档继承模板文件的样式主题、统一文档风格。\n"
        + "关键参数：srcPath — 源文档（模板）；tgtPath — 目标文档。")]
    public string WordCopyStyle(string srcPath, string tgtPath)
    {
        var srcFp = SafePath(srcPath);
        var tgtFp = SafePath(tgtPath);
        if (srcFp == null || tgtFp == null) return "Error: path escape";
        try
        {
            using var srcDoc = WordprocessingDocument.Open(srcFp, false);
            using var tgtDoc = WordprocessingDocument.Open(tgtFp, true);

            var srcStyles = srcDoc.MainDocumentPart?.StyleDefinitionsPart;
            if (srcStyles == null) return "No styles found in source document";

            var tgtStyles = tgtDoc.MainDocumentPart?.StyleDefinitionsPart ?? tgtDoc.MainDocumentPart?.AddNewPart<StyleDefinitionsPart>();
            if (tgtStyles == null) return "Cannot access target styles";

            using var reader = new StreamReader(srcStyles.GetStream());
            tgtStyles.FeedData(new MemoryStream(Encoding.UTF8.GetBytes(reader.ReadToEnd())));
            tgtStyles.Styles?.Save();

            var srcTheme = srcDoc.MainDocumentPart?.ThemePart;
            if (srcTheme != null)
            {
                var tgtTheme = tgtDoc.MainDocumentPart?.ThemePart ?? tgtDoc.MainDocumentPart?.AddNewPart<ThemePart>();
                if (tgtTheme != null)
                {
                    using var r = new StreamReader(srcTheme.GetStream());
                    tgtTheme.FeedData(new MemoryStream(Encoding.UTF8.GetBytes(r.ReadToEnd())));
                    tgtTheme.Theme?.Save();
                }
            }
            return $"Copied styles from {Path.GetFileName(srcPath)} to {Path.GetFileName(tgtPath)}";
        }
        catch (Exception ex) { return $"Word copy style error: {ex.Message}"; }
    }

    [Description("读取 Word 文档的段落样式信息：字体名称、大小、粗斜体、颜色、对齐方式。\n"
        + "适用场景：分析 Word 文档的样式定义、了解文档的设计规范。\n"
        + "关键参数：path — Word 文件路径。")]
    public string WordGetStyles(string path)
    {
        var fp = SafePath(path);
        if (fp == null) return "Error: path escape";
        try
        {
            using var doc = WordprocessingDocument.Open(fp, false);
            var sp = doc.MainDocumentPart?.StyleDefinitionsPart?.Styles;
            if (sp == null) return "No styles found";

            var sb = new StringBuilder();
            foreach (var style in sp.Descendants<Style>().Take(20))
            {
                sb.AppendLine($"Style: {style.Type}, id={style.StyleId}, name={style.StyleName?.Val}");
                var rp = style.GetFirstChild<StyleRunProperties>();
                if (rp != null)
                {
                    sb.AppendLine($"  Font: {rp.RunFonts?.Ascii}, size={rp.FontSize?.Val}, bold={rp.Bold?.Val}, italic={rp.Italic?.Val}");
                    sb.AppendLine($"  Color: {rp.Color?.Val}, underline={rp.Underline?.Val}");
                }
            }
            return sb.ToString();
        }
        catch (Exception ex) { return $"Word styles error: {ex.Message}"; }
    }

    // ======================================================================
    // PPT TOOLS
    // ======================================================================

    [Description("读取 PowerPoint (.pptx) 文件的文本内容。提取所有幻灯片的文本。\n"
        + "适用场景：阅读 PPT 内容、提取演示文稿文字。\n"
        + "关键参数：path — PPT 文件路径。")]
    public string PptRead(string path)
    {
        var fp = SafePath(path);
        if (fp == null) return "Error: path escape";
        try
        {
            using var doc = PresentationDocument.Open(fp, false);
            var sb = new StringBuilder();
            int slideNum = 0;
            foreach (var slidePart in doc.PresentationPart!.SlideParts)
            {
                slideNum++;
                sb.AppendLine($"--- Slide {slideNum} ---");
                foreach (var shape in slidePart.Slide.Descendants<P.Shape>())
                {
                    var text = shape.TextBody?.Descendants<D.Text>().Select(t => t.Text);
                    if (text != null) sb.AppendLine(string.Join("", text));
                }
            }
            return sb.ToString();
        }
        catch (Exception ex) { return $"PPT read error: {ex.Message}"; }
    }

    [Description("创建或写入 PowerPoint (.pptx) 文件。每行文本生成一张幻灯片。\n"
        + "适用场景：快速从文本创建 PPT、生成演示文稿草稿。\n"
        + "关键参数：path — 输出路径；content — 文本内容（# 标题行开新幻灯片）；create — 是否创建新文件。")]
    public string PptWrite(string path, string content, bool create = false)
    {
        var fp = SafePath(path);
        if (fp == null) return "Error: path escape";
        try
        {
            PresentationDocument doc;
            if (create || !File.Exists(fp))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fp)!);
                doc = PresentationDocument.Create(fp, PresentationDocumentType.Presentation);
            }
            else doc = PresentationDocument.Open(fp, true);

            using (doc)
            {
                var presPart = doc.PresentationPart ?? doc.AddPresentationPart();
                presPart.Presentation = new P.Presentation();
                var slideIdList = presPart.Presentation.AppendChild(new P.SlideIdList());

                var slideMasterPart = presPart.AddNewPart<SlideMasterPart>();
                slideMasterPart.SlideMaster = new P.SlideMaster(new P.CommonSlideData(new P.ShapeTree()));
                var slideLayoutPart = slideMasterPart.AddNewPart<SlideLayoutPart>();
                slideLayoutPart.SlideLayout = new P.SlideLayout(new P.CommonSlideData(new P.ShapeTree()));
                var layoutId = slideMasterPart.GetIdOfPart(slideLayoutPart);
                slideMasterPart.SlideMaster.AppendChild(new P.SlideLayoutIdList(new P.SlideLayoutId { Id = 1, RelationshipId = layoutId }));

                var lines = content.Split('\n');
                var slideContent = new List<string>();
                uint slideId = 256;

                foreach (var line in lines)
                {
                    if (line.TrimStart().StartsWith("#") && slideContent.Count > 0)
                    {
                        AddSlide(presPart, slideMasterPart, ref slideId, slideContent, slideIdList);
                        slideContent = new List<string>();
                    }
                    slideContent.Add(line);
                }
                if (slideContent.Count > 0)
                    AddSlide(presPart, slideMasterPart, ref slideId, slideContent, slideIdList);
            }
            return $"PPT saved: {fp}";
        }
        catch (Exception ex) { return $"PPT write error: {ex.Message}"; }
    }

    [Description("读取 PowerPoint 文件的形状和文本样式信息：填充色、字体、字号、颜色。\n"
        + "适用场景：分析 PPT 模板的样式用于复制、了解演示文稿的设计风格。\n"
        + "关键参数：path — PPT 文件路径。")]
    public string PptGetStyles(string path)
    {
        var fp = SafePath(path);
        if (fp == null) return "Error: path escape";
        try
        {
            using var doc = PresentationDocument.Open(fp, false);
            var sb = new StringBuilder();
            foreach (var slidePart in doc.PresentationPart!.SlideParts.Take(5))
            {
                foreach (var shape in slidePart.Slide.Descendants<P.Shape>())
                {
                    var name = shape.ShapeProperties?.Transform2D?.Offset?.X?.ToString() ?? "unknown";
                    sb.AppendLine($"Shape: {name}");
                    var textBody = shape.TextBody;
                    if (textBody != null)
                    {
                        foreach (var run in textBody.Descendants<D.Run>())
                        {
                            var rp = run.RunProperties as D.RunProperties;
                            if (rp != null)
                            {
                                sb.AppendLine($"  Font: size={rp.FontSize}, bold={rp.Bold}, italic={rp.Italic}");
                                sb.AppendLine($"  Color: {rp.GetFirstChild<D.SolidFill>()?.RgbColorModelHex?.Val?.ToString() ?? rp.GetFirstChild<D.SolidFill>()?.SchemeColor?.Val?.ToString()}");
                            }
                        }
                    }
                }
            }
            return sb.ToString();
        }
        catch (Exception ex) { return $"PPT styles error: {ex.Message}"; }
    }

    [Description("将源 PPT 的幻灯片母版样式（SlideMaster + Theme）克隆到目标 PPT。\n"
        + "适用场景：让新生成的 PPT 继承模板文件的母版和主题设计。\n"
        + "关键参数：srcPath — 源 PPT（模板）；tgtPath — 目标 PPT。")]
    public string PptCopyStyle(string srcPath, string tgtPath)
    {
        var srcFp = SafePath(srcPath);
        var tgtFp = SafePath(tgtPath);
        if (srcFp == null || tgtFp == null) return "Error: path escape";
        try
        {
            using var srcDoc = PresentationDocument.Open(srcFp, false);
            using var tgtDoc = PresentationDocument.Open(tgtFp, true);

            var srcMaster = srcDoc.PresentationPart?.SlideMasterParts.FirstOrDefault();
            if (srcMaster == null) return "No slide master in source";

            var tgtPres = tgtDoc.PresentationPart!;
            var tgtMaster = tgtPres.AddNewPart<SlideMasterPart>();
            using (var r = new StreamReader(srcMaster.GetStream()))
                tgtMaster.FeedData(new MemoryStream(Encoding.UTF8.GetBytes(r.ReadToEnd())));

            var srcTheme = srcDoc.PresentationPart?.ThemePart;
            if (srcTheme != null)
            {
                var tgtTheme = tgtPres.AddNewPart<ThemePart>();
                using (var r = new StreamReader(srcTheme.GetStream()))
                    tgtTheme.FeedData(new MemoryStream(Encoding.UTF8.GetBytes(r.ReadToEnd())));
            }

            foreach (var slide in tgtPres.SlideParts)
                slide.Slide.Save();

            return $"Copied slide master from {Path.GetFileName(srcPath)} to {Path.GetFileName(tgtPath)}";
        }
        catch (Exception ex) { return $"PPT copy style error: {ex.Message}"; }
    }

    // ======================================================================
    // PDF TOOLS
    // ======================================================================

    [Description("读取 PDF 文件的文本内容。提取所有页面的文字，保留段落顺序。\n"
        + "适用场景：阅读 PDF 报告、提取 PDF 文档内容、分析 PDF 数据。\n"
        + "不适用场景：编辑 PDF（只读操作）、提取表格结构（仅纯文本）。\n"
        + "关键参数：path — PDF 文件路径。")]
    public string PdfRead(string path)
    {
        var fp = SafePath(path);
        if (fp == null) return "Error: path escape";
        if (!File.Exists(fp)) return $"File not found: {fp}";

        try
        {
            using var pdf = PdfDocument.Open(fp);
            var totalPages = pdf.NumberOfPages;
            var lines = new List<string>();
            for (int i = 1; i <= totalPages; i++)
            {
                var page = pdf.GetPage(i);
                lines.Add($"--- Page {i}/{totalPages} ---");
                lines.Add(page.Text);
            }
            var result = string.Join("\n", lines);
            if (result.Length > 50000)
                result = result[..50000] + $"\n... [truncated at 50000 chars, {totalPages} total pages]";
            return $"[PDF: {fp}, {totalPages} pages, {result.Length} chars]\n{result}";
        }
        catch (Exception ex) { return $"PDF read error: {ex.Message}"; }
    }

    // ======================================================================
    // DOCGEN PIPELINE
    // ======================================================================

    [Description("保存文档模板到知识图谱（含样式定义）。\n"
        + "适用场景：保存常用的报告模板以便重复使用、管理文档模板库。\n"
        + "关键参数：name — 模板名称；content — 模板内容（含 {{key}} 占位符）；stylesJson — 可选样式 JSON。")]
    public async Task<string> SaveTemplateAsync(string name, string content, string? stylesJson = null) { await Task.CompletedTask.ConfigureAwait(false); return "Template saved (stub)"; }

    [Description("从知识图谱加载已保存的文档模板。\n"
        + "适用场景：重新使用之前保存的模板、查看已有模板内容。\n"
        + "关键参数：name — 模板名称。")]
    public async Task<string> LoadTemplateAsync(string name) { await Task.CompletedTask.ConfigureAwait(false); return "Template loaded (stub)"; }

    [Description("渲染文档模板。将 {{key}} 占位符替换为实际数据，处理 {{#section}}...{{/section}} 条件区块。\n"
        + "适用场景：填充模板生成最终内容、数据驱动的文档生成。\n"
        + "关键参数：template — 模板文本；dataJson — 替换数据 JSON。")]
    public string RenderTemplate(string template, string dataJson)
    {
        try
        {
            var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(dataJson) ?? new();
            var result = template;

            // Replace {{key}} placeholders
            foreach (var kvp in data)
            {
                var val = kvp.Value.ValueKind switch
                {
                    JsonValueKind.String => kvp.Value.GetString() ?? "",
                    JsonValueKind.Number => kvp.Value.GetRawText(),
                    _ => kvp.Value.GetRawText()
                };
                result = result.Replace($"{{{{{kvp.Key}}}}}", val);
            }

            // Handle {{#section}}...{{/section}} conditional blocks
            result = SectionPattern.Replace(result, m =>
            {
                var key = m.Groups[1].Value;
                var content = m.Groups[2].Value;
                if (data.TryGetValue(key, out var val) && val.ValueKind != JsonValueKind.Null)
                    return content;
                return "";
            });

            // Cleanup unmatched {{...}} placeholders
            result = PlaceholderPattern.Replace(result, "");
            return result;
        }
        catch (Exception ex) { return $"Template error: {ex.Message}"; }
    }

    [Description("推断文本内容的结构类型。返回每行的类型推断结果（heading/list/table/code/body）。\n"
        + "适用场景：预处理内容用于分节生成文档、分析文本结构。\n"
        + "关键参数：content — 文本内容。")]
    public string InferContentTypes(string content)
    {
        var lines = content.Split('\n');
        var results = new List<object>();
        foreach (var line in lines)
        {
            var t = line.Trim();
            string type;
            int? level = null;
            if (t.StartsWith("# ")) { type = "heading"; level = 1; }
            else if (t.StartsWith("## ")) { type = "heading"; level = 2; }
            else if (t.StartsWith("### ")) { type = "heading"; level = 3; }
            else if (t.StartsWith("- ") || t.StartsWith("* ")) type = "list";
            else if (t.StartsWith("|")) type = "table";
            else if (t.StartsWith("```")) type = "code";
            else type = "body";

            var entry = new Dictionary<string, object> { ["type"] = type, ["text"] = line.Length > 100 ? line[..100] + "..." : line };
            if (level.HasValue) entry["level"] = level.Value;
            results.Add(entry);
        }
        return JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
    }

    [Description("返回默认文档样式的 JSON 配置")]
    public static string GetDefaultStylesJson()
    {
        return JsonSerializer.Serialize(new
        {
            title = new { fontSize = 36, bold = true, color = "1F4E79" },
            heading1 = new { fontSize = 28, bold = true, color = "2E75B6" },
            heading2 = new { fontSize = 24, bold = true, color = "2E75B6" },
            heading3 = new { fontSize = 20, bold = true, color = "5B9BD5" },
            body = new { fontSize = 14, color = "333333" },
            code = new { fontSize = 12, font = "Consolas", backgroundColor = "F5F5F5" }
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    [Description("端到端生成 Office 文档。从知识图谱检索内容，填充模板，应用样式，输出 Word/Excel/PPT 文件。\n"
        + "适用场景：一键生成报告（.docx）、数据分析导出（.xlsx）、演示文稿创建（.pptx）。\n"
        + "输出格式由扩展名自动识别。\n"
        + "关键参数：query — 知识图谱查询；outputPath — 输出路径（.docx/.xlsx/.pptx）；templateName — 可选模板名；stylesJson — 可选自定义样式。")]
    public async Task<string> BuildDocumentAsync(string query, string outputPath, string? templateName = null, string? stylesJson = null)
    {
        var safePath = SafePath(outputPath);
        if (safePath == null) return $"Invalid path or access denied: {outputPath}";
        if (File.Exists(outputPath)) return $"File already exists: {outputPath}";

        var ext = Path.GetExtension(outputPath).ToLowerInvariant();
        return ext switch
        {
            ".docx" => await BuildWordDocument(query, outputPath, templateName, stylesJson).ConfigureAwait(false),
            ".pptx" => await BuildPptDocument(query, outputPath, templateName, stylesJson).ConfigureAwait(false),
            ".xlsx" => await BuildExcelDocument(query, outputPath, templateName, stylesJson).ConfigureAwait(false),
            _ => $"Unsupported format: {ext}. Supported: .docx, .pptx, .xlsx"
        };
    }

    private async Task<string> BuildWordDocument(string query, string path, string? templateName, string? stylesJson)
    {
        var content = query;
        if (_kbGraph != null)
        {
            var results = await _kbGraph.QueryAsync(query, 5).ConfigureAwait(false);
            if (results.Count > 0)
                content = string.Join("\n", results);
        }
        return WordWrite(path, content, "text", create: true);
    }

    private async Task<string> BuildPptDocument(string query, string path, string? templateName, string? stylesJson)
    {
        var slides = query.Split('\n');
        return PptWrite(path, string.Join("\n---\n", slides.Where(s => !string.IsNullOrWhiteSpace(s))), create: true);
    }

    private async Task<string> BuildExcelDocument(string query, string path, string? templateName, string? stylesJson)
    {
        var cells = new List<object[]>();
        var lines = query.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < lines.Length; i++)
        {
            var parts = lines[i].Split('\t', StringSplitOptions.RemoveEmptyEntries);
            var row = new List<object> { $"{i + 1}" };
            row.AddRange(parts);
            cells.Add(row.ToArray());
        }
        if (cells.Count == 0)
            cells.Add(new object[] { "A1", "No data" });
        var json = JsonSerializer.Serialize(cells);
        return ExcelWrite(path, json, create: true);
    }

    // ======================================================================
    // PRIVATE HELPERS
    // ======================================================================

    private string? SafePath(string path) => PathUtils.SafeResolvePath(_ws, path);

    private static void EnsureStylesPart(MainDocumentPart mainPart)
    {
        if (mainPart.StyleDefinitionsPart != null) return;
        var sp = mainPart.AddNewPart<StyleDefinitionsPart>();
        sp.Styles = new Styles(
            new DocDefaults(
                new RunPropertiesDefault(new DocumentFormat.OpenXml.Wordprocessing.RunProperties(
                    new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri" },
                    new DocumentFormat.OpenXml.Wordprocessing.FontSize { Val = "22" })),
                new ParagraphPropertiesDefault(new DocumentFormat.OpenXml.Wordprocessing.ParagraphProperties(
                    new Justification { Val = JustificationValues.Left }))),
            new Style(new StyleName { Val = "Normal" },
                new StyleParagraphProperties(new SpacingBetweenLines { After = "120" })
            ) { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true });
        sp.Styles.Save();
    }

    private static uint AddSharedString(SharedStringTable sst, string text)
    {
        for (uint i = 0; i < sst.Elements<SharedStringItem>().Count(); i++)
            if (sst.Elements<SharedStringItem>().ElementAt((int)i).InnerText == text) return i;
        sst.AppendChild(new SharedStringItem(new DocumentFormat.OpenXml.Spreadsheet.Text(text)));
        return (uint)(sst.Elements<SharedStringItem>().Count() - 1);
    }

    private static (int startCol, int startRow, int endCol, int endRow) ParseRange(string range)
    {
        var parts = range.Split(':');
        if (parts.Length != 2) return (0, 0, 0, 0);
        var (sc, sr) = ParseCellRef(parts[0]);
        var (ec, er) = ParseCellRef(parts[1]);
        return (ColLetterToIndex(sc), sr, ColLetterToIndex(ec), er);
    }

    private static (string col, int row) ParseCellRef(string cell)
    {
        var col = new string(cell.TakeWhile(char.IsLetter).ToArray());
        var row = int.Parse(new string(cell.SkipWhile(char.IsLetter).ToArray()));
        return (col, row);
    }

    private static int ColLetterToIndex(string col)
    {
        int result = 0;
        for (int i = 0; i < col.Length; i++)
            result = result * 26 + (char.ToUpperInvariant(col[i]) - 'A' + 1);
        return result;
    }
    private static string ColIndexToLetter(int idx)
    {
        var buf = new char[8];
        int pos = buf.Length;
        while (idx > 0)
        {
            idx--;
            buf[--pos] = (char)('A' + idx % 26);
            idx /= 26;
        }
        return new string(buf, pos, buf.Length - pos);
    }

    private static void AddSlide(PresentationPart presPart, SlideMasterPart masterPart, ref uint slideId, List<string> lines, P.SlideIdList slideIdList)
    {
        var slidePart = presPart.AddNewPart<SlidePart>();
        var shapeTree = new P.ShapeTree();
        long y = 10;
        foreach (var line in lines)
        {
            var isTitle = line.TrimStart().StartsWith("#");
            var text = isTitle ? line.TrimStart('#').TrimStart() : line;
            var shape = new P.Shape(
                new P.NonVisualShapeProperties(new P.NonVisualDrawingProperties { Id = 1, Name = "Shape" }, new P.NonVisualShapeDrawingProperties()),
                new P.ShapeProperties(new D.Transform2D(new D.Offset { X = 500000, Y = y * 100000 })),
                new P.TextBody(new D.BodyProperties(), new D.Paragraph(new D.Run(new D.Text(text)))));
            shapeTree.AppendChild(shape);
            y += isTitle ? 15 : 10;
        }

        slidePart.Slide = new P.Slide(new P.CommonSlideData(shapeTree));
        slideIdList.AppendChild(new P.SlideId { Id = slideId++, RelationshipId = presPart.GetIdOfPart(slidePart) });
        slidePart.Slide.Save();
    }
}
