using System.Text;
using LTAI.AI;

namespace LTAI.Agent.Experts.Routing;

/// <summary>
/// Merges, deduplicates, and resolves conflicts across multiple expert responses.
/// Produces a single AggregatedContext block for injection into the LLM prompt.
///
/// Pipeline:
///   1. Sort by confidence descending
///   2. Deduplicate overlapping content (MinHash text similarity > 0.8)
///   3. Merge citations
///   4. Build Markdown context block
/// </summary>
public sealed class ExpertAggregator
{
    private const float DupThreshold = 0.8f;

    public ExpertAggregator(EmbeddingClient? embedder = null)
    {
        // embedder accepted for API compatibility; Phase 2 uses text MinHash
        // which is 50x faster than ONNX embeddings for small response counts.
    }

    /// <summary>
    /// Aggregate expert responses into a single context block.
    /// Responses with NoAnswer=true are filtered out.
    /// </summary>
    public Task<AggregatedContext> AggregateAsync(
        IReadOnlyList<ExpertResponse> responses,
        CancellationToken ct = default)
    {
        var valid = responses
            .Where(r => !r.NoAnswer && !string.IsNullOrWhiteSpace(r.Content))
            .OrderByDescending(r => r.Confidence)
            .ToList();

        if (valid.Count == 0)
        {
            var noAnswerFirst = responses.FirstOrDefault(r => r.NoAnswer && r.ClarifyQuestion != null);
            return Task.FromResult(new AggregatedContext(
                noAnswerFirst?.ClarifyQuestion ?? "No expert could answer this query.",
                [], 0f, false));
        }

        var dedupedContent = Deduplicate(valid);
        var allCitations = valid.SelectMany(r => r.Citations).DistinctBy(c => c.Id).ToList();
        var avgConfidence = valid.Average(r => r.Confidence);

        var sb = new StringBuilder();
        sb.AppendLine("## Expert Context (aggregated from multiple knowledge sources)\n");

        for (int i = 0; i < dedupedContent.Count; i++)
        {
            var (response, isDeduped) = dedupedContent[i];
            if (isDeduped)
            {
                sb.AppendLine($"> *[Content from {response.ExpertId} overlaps with earlier results — merged]*");
            }
            else
            {
                var sourceTag = response.Provenance.SourceGraph;
                sb.AppendLine($"### Source: {response.ExpertId} ({sourceTag}, confidence: {response.Confidence:P0})");
                sb.AppendLine(TruncateContent(response.Content, 3000));
                sb.AppendLine();
            }
        }

        sb.AppendLine("---");
        sb.AppendLine($"*Aggregate confidence: {avgConfidence:P0} | {allCitations.Count} citations from {valid.Count} experts*");

        return Task.FromResult(new AggregatedContext(sb.ToString(), allCitations, avgConfidence, true));
    }

    private static List<(ExpertResponse Response, bool IsDeduped)> Deduplicate(
        List<ExpertResponse> responses)
    {
        if (responses.Count <= 1)
            return responses.Select(r => (r, false)).ToList();

        var result = new List<(ExpertResponse Response, bool IsDeduped)> { (responses[0], false) };
        var prevHashes = new List<(ulong[] MinHash, int Index)> { (MinHash(responses[0].Content), 0) };

        for (int i = 1; i < responses.Count; i++)
        {
            var hash = MinHash(responses[i].Content);
            bool isDuplicate = false;

            for (int j = 0; j < prevHashes.Count && !isDuplicate; j++)
            {
                if (result[prevHashes[j].Index].IsDeduped) continue;
                var sim = JaccardSimilarity(hash, prevHashes[j].MinHash);
                if (sim > DupThreshold) isDuplicate = true;
            }

            if (!isDuplicate)
                prevHashes.Add((hash, i));

            result.Add((responses[i], isDuplicate));
        }

        return result;
    }

    /// <summary>
    /// Simple MinHash: 32 hashes of character 4-grams.
    /// ~1μs per hash vs ~50ms per ONNX embedding call.
    /// </summary>
    private static ulong[] MinHash(string text)
    {
        const int numHashes = 32;
        var hashes = new ulong[numHashes];
        Array.Fill(hashes, ulong.MaxValue);
        var span = text.AsSpan();

        for (int i = 0; i <= span.Length - 4; i++)
        {
            var gram = span.Slice(i, 4);
            var h = Fnv1a(gram);
            for (int j = 0; j < numHashes; j++)
            {
                var rotated = RotateLeft(h, j * 7) ^ (137 + (ulong)j * 31);
                if (rotated < hashes[j])
                    hashes[j] = rotated;
            }
        }

        return hashes;
    }

    private static float JaccardSimilarity(ulong[] a, ulong[] b)
    {
        int match = 0;
        for (int i = 0; i < a.Length; i++)
            if (a[i] == b[i]) match++;
        return (float)match / a.Length;
    }

    private static ulong Fnv1a(ReadOnlySpan<char> s)
    {
        const ulong prime = 1099511628211;
        ulong hash = 14695981039346656037;
        for (int i = 0; i < s.Length; i++)
        {
            hash ^= s[i];
            hash *= prime;
        }
        return hash;
    }

    private static ulong RotateLeft(ulong x, int n) => (x << n) | (x >> (64 - n));

    private static string TruncateContent(string content, int maxChars)
    {
        if (content.Length <= maxChars) return content;
        var breakPoint = content.LastIndexOf('\n', maxChars);
        if (breakPoint > maxChars * 0.7)
            return content[..breakPoint] + $"\n... (truncated, {content.Length - breakPoint} more chars)";
        return content[..maxChars] + $"... (truncated, {content.Length - maxChars} more chars)";
    }
}
