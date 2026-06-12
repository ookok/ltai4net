using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Memory;

/// <summary>
/// AnchorMem-inspired atomic fact extractor.
/// Extracts 1-3 standalone, self-contained statements from memory content
/// using L3 (cheapest LLM). These facts serve as high-precision retrieval
/// anchors while the original content remains frozen as generation context.
///
/// Inspired by the Proust phenomenon: atomic facts act as the "madeleine moment"
/// that triggers recall of the full original context.
/// </summary>
public sealed class FactExtractor
{
    private readonly IChatClient? _l3Client;
    private readonly ILogger<FactExtractor>? _logger;

    public FactExtractor(IChatClient? l3Client = null, ILogger<FactExtractor>? logger = null)
    {
        _l3Client = l3Client;
        _logger = logger;
    }

    /// <summary>
    /// Extract 1-3 atomic, self-contained facts from content.
    /// Each fact is a standalone sentence that can be understood without
    /// the surrounding context. Used as retrieval anchors.
    /// Returns empty list if L3 is unavailable or content is too short.
    /// </summary>
    public async Task<IReadOnlyList<string>> ExtractFactsAsync(string content, CancellationToken ct = default)
    {
        if (_l3Client == null || content.Length < 20) return [];

        try
        {
            var prompt = $$"""
                Extract 1-3 atomic, self-contained facts from this text.
                Each fact must be a standalone sentence understandable without context.
                Return ONLY the facts, one per line. No numbering, no prefixes.
                
                Text:
                {{content}}
                """;

            var messages = new List<ChatMessage> { new(ChatRole.User, prompt) };
            var response = await _l3Client.GetResponseAsync(
                messages,
                new ChatOptions { Temperature = 0f, MaxOutputTokens = 150 },
                ct).ConfigureAwait(false);

            var text = response.Text?.Trim();
            if (string.IsNullOrEmpty(text)) return [];

            var facts = new List<string>();
            foreach (var line in text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim().TrimStart('-', '*', ' ', '\t', '0', '1', '2', '3', '.');
                if (trimmed.Length > 5 && !trimmed.Contains("Extract") && !trimmed.Contains("fact"))
                    facts.Add(trimmed);
            }

            return facts;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "FactExtractor: extraction failed");
            return [];
        }
    }
}
