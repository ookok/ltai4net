using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LTAI.Knowledge.Document.Interfaces;
using LTAI.Knowledge.Document.Models;

namespace LTAI.Knowledge.Document;

public sealed class UniversalFileParser
{
    private readonly Dictionary<string, IDocumentParser> _parsers = new();
    private readonly Lock _registerLock = new();

    public UniversalFileParser(IEnumerable<IDocumentParser> parsers)
    {
        foreach (var parser in parsers)
            RegisterParser(parser);
    }

    public void RegisterParser(IDocumentParser parser)
    {
        lock (_registerLock)
        {
            foreach (var ext in parser.SupportedExtensions)
            {
                var key = ext.ToLowerInvariant();
                _parsers[key] = parser;
            }
        }
    }

    public async Task<ParseResult> ParseAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            if (!File.Exists(filePath))
                return ParseResult.Fail(filePath, "File not found", sw.ElapsedMilliseconds);

            var format = DetectFormat(filePath);
            var ext = Path.GetExtension(filePath).ToLowerInvariant();

            if (_parsers.TryGetValue(ext, out var parser))
            {
                var result = await parser.ParseAsync(filePath, cancellationToken).ConfigureAwait(false);
                sw.Stop();
                return result with { ElapsedMs = sw.ElapsedMilliseconds };
            }

            var text = await TryReadTextAsync(filePath, cancellationToken).ConfigureAwait(false);
            sw.Stop();

            if (text != null)
                return ParseResult.Ok(filePath, format, "text", text, elapsed: sw.ElapsedMilliseconds);

            return ParseResult.Fail(filePath, $"No parser available for format: {format}", sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            return ParseResult.Fail(filePath, ex.Message, sw.ElapsedMilliseconds);
        }
    }

    public string DetectFormat(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        if (!File.Exists(filePath))
            return ext.TrimStart('.');

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var magic = new byte[16];
            var read = fs.Read(magic, 0, magic.Length);

            if (read >= 4)
            {
                if (magic[0] == 0xFF && magic[1] == 0xD8 && magic[2] == 0xFF)
                    return "jpeg";
                if (magic[0] == 0x89 && magic[1] == 0x50 && magic[2] == 0x4E && magic[3] == 0x47)
                    return "png";
                if (magic[0] == 0x47 && magic[1] == 0x49 && magic[2] == 0x46)
                    return "gif";
                if (magic[0] == 0x25 && magic[1] == 0x50 && magic[2] == 0x44 && magic[3] == 0x46)
                    return "pdf";
                if (magic[0] == 0x50 && magic[1] == 0x4B && magic[2] == 0x03 && magic[3] == 0x04)
                {
                    if (ext is ".docx") return "docx";
                    if (ext is ".xlsx") return "xlsx";
                    if (ext is ".pptx") return "pptx";
                    return "zip";
                }
                if (magic[0] == 0xD0 && magic[1] == 0xCF && magic[2] == 0x11 && magic[3] == 0xE0)
                    return "doc";
                if (magic[0] == 0x52 && magic[1] == 0x61 && magic[2] == 0x72 && magic[3] == 0x21)
                    return "rar";
                if (magic[0] == 0x37 && magic[1] == 0x7A && magic[2] == 0xBC && magic[3] == 0xAF)
                    return "7z";
                if (magic[0] == 0x53 && magic[1] == 0x51 && magic[2] == 0x4C && magic[3] == 0x69)
                    return "sqlite";
            }
        }
        catch { /* non-fatal */ }

        return ext.TrimStart('.');
    }

    public IReadOnlyList<ParserInfo> ListParsers()
    {
        return _parsers.Values.Distinct().Select(p => new ParserInfo
        {
            Name = p.FormatName,
            Extensions = p.SupportedExtensions.ToArray(),
            Description = p.FormatName,
            IsAvailable = true
        }).ToList();
    }

    private static async Task<string?> TryReadTextAsync(string filePath, CancellationToken cancellationToken)
    {
        var textExtensions = new HashSet<string> { ".txt", ".md", ".csv", ".tsv", ".json", ".xml", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".html", ".htm", ".css", ".js", ".ts", ".py", ".cs", ".go", ".rs", ".java", ".log" };
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        if (textExtensions.Contains(ext))
        {
            return await File.ReadAllTextAsync(filePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var text = await File.ReadAllTextAsync(filePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            if (IsLikelyText(text))
                return text;
        }
        catch { /* non-fatal */ }

        return null;
    }

    private static bool IsLikelyText(string content)
    {
        if (content.Length > 1024 * 1024)
            return false;

        var sample = content[..Math.Min(content.Length, 1024)];
        var binaryChars = sample.Count(c => char.IsControl(c) && c != '\r' && c != '\n' && c != '\t');
        return binaryChars < sample.Length * 0.05;
    }
}
