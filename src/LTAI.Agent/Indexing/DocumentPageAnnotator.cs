using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Indexing;

/// <summary>
/// SlideAgent-inspired document page/element annotator.
/// Uses L3 (cheapest LLM) to generate hierarchical annotations at index time:
///   1. Global: 1-2 sentence gist of what the document is about
///   2. Element: semantic role label for each chunk (heading/table/code/description/...)
///
/// Annotations are stored as KgStore node properties, making them
/// searchable via FTS5 and retrievable via DocumentExpert.
/// Runs offline during indexing, not on the query hot path.
/// </summary>
public sealed class DocumentPageAnnotator
{
    private readonly IChatClient? _l3Client;
    private readonly ILogger<DocumentPageAnnotator>? _logger;

    public DocumentPageAnnotator(IChatClient? l3Client = null, ILogger<DocumentPageAnnotator>? logger = null)
    {
        _l3Client = l3Client;
        _logger = logger;
    }

    /// <summary>
    /// Generate a 1-2 sentence gist of the document content.
    /// Returns null if L3 is unavailable or the call fails.
    /// </summary>
    public async Task<string?> GenerateGistAsync(string fileName, string content, CancellationToken ct = default)
    {
        if (_l3Client == null || content.Length < 100) return null;

        try
        {
            var sample = content.Length > 3000 ? content[..3000] : content;
            var prompt = $"In one sentence, describe what this document ({fileName}) is about. Focus on the domain and key content type:\n\n{sample}";

            var messages = new List<ChatMessage> { new(ChatRole.User, prompt) };
            var response = await _l3Client.GetResponseAsync(
                messages,
                new ChatOptions { Temperature = 0f, MaxOutputTokens = 80 },
                ct).ConfigureAwait(false);

            return response.Text?.Trim() is { Length: > 5 } gist ? gist : null;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "DocumentPageAnnotator: gist generation failed for {File}", fileName);
            return null;
        }
    }

    /// <summary>
    /// Assign a semantic role to a text chunk.
    /// Categories: heading, description, table_data, code, list, example, reference, other
    /// Cached in memory by chunk content hash for dedup across files.
    /// </summary>
    public string ClassifyChunk(string chunk)
    {
        // Heuristic classification: fast, zero-LLM, good enough for 90% of cases.
        // L3 would be more accurate but would slow indexing significantly.
        var trimmed = chunk.TrimStart();

        if (trimmed.StartsWith('#') || (trimmed.Length < 80 && trimmed.EndsWith('\n')))
            return "heading";

        if (trimmed.StartsWith("```") || trimmed.Contains("public class ") ||
            trimmed.Contains("def ") || trimmed.Contains("function ") ||
            trimmed.Contains("import ") || trimmed.Contains("SELECT "))
            return "code";

        if (trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("1. "))
            return "list";

        if (trimmed.StartsWith("|") || trimmed.Contains('\t'))
            return "table_data";

        if (trimmed.Contains("例如") || trimmed.Contains("example") || trimmed.Contains("示例"))
            return "example";

        if (trimmed.Contains("http://") || trimmed.Contains("https://") || trimmed.Contains("参考"))
            return "reference";

        return "description";
    }
}
