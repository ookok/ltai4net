using System.Text;
using DocumentFormat.OpenXml.Packaging;
using SS = DocumentFormat.OpenXml.Spreadsheet;
using WP = DocumentFormat.OpenXml.Wordprocessing;
using P = DocumentFormat.OpenXml.Presentation;
using D = DocumentFormat.OpenXml.Drawing;

namespace LTAI.Agent.Tools;

/// <summary>
/// Static text extraction helpers for Office documents.
/// Supports cancellation, size pre-checks, progress reporting, and structural description.
/// </summary>
public static class OfficeDocumentReader
{
    public const long MaxFileSize = 50 * 1024 * 1024;
    public const int MaxOutputChars = 100_000;

    public static string? CheckFile(string path)
    {
        if (!File.Exists(path)) return $"文件不存在: {path}";
        var len = new FileInfo(path).Length;
        if (len > MaxFileSize)
            return $"文件过大 ({len / 1024 / 1024}MB)，最大支持 {MaxFileSize / 1024 / 1024}MB。";
        if (len == 0) return $"文件为空: {path}";
        return null;
    }

    /// <summary>获取 Word 文档结构摘要（不提取全文）。</summary>
    public static string DescribeWord(string path)
    {
        try
        {
            using var doc = WordprocessingDocument.Open(path, false);
            var body = doc.MainDocumentPart?.Document.Body;
            if (body == null) return "Word 文档 (空)";
            var fi = new FileInfo(path);
            var paras = body.Descendants<WP.Paragraph>().Count();
            var sections = body.Descendants<WP.SectionProperties>().Count();
            var tables = body.Descendants<WP.Table>().Count();
            var images = doc.MainDocumentPart?.ImageParts.Count() ?? 0;
            var sizeKb = fi.Length / 1024;
            var info = $"Word 文档 ({sizeKb}KB, {paras} 段落";
            if (sections > 1) info += $", {sections} 节";
            if (tables > 0) info += $", {tables} 表格";
            if (images > 0) info += $", {images} 图片";
            return info + ")";
        }
        catch { return "Word 文档"; }
    }

    /// <summary>获取 Excel 文档结构摘要（不提取全文）。</summary>
    public static string DescribeExcel(string path)
    {
        try
        {
            using var doc = SpreadsheetDocument.Open(path, false);
            var wb = doc.WorkbookPart!;
            var fi = new FileInfo(path);
            var sheets = wb.Workbook!.Descendants<SS.Sheet>().ToList();
            var totalRows = 0;
            foreach (var sheet in sheets)
            {
                var sp = sheet.Id?.Value is not null ? (WorksheetPart)wb.GetPartById(sheet.Id.Value) : null;
                if (sp != null)
                    totalRows += sp.Worksheet.Descendants<SS.Row>().Count();
            }
            var sizeKb = fi.Length / 1024;
            return $"Excel 工作簿 ({sizeKb}KB, {sheets.Count} 个工作表, ~{totalRows} 行数据)";
        }
        catch { return "Excel 工作簿"; }
    }

    /// <summary>获取 PPT 文档结构摘要（不提取全文）。</summary>
    public static string DescribePpt(string path)
    {
        try
        {
            using var doc = PresentationDocument.Open(path, false);
            var pres = doc.PresentationPart!;
            var fi = new FileInfo(path);
            var slides = pres.SlideParts.Count();
            var sizeKb = fi.Length / 1024;
            return $"PPT 演示文稿 ({sizeKb}KB, {slides} 张幻灯片)";
        }
        catch { return "PPT 演示文稿"; }
    }

    /// <summary>获取文档结构描述（根据扩展名自动选择）。</summary>
    public static string Describe(string path)
    {
        var ext = Path.GetExtension(path)?.ToLowerInvariant();
        return ext switch
        {
            ".docx" => DescribeWord(path),
            ".xlsx" => DescribeExcel(path),
            ".pptx" => DescribePpt(path),
            _ => $"文件 ({new FileInfo(path).Length / 1024}KB)"
        };
    }

    public static string ExtractWordText(string path,
        CancellationToken ct = default, IProgress<string>? progress = null)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart?.Document.Body;
        if (body == null) return "";

        var sb = new StringBuilder();
        var paras = body.Descendants<WP.Paragraph>().ToList();
        var summary = DescribeWord(path);
        sb.AppendLine($"[{summary}]");
        sb.AppendLine();
        progress?.Report($"正在读取 Word 文档 ({paras.Count} 段落)...");

        for (int i = 0; i < paras.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var txt = string.Concat(paras[i].Descendants<WP.Text>().Select(t => t.Text));
            if (!string.IsNullOrWhiteSpace(txt))
                sb.AppendLine(txt.Trim());

            if (sb.Length > MaxOutputChars)
            {
                sb.Append($"\n... [已截断: 仅显示前 {MaxOutputChars / 1000}K 字符，共 {paras.Count} 段落]");
                break;
            }

            if (i % 50 == 0 && i > 0)
                progress?.Report($"正在读取 Word... 第 {i}/{paras.Count} 段落");
        }
        return sb.ToString();
    }

    public static string ExtractExcelText(string path,
        CancellationToken ct = default, IProgress<string>? progress = null)
    {
        using var doc = SpreadsheetDocument.Open(path, false);
        var wb = doc.WorkbookPart!;
        var sb = new StringBuilder();
        var sheets = wb.Workbook!.Descendants<SS.Sheet>().ToList();
        var summary = DescribeExcel(path);
        sb.AppendLine($"[{summary}]");
        sb.AppendLine();

        for (int si = 0; si < sheets.Count; si++)
        {
            ct.ThrowIfCancellationRequested();
            var sheet = sheets[si];
            progress?.Report($"正在读取 Excel... 第 {si + 1}/{sheets.Count} 个工作表");

            var sp = sheet.Id?.Value is not null
                ? (WorksheetPart)wb.GetPartById(sheet.Id.Value)
                : null;
            if (sp == null) continue;

            var rows = sp.Worksheet.Descendants<SS.Row>().ToList();
            if (rows.Count == 0) continue;

            var sst = wb.SharedStringTablePart?.SharedStringTable;
            sb.AppendLine($"## {sheet.Name} ({rows.Count} 行)");

            for (int ri = 0; ri < rows.Count; ri++)
            {
                ct.ThrowIfCancellationRequested();
                var vals = rows[ri].Descendants<SS.Cell>()
                    .Select(c => GetVal(c, sst))
                    .Where(v => !string.IsNullOrEmpty(v));
                var line = string.Join(" | ", vals);
                if (!string.IsNullOrWhiteSpace(line))
                    sb.AppendLine(line);

                if (sb.Length > MaxOutputChars)
                {
                    sb.Append($"\n... [已截断: 仅显示前 {MaxOutputChars / 1000}K 字符]");
                    return sb.ToString();
                }
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static string ExtractPptText(string path,
        CancellationToken ct = default, IProgress<string>? progress = null)
    {
        using var doc = PresentationDocument.Open(path, false);
        var pres = doc.PresentationPart!;
        var sb = new StringBuilder();
        var slides = pres.SlideParts.ToList();
        var summary = DescribePpt(path);
        sb.AppendLine($"[{summary}]");
        sb.AppendLine();

        for (int si = 0; si < slides.Count; si++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"正在读取 PPT... 第 {si + 1}/{slides.Count} 张幻灯片");

            sb.AppendLine($"## Slide {si + 1}");
            foreach (var shape in slides[si].Slide.Descendants<P.Shape>())
            {
                var txt = string.Concat(shape.Descendants<D.Text>().Select(t => t.Text));
                if (!string.IsNullOrWhiteSpace(txt))
                    sb.AppendLine(txt.Trim());
            }
            sb.AppendLine();

            if (sb.Length > MaxOutputChars)
            {
                sb.Append($"\n... [已截断: 仅显示前 {MaxOutputChars / 1000}K 字符]");
                break;
            }
        }
        return sb.ToString();
    }

    private static string GetVal(SS.Cell? c, SS.SharedStringTable? sst)
    {
        if (c?.CellValue == null) return "";
        if (c.DataType != null && c.DataType == SS.CellValues.SharedString && sst != null
            && int.TryParse(c.CellValue.Text, out int i) && i < sst.Count())
            return sst.ElementAt(i).InnerText;
        return c.CellValue.Text ?? "";
    }
}
