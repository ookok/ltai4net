using LTAI.Knowledge.Document.Models;

namespace LTAI.Knowledge.Document.Interfaces;

public interface IDocumentParser
{
    Task<ParseResult> ParseAsync(string filePath, CancellationToken cancellationToken = default);

    bool CanParse(string filePath);

    string FormatName { get; }

    IReadOnlyList<string> SupportedExtensions { get; }
}
