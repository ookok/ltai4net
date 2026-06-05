using LTAI.AI;

namespace LTAI.Agent.Memory;

/// <summary>
/// Embedding-based wing classifier for the Palace memory system.
/// Uses FastEmb (BM25 heuristic, zero ONNX load) to classify a text
/// into one of 6 known wings (project/code/user/system/architecture/config).
/// Replaces brittle keyword-contains heuristic.
/// </summary>
internal static class WingClassifier
{
    private static readonly Dictionary<string, string[]> KnownWings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["project"] = ["project roadmap task feature sprint milestone release backlog epic user_story"],
        ["code"] = ["code class function method variable api interface implementation debug bug fix refactor test"],
        ["user"] = ["user preference profile setting configuration personal habit style"],
        ["system"] = ["system server docker deployment infrastructure network cpu memory disk"],
        ["architecture"] = ["architecture design pattern component layer module dependency abstraction decoupling"],
        ["config"] = ["config configuration setting option parameter environment variable ini yaml toml"],
    };

    private static readonly Lazy<Dictionary<string, float[]>> _wingEmbeddings = new(() =>
    {
        const int dim = 384;
        return KnownWings.ToDictionary(
            kv => kv.Key,
            kv => EmbeddingClient.FastEmb(string.Join(" ", kv.Value), dim),
            StringComparer.OrdinalIgnoreCase);
    }, true);

    /// <summary>
    /// Classify a text into a wing using FastEmb cosine similarity.
    /// Returns null if no wing scores above threshold (0.30).
    /// </summary>
    internal static string? Classify(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        const int dim = 384;
        const float threshold = 0.30f;
        var emb = EmbeddingClient.FastEmb(text, dim);
        var wings = _wingEmbeddings.Value;
        string? bestWing = null;
        float bestScore = -1f;

        foreach (var (wing, wingEmb) in wings)
        {
            var score = CosineSimilarity(emb, wingEmb);
            if (score > bestScore)
            {
                bestScore = score;
                bestWing = wing;
            }
        }

        return bestScore >= threshold ? bestWing : null;
    }

    internal static string? ClassifyFromMessages(IEnumerable<Microsoft.Extensions.AI.ChatMessage>? messages)
    {
        if (messages == null) return null;
        var text = string.Join(' ', messages
            .Where(m => !string.IsNullOrWhiteSpace(m.Text))
            .Select(m => m.Text));
        return Classify(text);
    }

    private static float CosineSimilarity(float[] a, float[] b)
        => LTAI.AI.VectorMath.CosineSimilarity(a.AsSpan(), b.AsSpan());
}
