using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using LTAI.Core.System;

namespace LTAI.TreeLLM.Heima;

public sealed class ThinkingToken
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public double[] Embedding { get; set; } = Array.Empty<double>();
    public string KeyEntity { get; set; } = "";
    public string Relation { get; set; } = "";
    public double Confidence { get; set; } = 1.0;
    public double Importance { get; set; }
    public string StepType { get; set; } = "";
    public int OriginalLength { get; set; }
    public double MutualInfo { get; set; } = 1.0;

    public int CompressedSize => Embedding.Length * 8 + Encoding.UTF8.GetByteCount(KeyEntity + Relation);
    public double CompressionRatio => OriginalLength > 0 ? (double)CompressedSize / OriginalLength : 0;
}

public sealed class HeimaConfig
{
    public int EmbeddingDim { get; set; } = 64;
    public double ImportanceThreshold { get; set; } = 0.3;
    public double ConfidenceThreshold { get; set; } = 0.5;
    public int MaxThinkingTokens { get; set; } = 128;
    public bool EnableMutualInfoTracking { get; set; } = true;
    public bool EnableStatisticalCompression { get; set; } = true;
}

public sealed class HeimaEncoder
{
    private readonly HeimaConfig _config;
    private readonly Random _rng = new(42);
    private readonly ConcurrentDictionary<string, double[]> _entityEmbeddings = new();

    public HeimaEncoder(HeimaConfig? config = null) => _config = config ?? new();

    public List<ThinkingToken> Encode(string chainOfThought)
    {
        var tokens = new List<ThinkingToken>();
        var steps = ExtractReasoningSteps(chainOfThought);
        var totalLength = chainOfThought.Length;
        var stepLengths = steps.Select(s => s.Length).ToList();
        var totalStepLength = stepLengths.Sum();

        foreach (var step in steps)
        {
            var entities = ExtractEntities(step);
            var relations = ExtractRelations(step);
            var stepType = ClassifyStepType(step);

            if (entities.Count == 0) continue;

            var importance = ComputeImportance(step, chainOfThought, (double)step.Length / totalStepLength);
            if (importance < _config.ImportanceThreshold) continue;

            foreach (var entity in entities.Take(3))
            {
                var embedding = GetOrCreateEmbedding(entity, step);
                var relation = relations.Count > 0 ? relations[0] : "states";
                var confidence = ComputeConfidence(step, entity);
                var mutualInfo = _config.EnableMutualInfoTracking ? ComputeMutualInfo(step, chainOfThought) : 1.0;

                var token = new ThinkingToken
                {
                    Embedding = embedding,
                    KeyEntity = entity,
                    Relation = relation,
                    Confidence = confidence,
                    Importance = importance,
                    StepType = stepType,
                    OriginalLength = step.Length,
                    MutualInfo = mutualInfo
                };

                tokens.Add(token);
                if (tokens.Count >= _config.MaxThinkingTokens) break;
            }
            if (tokens.Count >= _config.MaxThinkingTokens) break;
        }

        DistributeImportance(tokens, totalLength);
        return tokens;
    }

    public string EncodeToString(string chainOfThought)
    {
        var tokens = Encode(chainOfThought);
        if (tokens.Count == 0) return "";

        var parts = tokens.Select(t =>
            $"<think e=\"{t.KeyEntity}\" r=\"{t.Relation}\" c=\"{t.Confidence:F2}\" i=\"{t.Importance:F2}\" t=\"{t.StepType}\"/>");
        return string.Join("", parts);
    }

    public (int originalTokens, int compressedTokens, double ratio, double avgMutualInfo) GetCompressionStats(
        string chainOfThought, List<ThinkingToken>? tokens = null)
    {
        tokens ??= Encode(chainOfThought);
        var estimatedOriginalTokens = EstimateTokenCount(chainOfThought);
        var compressedBytes = tokens.Sum(t => t.CompressedSize);
        var estimatedCompressedTokens = Math.Max(1, compressedBytes / 4);
        var avgMi = tokens.Count > 0 ? tokens.Average(t => t.MutualInfo) : 0;

        return (estimatedOriginalTokens, estimatedCompressedTokens,
                (double)estimatedCompressedTokens / estimatedOriginalTokens, avgMi);
    }

    private List<string> ExtractReasoningSteps(string text)
    {
        var steps = new List<string>();
        var delimiters = new[] { "\n\n", "\nStep", "\nThought:", "\nLet me", "First,", "Next,", "Then,", "Finally,",
            "So,", "Thus,", "Therefore,", "However,", "Because", "This means" };

        var current = text;
        while (current.Length > 0)
        {
            var bestPos = current.Length;
            foreach (var d in delimiters)
            {
                var pos = current.IndexOf(d, 1);
                if (pos > 0 && pos < bestPos) bestPos = pos;
            }

            if (bestPos >= current.Length)
            {
                if (current.Trim().Length > 0) steps.Add(current.Trim());
                break;
            }

            steps.Add(current[..bestPos].Trim());
            current = current[bestPos..];
        }

        return steps.Count == 0 ? new List<string> { text } : steps;
    }

    private List<string> ExtractEntities(string text)
    {
        var entities = new List<string>();
        var words = text.Split(new[] { ' ', ',', '.', '!', '?', ';', ':', '(', ')', '[', ']' },
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var word in words.Where(w => w.Length > 1 && char.IsUpper(w[0]) || w.Length > 3))
        {
            if (!IsStopWord(word))
                entities.Add(word.Trim(',', '.', ';', ':'));
        }

        var chineseMatches = System.Text.RegularExpressions.Regex.Matches(text, @"[\u4e00-\u9fff]{2,8}");
        foreach (System.Text.RegularExpressions.Match m in chineseMatches)
            entities.Add(m.Value);

        return entities.Distinct().Take(10).ToList();
    }

    private static List<string> ExtractRelations(string text)
    {
        var patterns = new Dictionary<string, string[]>
        {
            ["is"] = new[] { " is ", " are ", " was ", " were " },
            ["has"] = new[] { " has ", " have ", " contains " },
            ["causes"] = new[] { " causes ", " leads to ", " results in " },
            ["indicates"] = new[] { " indicates ", " suggests ", " implies " },
            ["belongs_to"] = new[] { " belongs to ", " is a type of ", " is part of " }
        };

        foreach (var (rel, pats) in patterns)
            if (pats.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase)))
                return new List<string> { rel };

        return new List<string>();
    }

    private static string ClassifyStepType(string text)
    {
        var lower = text.ToLower();
        return ClassificationRegistry.StepType.Classify(lower);
    }

    private double ComputeImportance(string step, string fullText, double lengthRatio)
    {
        var entropy = ComputeEntropy(step);
        var uniqueness = ComputeUniqueness(step, fullText);
        var positionWeight = 1.0 - (fullText.IndexOf(step) / (double)Math.Max(1, fullText.Length));
        return Math.Min(1.0, entropy * 0.4 + uniqueness * 0.4 + positionWeight * 0.1 + lengthRatio * 0.1);
    }

    private static double ComputeEntropy(string text)
    {
        var freq = new Dictionary<char, int>();
        foreach (var c in text.Where(char.IsLetterOrDigit))
            freq[c] = freq.GetValueOrDefault(c) + 1;
        var total = freq.Values.Sum();
        if (total == 0) return 0;
        return freq.Values.Sum(v => { var p = (double)v / total; return -p * Math.Log2(Math.Max(p, 1e-10)); }) / Math.Log2(freq.Count + 1);
    }

    private static double ComputeUniqueness(string step, string fullText)
    {
        var words = new HashSet<string>(step.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var fullWords = fullText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var rare = words.Count(w => fullWords.Count(fw => fw == w) <= 2);
        return words.Count > 0 ? (double)rare / words.Count : 0;
    }

    private double[] GetOrCreateEmbedding(string entity, string context)
    {
        if (_entityEmbeddings.TryGetValue(entity, out var cached)) return cached;

        var emb = new double[_config.EmbeddingDim];
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(entity + context));
        for (var i = 0; i < _config.EmbeddingDim; i++)
        {
            var h = hash[i % hash.Length] / 255.0;
            var noise = (_rng.NextDouble() - 0.5) * 0.1;
            emb[i] = Math.Clamp(h + noise, 0, 1);
        }

        _entityEmbeddings[entity] = emb;
        return emb;
    }

    private static double ComputeConfidence(string step, string entity)
    {
        var mentions = System.Text.RegularExpressions.Regex.Matches(step, System.Text.RegularExpressions.Regex.Escape(entity)).Count;
        return Math.Min(1.0, mentions * 0.3 + 0.4);
    }

    private static double ComputeMutualInfo(string step, string fullText)
    {
        var stepWords = new HashSet<string>(step.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var fullWords = fullText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var overlap = stepWords.Count(w => fullWords.Contains(w));
        var entropyReduction = stepWords.Count > 0 ? (double)overlap / stepWords.Count : 0;
        return Math.Max(0.1, entropyReduction);
    }

    private static void DistributeImportance(List<ThinkingToken> tokens, int totalLength)
    {
        var totalImportance = tokens.Sum(t => t.Importance);
        if (totalImportance <= 0) return;
        foreach (var t in tokens)
            t.Importance /= totalImportance;
    }

    private static int EstimateTokenCount(string text)
    {
        var chars = text.Length;
        var cjk = text.Count(c => c >= 0x4e00 && c <= 0x9fff);
        return Math.Max(1, cjk + (chars - cjk) / 4);
    }

    private static bool IsStopWord(string word)
    {
        var stops = new HashSet<string> { "the", "a", "an", "is", "are", "was", "were", "be", "been", "being",
            "have", "has", "had", "do", "does", "did", "will", "would", "could", "should", "may", "might", "can",
            "shall", "to", "of", "in", "for", "on", "with", "at", "by", "from", "as", "into", "through",
            "and", "but", "or", "not", "this", "that", "these", "those", "it", "its", "they", "them", "their" };
        return stops.Contains(word.ToLower()) || word.Length <= 2;
    }
}
