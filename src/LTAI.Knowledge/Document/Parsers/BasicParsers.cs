using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using LTAI.Knowledge.Document.Interfaces;
using LTAI.Knowledge.Document.Models;

namespace LTAI.Knowledge.Document.Parsers;

public sealed class JsonParser : IDocumentParser
{
    public string FormatName => "JSON";
    public IReadOnlyList<string> SupportedExtensions => new[] { ".json" };

    public bool CanParse(string filePath) =>
        Path.GetExtension(filePath).Equals(".json", StringComparison.OrdinalIgnoreCase);

    public async Task<ParseResult> ParseAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var text = await File.ReadAllTextAsync(filePath, Encoding.UTF8, cancellationToken);
        var metadata = new Dictionary<string, string>
        {
            ["size"] = new FileInfo(filePath).Length.ToString(),
            ["chars"] = text.Length.ToString()
        };

        try
        {
            using var doc = JsonDocument.Parse(text);
            var rootKind = doc.RootElement.ValueKind;
            metadata["root_type"] = rootKind.ToString();

            if (rootKind == JsonValueKind.Array)
                metadata["array_len"] = doc.RootElement.GetArrayLength().ToString();
            else if (rootKind == JsonValueKind.Object)
                metadata["object_keys"] = doc.RootElement.EnumerateObject().Count().ToString();
        }
        catch { /* non-fatal */ }

        return ParseResult.Ok(filePath, "json", "json", text, metadata);
    }
}

public sealed class XmlParser : IDocumentParser
{
    public string FormatName => "XML";
    public IReadOnlyList<string> SupportedExtensions => new[] { ".xml", ".html", ".htm" };

    public bool CanParse(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext is ".xml" or ".html" or ".htm";
    }

    public async Task<ParseResult> ParseAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var text = await File.ReadAllTextAsync(filePath, Encoding.UTF8, cancellationToken);
        var metadata = new Dictionary<string, string>
        {
            ["size"] = new FileInfo(filePath).Length.ToString()
        };

        try
        {
            var doc = XDocument.Parse(text);
            metadata["root_element"] = doc.Root?.Name.LocalName ?? "unknown";
            metadata["elements"] = doc.Descendants().Count().ToString();
        }
        catch { /* non-fatal */ }

        return ParseResult.Ok(filePath, "xml", "xml", text, metadata);
    }
}

public sealed class CsvParser : IDocumentParser
{
    public string FormatName => "CSV";
    public IReadOnlyList<string> SupportedExtensions => new[] { ".csv", ".tsv" };

    public bool CanParse(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext is ".csv" or ".tsv";
    }

    public async Task<ParseResult> ParseAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var text = await File.ReadAllTextAsync(filePath, Encoding.UTF8, cancellationToken);
        var separator = Path.GetExtension(filePath).ToLowerInvariant() == ".tsv" ? '\t' : ',';

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var metadata = new Dictionary<string, string>
        {
            ["rows"] = lines.Length.ToString(),
            ["separator"] = separator == '\t' ? "tab" : "comma"
        };

        var tables = new List<Dictionary<string, object?>>();
        if (lines.Length > 1)
        {
            var headers = lines[0].Split(separator).Select(h => h.Trim()).ToArray();
            metadata["columns"] = headers.Length.ToString();
            metadata["headers"] = string.Join(", ", headers.Take(10));

            for (var i = 1; i < Math.Min(lines.Length, 100); i++)
            {
                var values = lines[i].Split(separator);
                var row = new Dictionary<string, object?>();
                for (var j = 0; j < Math.Min(headers.Length, values.Length); j++)
                    row[headers[j]] = values[j].Trim();
                tables.Add(row);
            }
        }

        return new ParseResult
        {
            FilePath = filePath,
            Format = "csv",
            Success = true,
            ParserUsed = "csv",
            Text = text,
            Tables = tables,
            Metadata = metadata
        };
    }
}

public sealed class TextParser : IDocumentParser
{
    public string FormatName => "Plain Text";
    public IReadOnlyList<string> SupportedExtensions => new[] { ".txt", ".md", ".log", ".ini", ".cfg", ".yaml", ".yml", ".toml" };

    public bool CanParse(string filePath) => true;

    public async Task<ParseResult> ParseAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var text = await File.ReadAllTextAsync(filePath, Encoding.UTF8, cancellationToken);
        var metadata = new Dictionary<string, string>
        {
            ["size"] = new FileInfo(filePath).Length.ToString(),
            ["lines"] = text.Count(c => c == '\n').ToString(),
            ["chars"] = text.Length.ToString()
        };

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var format = ext.TrimStart('.');
        if (string.IsNullOrEmpty(format)) format = "text";

        return ParseResult.Ok(filePath, format, "text", text, metadata);
    }
}
