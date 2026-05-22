using Microsoft.Extensions.Logging;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using NPOI.XWPF.UserModel;
using UglyToad.PdfPig;
using Markdig;

namespace LTAI.Tools.Documents;

public sealed class DocumentProcessor
{
    private readonly ILogger<DocumentProcessor> _logger;

    public DocumentProcessor(ILogger<DocumentProcessor> logger)
    {
        _logger = logger;
    }

    public async Task<string> ExtractTextAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        _logger.LogInformation("Processing document: {Path} ({Ext})", filePath, ext);

        return ext switch
        {
            ".pdf" => await ExtractPdfAsync(filePath, cancellationToken),
            ".xlsx" or ".xls" => await ExtractExcelAsync(filePath, cancellationToken),
            ".docx" => await ExtractDocxAsync(filePath, cancellationToken),
            ".md" or ".markdown" => await ExtractMarkdownAsync(filePath, cancellationToken),
            ".txt" or ".log" => await File.ReadAllTextAsync(filePath, cancellationToken),
            ".csv" => await File.ReadAllTextAsync(filePath, cancellationToken),
            _ => $"Unsupported format: {ext}"
        };
    }

    public async Task<List<DocumentSection>> ExtractSectionsAsync(
        string filePath, CancellationToken cancellationToken = default)
    {
        var text = await ExtractTextAsync(filePath, cancellationToken);
        var sections = new List<DocumentSection>();
        var lines = text.Split('\n');
        DocumentSection? current = null;

        foreach (var line in lines)
        {
            if (line.StartsWith('#') || line.StartsWith("##") || line.StartsWith("###"))
            {
                if (current != null) sections.Add(current);
                current = new DocumentSection
                {
                    Heading = line.TrimStart('#', ' '),
                    Level = line.TakeWhile(c => c == '#').Count()
                };
            }
            else if (current != null)
            {
                current.Content += line + "\n";
            }
            else
            {
                current = new DocumentSection { Heading = "Body", Level = 0, Content = line + "\n" };
            }
        }

        if (current != null) sections.Add(current);
        return sections;
    }

    private static async Task<string> ExtractPdfAsync(string filePath, CancellationToken ct)
    {
        using var pdf = PdfDocument.Open(filePath);
        var text = string.Join("\n", pdf.GetPages().Select(p => p.Text));
        return await Task.FromResult(text);
    }

    private static async Task<string> ExtractExcelAsync(string filePath, CancellationToken ct)
    {
        using var stream = File.OpenRead(filePath);
        var workbook = new XSSFWorkbook(stream);
        var sb = new System.Text.StringBuilder();

        for (var i = 0; i < workbook.NumberOfSheets; i++)
        {
            var sheet = workbook.GetSheetAt(i);
            sb.AppendLine($"--- Sheet: {sheet.SheetName} ---");

            for (var rowIdx = 0; rowIdx <= sheet.LastRowNum; rowIdx++)
            {
                var row = sheet.GetRow(rowIdx);
                if (row == null) continue;

                var cells = new List<string>();
                for (var colIdx = 0; colIdx < row.LastCellNum; colIdx++)
                {
                    var cell = row.GetCell(colIdx);
                    cells.Add(cell?.ToString() ?? "");
                }

                sb.AppendLine(string.Join("\t", cells));
            }
            sb.AppendLine();
        }

        return await Task.FromResult(sb.ToString());
    }

    private static async Task<string> ExtractDocxAsync(string filePath, CancellationToken ct)
    {
        using var stream = File.OpenRead(filePath);
        var doc = new XWPFDocument(stream);
        var sb = new System.Text.StringBuilder();

        foreach (var para in doc.Paragraphs)
            sb.AppendLine(para.Text);

        foreach (var table in doc.Tables)
        {
            foreach (var row in table.Rows)
            {
                var cells = row.GetTableCells().Select(c => c.GetText());
                sb.AppendLine(string.Join("\t", cells));
            }
        }

        return await Task.FromResult(sb.ToString());
    }

    private static async Task<string> ExtractMarkdownAsync(string filePath, CancellationToken ct)
    {
        var markdown = await File.ReadAllTextAsync(filePath, ct);
        var html = Markdown.ToHtml(markdown);
        var text = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        return text;
    }
}

public sealed class DocumentSection
{
    public string Heading { get; init; } = "";
    public int Level { get; init; }
    public string Content { get; set; } = "";
}
