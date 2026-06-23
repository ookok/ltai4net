using LTAI.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Context;

public sealed class SemanticCompressor
{
    private readonly EmbeddingClient? _embedder;
    private readonly ILogger<SemanticCompressor>? _logger;

    public SemanticCompressor(EmbeddingClient? embedder = null, ILogger<SemanticCompressor>? logger = null)
    {
        _embedder = embedder;
        _logger = logger;
    }

    public async Task<string> CompressSemanticallyAsync(string text, double targetRatio = 0.5, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(text) || targetRatio >= 1.0) return text;

        var sentences = SplitSentences(text);
        if (sentences.Count <= 3) return text;

        var targetLen = (int)(text.Length * targetRatio);
        if (targetLen < 100) targetLen = 100;

        if (_embedder == null)
        {
            return TruncateByPosition(sentences, targetLen);
        }

        try
        {
            var embeddings = await _embedder.GenerateBatchAsync(sentences.ToArray(), ct).ConfigureAwait(false);
            var centroid = AverageVector(embeddings);
            var scored = sentences.Select((s, i) => (Sentence: s, Score: CosineSimilarity(embeddings[i], centroid)))
                .OrderByDescending(x => x.Score)
                .ToList();

            var result = new List<string>();
            var currentLen = 0;
            foreach (var (s, _) in scored)
            {
                if (currentLen + s.Length > targetLen && result.Count > 0) break;
                result.Add(s);
                currentLen += s.Length;
            }

            var final = string.Join(" ", result.OrderBy(_ => sentences.IndexOf(_)));
            return final.Length < text.Length / 2 ? final : text;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "SemanticCompressor: embedding failed, fallback to positional truncation");
            return TruncateByPosition(sentences, targetLen);
        }
    }

    private static string TruncateByPosition(List<string> sentences, int targetLen)
    {
        var result = new List<string>();
        var len = 0;
        foreach (var s in sentences)
        {
            if (len + s.Length > targetLen && result.Count > 0) break;
            result.Add(s);
            len += s.Length;
        }
        return string.Join(" ", result);
    }

    private static List<string> SplitSentences(string text)
    {
        var parts = new List<string>();
        var span = text.AsSpan();
        int start = 0;
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] == '.' || span[i] == '!' || span[i] == '?' || span[i] == '\n')
            {
                var len = i - start + 1;
                if (len > 0) parts.Add(span[start..(i + 1)].ToString());
                start = i + 1;
            }
        }
        if (start < span.Length) parts.Add(span[start..].ToString());
        if (parts.Count == 0) parts.Add(text);
        return parts;
    }

    private static float[] AverageVector(float[][] vectors)
    {
        if (vectors.Length == 0) return [];
        var dim = vectors[0].Length;
        var avg = new float[dim];
        foreach (var v in vectors)
            for (int i = 0; i < dim; i++) avg[i] += v[i];
        for (int i = 0; i < dim; i++) avg[i] /= vectors.Length;
        return avg;
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        float dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        var mag = MathF.Sqrt(na) * MathF.Sqrt(nb);
        return mag > 0 ? dot / mag : 0;
    }
}
