using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Knowledge.Core;

public sealed class SemanticCompactionEngine
{
    private readonly IChatClient _chatClient;
    private readonly StructMemory _structMemory;
    private readonly ILogger<SemanticCompactionEngine> _logger;

    public SemanticCompactionEngine(
        IChatClient chatClient,
        StructMemory structMemory,
        ILogger<SemanticCompactionEngine>? logger = null)
    {
        _chatClient = chatClient;
        _structMemory = structMemory;
        _logger = logger ?? NullLogger<SemanticCompactionEngine>.Instance;
    }

    public async Task<string> CompressColdBlock(string content, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Length < 200)
            return content;

        var prompt = $"""
            Summarize the following text into 2-3 concise sentences while preserving all key facts, 
            entities, and relationships. Remove redundancy and filler. Output only the summary.

            TEXT:
            {content[..Math.Min(content.Length, 4000)]}
            """;

        try
        {
            var response = await _chatClient.GetResponseAsync(prompt, cancellationToken: ct).ConfigureAwait(false);
            var summary = response.Text?.Trim() ?? "";
            return summary.Length > 5 ? summary : content[..Math.Min(500, content.Length)];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Semantic compaction failed, falling back to heuristic");
            return CompressHeuristic(content);
        }
    }

    public async Task<string> MergeSimilarBlocks(
        List<string> similarBlocks, CancellationToken ct = default)
    {
        if (similarBlocks == null || similarBlocks.Count <= 1)
            return similarBlocks?.FirstOrDefault() ?? "";

        var combined = string.Join("\n\n---\n\n", similarBlocks);
        var prompt = $"""
            Merge the following related text blocks into a single coherent summary paragraph. 
            Remove duplicate information, resolve any contradictions (keep the more specific version), 
            and preserve all unique facts. Output only the merged text.

            BLOCKS:
            {combined[..Math.Min(combined.Length, 4000)]}
            """;

        try
        {
            var response = await _chatClient.GetResponseAsync(prompt, cancellationToken: ct).ConfigureAwait(false);
            var merged = response.Text?.Trim() ?? "";
            return merged.Length > 10 ? merged : similarBlocks.FirstOrDefault() ?? "";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Block merge failed");
            return similarBlocks.FirstOrDefault() ?? "";
        }
    }

    public async Task<bool> ShouldExpire(string knowledgeBlock, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(knowledgeBlock) || knowledgeBlock.Length < 100)
            return false;

        var prompt = $"""
            Determine if the following knowledge is likely outdated, superseded, or no longer accurate.
            Answer with a single word: "YES" or "NO".

            KNOWLEDGE:
            {knowledgeBlock[..Math.Min(knowledgeBlock.Length, 2000)]}
            """;

        try
        {
            var response = await _chatClient.GetResponseAsync(prompt, cancellationToken: ct).ConfigureAwait(false);
            return (response.Text ?? "").Trim().StartsWith("YES", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public async Task<int> CompactAndDeduplicateAsync(CancellationToken ct = default)
    {
        var contextBlock = _structMemory.GetContextBlock();
        if (string.IsNullOrWhiteSpace(contextBlock) || contextBlock.Length < 500)
            return 0;

        var blockSnippet = contextBlock[..Math.Min(contextBlock.Length, 4000)];
        var prompt = "Analyze this memory context and:\n" +
            "1. Identify entries that say essentially the same thing (near-duplicates)\n" +
            "2. Identify entries that are likely outdated or contradicted by newer entries\n" +
            "Return a JSON object: { \"duplicates\": [[\"id1\",\"id2\"]], \"expired\": [\"id3\"] }\n\n" +
            "CONTEXT:\n" + blockSnippet;

        try
        {
            var response = await _chatClient.GetResponseAsync(prompt, cancellationToken: ct).ConfigureAwait(false);
            _logger.LogInformation("Semantic compaction analyzed context ({Len} chars)", contextBlock.Length);
            return 1;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Semantic compaction analysis failed");
            return 0;
        }
    }

    public async Task<string> CompressAndMergeAsync(
        string content, CancellationToken ct = default)
    {
        var compressed = await CompressColdBlock(content, ct).ConfigureAwait(false);
        return compressed;
    }

    private static readonly char[] s_sentenceSeparators = { '.', '!', '?', '。', '！', '？' };

    private static string CompressHeuristic(string content)
    {
        var sentences = content.Split(s_sentenceSeparators, StringSplitOptions.RemoveEmptyEntries);

        if (sentences.Length <= 3)
            return content[..Math.Min(500, content.Length)];

        var firstSentences = sentences.Take(3);
        return string.Join(". ", firstSentences) + ".";
    }
}
