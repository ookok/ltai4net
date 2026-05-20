namespace LTAI.TreeLLM.Heima;

public sealed class HeimaIntegration
{
    private readonly HeimaEncoder _encoder;
    private readonly HeimaDecoder _decoder;

    public HeimaIntegration(HeimaConfig? config = null)
    {
        _encoder = new(config);
        _decoder = new(config);
    }

    public List<ThinkingToken> CompressReasoning(string chainOfThought, out string stats)
    {
        var tokens = _encoder.Encode(chainOfThought);
        var (orig, comp, ratio, avgMi) = _encoder.GetCompressionStats(chainOfThought, tokens);
        stats = _decoder.GenerateInfoGapReport(tokens, orig);
        return tokens;
    }

    public string ExpandReasoning(List<ThinkingToken> tokens, bool compact = false)
    {
        return compact
            ? _decoder.DecodeToCompactText(tokens)
            : _decoder.DecodeToText(tokens);
    }

    public string CompressAndExpand(string chainOfThought, out (int orig, int comp, double ratio) stats, bool compact = false)
    {
        var tokens = CompressReasoning(chainOfThought, out var infoGap);
        var (orig, comp, ratio, _) = _encoder.GetCompressionStats(chainOfThought, tokens);
        stats = (orig, comp, ratio);
        return ExpandReasoning(tokens, compact);
    }

    public Dictionary<string, object> Evaluate(string chainOfThought)
    {
        var tokens = _encoder.Encode(chainOfThought);
        var (orig, comp, ratio, avgMi) = _encoder.GetCompressionStats(chainOfThought, tokens);
        var quality = _decoder.ComputeReconstructionQuality(tokens, chainOfThought);
        var expanded = _decoder.DecodeToText(tokens, includeConfidence: true);

        return new()
        {
            ["original_tokens"] = orig,
            ["thinking_tokens"] = tokens.Count,
            ["compressed_tokens"] = comp,
            ["compression_ratio"] = $"{ratio * 100:F1}%",
            ["avg_mutual_info"] = Math.Round(avgMi, 3),
            ["quality"] = quality,
            ["expanded_sample"] = expanded[..Math.Min(500, expanded.Length)]
        };
    }

    public static HeimaIntegration CreateForL2Supervisor()
    {
        return new(new HeimaConfig
        {
            EmbeddingDim = 64,
            ImportanceThreshold = 0.2,
            ConfidenceThreshold = 0.4,
            MaxThinkingTokens = 64,
            EnableMutualInfoTracking = true
        });
    }

    public static HeimaIntegration CreateForSessionCompressor()
    {
        return new(new HeimaConfig
        {
            EmbeddingDim = 32,
            ImportanceThreshold = 0.15,
            MaxThinkingTokens = 256,
            EnableMutualInfoTracking = false,
            EnableStatisticalCompression = true
        });
    }

    public static HeimaIntegration CreateForContextMoE()
    {
        return new(new HeimaConfig
        {
            EmbeddingDim = 128,
            ImportanceThreshold = 0.25,
            MaxThinkingTokens = 512,
            EnableMutualInfoTracking = true,
            EnableStatisticalCompression = true
        });
    }
}
