using System.Text.Json.Serialization;

namespace LTAI.Knowledge.Document.Models;

public sealed record ParseResult
{
    [JsonPropertyName("filepath")]
    public string FilePath { get; init; } = string.Empty;

    [JsonPropertyName("format")]
    public string Format { get; init; } = string.Empty;

    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("parser_used")]
    public string? ParserUsed { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("tables")]
    public List<Dictionary<string, object?>> Tables { get; init; } = new();

    [JsonPropertyName("metadata")]
    public Dictionary<string, string> Metadata { get; init; } = new();

    [JsonPropertyName("images")]
    public List<string> Images { get; init; } = new();

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("elapsed_ms")]
    public double ElapsedMs { get; init; }

    public static ParseResult Ok(string filepath, string format, string parser, string? text,
        Dictionary<string, string>? metadata = null, double elapsed = 0) =>
        new()
        {
            FilePath = filepath,
            Format = format,
            Success = true,
            ParserUsed = parser,
            Text = text,
            Metadata = metadata ?? new Dictionary<string, string>(),
            ElapsedMs = elapsed
        };

    public static ParseResult Fail(string filepath, string error, double elapsed = 0) =>
        new()
        {
            FilePath = filepath,
            Success = false,
            Error = error,
            ElapsedMs = elapsed
        };
}

public sealed class FileFormat
{
    public string Extension { get; init; } = string.Empty;
    public string FormatName { get; init; } = string.Empty;
    public string? MimeType { get; init; }
}

public sealed class ParserInfo
{
    public string Name { get; init; } = string.Empty;
    public string[] Extensions { get; init; } = [];
    public string Description { get; init; } = string.Empty;
    public bool IsAvailable { get; init; }
}
