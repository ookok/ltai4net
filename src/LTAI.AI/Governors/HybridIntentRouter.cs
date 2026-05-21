using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed record IntentResult
{
    public string Label { get; init; } = "deep";
    public float Confidence { get; init; }
    public string Source { get; init; } = "unknown";
    public float Complexity { get; init; }
}

public sealed record IntentExample
{
    public string Text { get; init; } = "";
    public string Label { get; init; } = "";
}

public sealed class HybridIntentRouter
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly LocalIntentClassifier _localClassifier;
    private readonly IEmbeddingGenerator<string, Embedding<float>>? _l0Embedder;
    private readonly ILogger<HybridIntentRouter> _logger;
    private readonly ConcurrentDictionary<string, IntentExample> _knownExamples = new();
    private readonly float _localConfidenceThreshold;
    private readonly float _l0SimilarityThreshold;

    public HybridIntentRouter(
        LocalIntentClassifier localClassifier,
        IEmbeddingGenerator<string, Embedding<float>>? l0Embedder = null,
        ILogger<HybridIntentRouter>? logger = null,
        float localConfidenceThreshold = 0.7f,
        float l0SimilarityThreshold = 0.65f)
    {
        _localClassifier = localClassifier;
        _l0Embedder = l0Embedder;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<HybridIntentRouter>.Instance;
        _localConfidenceThreshold = localConfidenceThreshold;
        _l0SimilarityThreshold = l0SimilarityThreshold;
        SeedKnownExamples();
    }

    public async Task<IntentResult> ClassifyAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new IntentResult { Label = "deep", Confidence = 1.0f, Source = "empty" };

        var localResult = _localClassifier.Classify(query);
        _logger.LogDebug("Local classifier: label={Label}, confidence={Conf:F2}", localResult.Label, localResult.Confidence);

        if (localResult.Confidence >= _localConfidenceThreshold)
        {
            return new IntentResult
            {
                Label = localResult.Label,
                Confidence = localResult.Confidence,
                Source = "local",
                Complexity = localResult.Complexity
            };
        }

        if (_l0Embedder != null)
        {
            var l0Result = await ClassifyWithL0Async(query, ct);
            if (l0Result != null)
            {
                _logger.LogDebug("L0 fallback: label={Label}, similarity={Sim:F2}", l0Result.Label, l0Result.Confidence);
                return l0Result with { Source = "l0_embedding" };
            }
        }

        _logger.LogDebug("Defaulting to deep (no confident classification)");
        return new IntentResult { Label = "deep", Confidence = 0.5f, Source = "default", Complexity = 0.6f };
    }

    private async Task<IntentResult?> ClassifyWithL0Async(string query, CancellationToken ct)
    {
        try
        {
            var queryEmbedding = await _l0Embedder.GenerateAsync(query, cancellationToken: ct);
            var queryVector = queryEmbedding.Vector.ToArray();

            float bestSimilarity = 0;
            string bestLabel = "deep";

            foreach (var example in _knownExamples.Values)
            {
                var exampleEmbedding = await _l0Embedder.GenerateAsync(example.Text, cancellationToken: ct);
                var exampleVector = exampleEmbedding.Vector.ToArray();
                var similarity = CosineSimilarity(queryVector, exampleVector);

                if (similarity > bestSimilarity)
                {
                    bestSimilarity = similarity;
                    bestLabel = example.Label;
                }
            }

            if (bestSimilarity >= _l0SimilarityThreshold)
            {
                return new IntentResult
                {
                    Label = bestLabel,
                    Confidence = bestSimilarity,
                    Complexity = 0.5f
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "L0 embedding fallback failed");
        }

        return null;
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        float dot = 0, normA = 0, normB = 0;
        int len = Math.Min(a.Length, b.Length);
        for (int i = 0; i < len; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        if (normA == 0 || normB == 0) return 0;
        return dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
    }

    private void SeedKnownExamples()
    {
        var examples = new[]
        {
            new IntentExample { Text = "帮我写一个排序函数", Label = "fast" },
            new IntentExample { Text = "这段代码有什么bug", Label = "deep" },
            new IntentExample { Text = "你好", Label = "fast" },
            new IntentExample { Text = "分析一下这个架构的优劣", Label = "deep" },
            new IntentExample { Text = "什么是REST API", Label = "fast" },
            new IntentExample { Text = "设计一个分布式系统的容错方案", Label = "deep" },
            new IntentExample { Text = "重构这个类", Label = "deep" },
            new IntentExample { Text = "谢谢", Label = "fast" },
            new IntentExample { Text = "为什么这个查询很慢", Label = "deep" },
            new IntentExample { Text = "帮我优化这段SQL", Label = "deep" },
            new IntentExample { Text = "解释一下这段代码", Label = "fast" },
            new IntentExample { Text = "规划一个微服务迁移方案", Label = "deep" },
        };

        foreach (var ex in examples)
            _knownExamples[ex.Text] = ex;
    }

    public void AddExample(string text, string label)
    {
        _knownExamples[text] = new IntentExample { Text = text, Label = label };
    }

    public int ExampleCount => _knownExamples.Count;
}
