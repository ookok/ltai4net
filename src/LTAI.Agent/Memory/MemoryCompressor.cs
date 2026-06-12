using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Memory;

/// <summary>
/// Semantic memory compressor. Generates 1-sentence summaries of long-form
/// content using L3 (cheapest LLM), replacing hard truncation with lossy
/// but semantically meaningful compression.
///
/// Inspired by Fuzzy-Trace Theory: gist representations survive,
/// verbatim details fade — only the essential "trace" is retained for retrieval.
/// </summary>
public sealed class MemoryCompressor
{
    private readonly IChatClient? _l3Client;
    private readonly ILogger<MemoryCompressor>? _logger;

    public MemoryCompressor(IChatClient? l3Client = null, ILogger<MemoryCompressor>? logger = null)
    {
        _l3Client = l3Client;
        _logger = logger;
    }

    /// <summary>
    /// Compress content to a single-sentence summary using L3.
    /// Falls back to smart truncation (last sentence boundary) if L3 unavailable.
    /// </summary>
    public async Task<string> CompressAsync(string content, int maxChars = 200, CancellationToken ct = default)
    {
        if (content.Length <= maxChars) return content;

        if (_l3Client != null)
        {
            try
            {
                var prompt = $"Summarize this in one sentence (max {maxChars/4} characters):\n\n{content}";
                var messages = new List<ChatMessage> { new(ChatRole.User, prompt) };
                var response = await _l3Client.GetResponseAsync(
                    messages,
                    new ChatOptions { Temperature = 0f, MaxOutputTokens = 80 },
                    ct).ConfigureAwait(false);
                var summary = response.Text?.Trim();
                if (!string.IsNullOrEmpty(summary) && summary.Length < content.Length * 0.8)
                    return summary;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "MemoryCompressor: L3 summary failed, using smart truncation");
            }
        }

        return SmartTruncate(content, maxChars);
    }

    /// <summary>
    /// Truncate at the last complete sentence boundary before maxChars,
    /// rather than cutting mid-word. Preserves semantic integrity.
    /// </summary>
    public static string SmartTruncate(string content, int maxChars = 200)
    {
        if (content.Length <= maxChars) return content;

        var span = content.AsSpan(0, Math.Min(maxChars + 50, content.Length));
        var breakPoints = new[] { '。', '.', '！', '!', '？', '?', '\n', '；', ';' };

        int bestBreak = -1;
        foreach (var ch in breakPoints)
        {
            var idx = span[..Math.Min(maxChars, span.Length)].LastIndexOf(ch);
            if (idx > bestBreak && idx > maxChars * 0.5)
                bestBreak = idx;
        }

        return bestBreak > 0
            ? content[..(bestBreak + 1)]
            : content[..maxChars] + "...";
    }
}
