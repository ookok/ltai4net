using LTAI.Document.Models;

namespace LTAI.Document.Interfaces;

public interface IDocumentParser
{
    Task<ParseResult> ParseAsync(string filePath, CancellationToken cancellationToken = default);

    bool CanParse(string filePath);

    string FormatName { get; }

    IReadOnlyList<string> SupportedExtensions { get; }
}
