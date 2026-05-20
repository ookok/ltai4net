using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using LTAI.Vector.Knowledge.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.Vector.Knowledge;

public sealed record PatternSnapshot
{
    public string Id { get; init; } = "";
    public List<string> SourceEventIds { get; init; } = new();
    public double[] WeightVector { get; init; } = Array.Empty<double>();
    public string PatternType { get; init; } = "";
    public string PatternText { get; init; } = "";
    public double Confidence { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public int ReplayCount { get; set; }
    public double LastReplay { get; set; }
}

public sealed record ConsolidationStats
{
    public int PatternsExtracted { get; init; }
    public int PatternsConsolidated { get; init; }
    public int ReplaysCompleted { get; init; }
    public double AvgWeightNorm { get; init; }
    public double LastRunMs { get; init; }
    public double LastInfoNceLoss { get; init; }
    public double LastHebbLoss { get; init; }
    public Dictionary<string, int> PatternTypeDistribution { get; init; } = new();
}

public sealed class NeocorticalConsolidator
{
    private readonly StructMemory _structMemory;
    private readonly ILogger<NeocorticalConsolidator>? _logger;

    private readonly List<PatternSnapshot> _patterns = new();
    private readonly ConcurrentDictionary<string, double[]> _weightStore = new();
    private readonly ConcurrentQueue<PatternSnapshot> _replayQueue = new();
    private readonly object _lock = new();

    private const int WeightDim = 64;
    private const double LearningRate = 0.015;
    private const double DecayRate = 0.999;
    private const int MaxPatterns = 500;
    private const int ReplayBatchSize = 16;
    private const int MinInterleavedAge = 120;
    private const int InfoNceNegativeCount = 8;
    private const double InfoNceTemperature = 0.07;
    private const double InfoNceLossWeight = 0.3;
    private static readonly TimeSpan ConsolidationInterval = TimeSpan.FromMinutes(2);

    private DateTimeOffset _lastConsolidation = DateTimeOffset.MinValue;
    private double _lastInfoNceLoss;
    private double _lastHebbLoss;

    public NeocorticalConsolidator(
        StructMemory structMemory,
        ILogger<NeocorticalConsolidator>? logger = null)
    {
        _structMemory = structMemory;
        _logger = logger;
    }

    public async Task<ConsolidationStats> ConsolidateAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var patterns = ExtractPatterns();
        var assembled = AssembleWeights(patterns);

        if (_replayQueue.Count >= ReplayBatchSize)
            await InterleavedReplayAsync();

        UpdateWeights(assembled);

        var stats = BuildStats(sw.ElapsedMilliseconds);
        _lastConsolidation = DateTimeOffset.UtcNow;

        _logger?.LogInformation(
            "NeocorticalConsolidator: extracted={Extracted} assembled={Assembled} replays={Replays} {Ms}ms",
            patterns.Count, assembled.Count, _replayQueue.Count, sw.ElapsedMilliseconds);

        return stats;
    }

    public Task ConsolidateIfNeededAsync()
    {
        var elapsed = DateTimeOffset.UtcNow - _lastConsolidation;
        if (elapsed >= ConsolidationInterval && _replayQueue.Count >= ReplayBatchSize / 2)
            return ConsolidateAsync();
        return Task.CompletedTask;
    }

    private List<PatternSnapshot> ExtractPatterns()
    {
        var extracted = new List<PatternSnapshot>();
        var synths = _structMemory.GetContextBlock()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.Length > 10)
            .ToList();

        foreach (var synth in synths)
        {
            var causal = Regex.Match(synth, @"(因为|由于|because|since).*?(所以|因此|therefore|thus)");
            if (causal.Success && causal.Value.Length > 15)
            {
                var vec = TextToVector(causal.Value);
                extracted.Add(new PatternSnapshot
                {
                    Id = $"pat_causal_{Guid.NewGuid():N}"[..16],
                    WeightVector = vec,
                    PatternType = "causal",
                    PatternText = causal.Value[..Math.Min(200, causal.Value.Length)],
                    Confidence = 0.6
                });
            }

            var conditional = Regex.Match(synth, @"(如果|if|when|若|当).*?(则|那么|then)");
            if (conditional.Success && conditional.Value.Length > 12)
            {
                var vec = TextToVector(conditional.Value);
                extracted.Add(new PatternSnapshot
                {
                    Id = $"pat_cond_{Guid.NewGuid():N}"[..16],
                    WeightVector = vec,
                    PatternType = "conditional",
                    PatternText = conditional.Value[..Math.Min(200, conditional.Value.Length)],
                    Confidence = 0.5
                });
            }

            var comparative = Regex.Match(synth, @"(比|比较|compared|versus|vs)\s*.{3,}");
            if (comparative.Success && comparative.Value.Length > 10)
            {
                var vec = TextToVector(comparative.Value);
                extracted.Add(new PatternSnapshot
                {
                    Id = $"pat_comp_{Guid.NewGuid():N}"[..16],
                    WeightVector = vec,
                    PatternType = "comparative",
                    PatternText = comparative.Value[..Math.Min(200, comparative.Value.Length)],
                    Confidence = 0.55
                });
            }

            var factual = Regex.Match(synth, @"(\w{3,})\s*(是|为|指|定义|属于|包含|=)\s*(\w{3,})");
            if (factual.Success && factual.Value.Length > 6)
            {
                var vec = TextToVector(factual.Value);
                extracted.Add(new PatternSnapshot
                {
                    Id = $"pat_fact_{Guid.NewGuid():N}"[..16],
                    WeightVector = vec,
                    PatternType = "factual",
                    PatternText = factual.Value[..Math.Min(200, factual.Value.Length)],
                    Confidence = 0.7
                });
            }
        }

        lock (_lock)
        {
            foreach (var p in extracted)
            {
                _patterns.Add(p);
                _replayQueue.Enqueue(p);
            }

            while (_patterns.Count > MaxPatterns)
                _patterns.RemoveAt(0);
        }

        return extracted;
    }

    private static List<(string key, double[] vector)> AssembleWeights(List<PatternSnapshot> patterns)
    {
        var result = new List<(string, double[])>();
        foreach (var p in patterns)
        {
            var key = $"{p.PatternType}_{p.Id}";
            result.Add((key, p.WeightVector));
        }
        return result;
    }

    private void UpdateWeights(List<(string key, double[] vector)> assembled)
    {
        foreach (var (key, vec) in assembled)
        {
            _weightStore.AddOrUpdate(key, vec, (_, old) =>
            {
                var merged = new double[WeightDim];
                for (int i = 0; i < WeightDim; i++)
                    merged[i] = old[i] * 0.85 + vec[i] * 0.15;
                return merged;
            });
        }
    }

    private async Task InterleavedReplayAsync()
    {
        var batch = new List<PatternSnapshot>();
        while (_replayQueue.TryDequeue(out var pattern))
        {
            batch.Add(pattern);
            if (batch.Count >= ReplayBatchSize) break;
        }

        if (batch.Count == 0) return;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var recent = batch.Where(p => (now - p.CreatedAt.ToUnixTimeSeconds()) < 300).ToList();
        var aged = batch.Where(p => (now - p.CreatedAt.ToUnixTimeSeconds()) >= MinInterleavedAge).ToList();

        var interleaved = new List<PatternSnapshot>();
        int maxLen = Math.Max(recent.Count, aged.Count);
        for (int i = 0; i < maxLen; i++)
        {
            if (i < aged.Count) interleaved.Add(aged[i]);
            if (i < recent.Count) interleaved.Add(recent[i]);
        }

        foreach (var pattern in interleaved)
        {
            var key = $"{pattern.PatternType}_{pattern.Id}";
            if (_weightStore.TryGetValue(key, out var stored))
            {
                var delta = HebbianDelta(pattern.WeightVector, stored);
                for (int i = 0; i < WeightDim; i++)
                    stored[i] = stored[i] * DecayRate + delta[i] * LearningRate;

                var negatives = SampleNegatives(key, InfoNceNegativeCount);
                var infoNceGrad = ComputeInfoNceGradient(
                    pattern.WeightVector, stored, negatives, InfoNceTemperature);
                for (int i = 0; i < WeightDim; i++)
                    stored[i] += infoNceGrad[i] * LearningRate * InfoNceLossWeight;

                _lastHebbLoss = delta.Sum(d => d * d);
                _lastInfoNceLoss = infoNceGrad.Sum(g => g * g);

                pattern.ReplayCount++;
                pattern.LastReplay = now;
            }

            if (pattern.ReplayCount < 5)
                _replayQueue.Enqueue(pattern);
        }
    }

    private static double[] HebbianDelta(double[] pre, double[] post)
    {
        var delta = new double[WeightDim];
        double trace = 0;
        for (int i = 0; i < WeightDim; i++)
        {
            delta[i] = pre[i] * post[i];
            trace += delta[i] * delta[i];
        }

        var bias = Math.Sqrt(trace) / WeightDim * 0.1;
        for (int i = 0; i < WeightDim; i++)
            delta[i] = delta[i] - bias;

        return delta;
    }

    private List<(string key, double[] vector)> SampleNegatives(
        string anchorKey, int count)
    {
        var candidates = new List<(string, double[])>();
        lock (_lock)
        {
            candidates = _weightStore
                .Where(kv => kv.Key != anchorKey)
                .Select(kv => (kv.Key, kv.Value))
                .ToList();
        }

        if (candidates.Count <= count)
            return candidates;

        var rng = new Random();
        return candidates
            .OrderBy(_ => rng.NextDouble())
            .Take(count)
            .ToList();
    }

    private static double[] ComputeInfoNceGradient(
        double[] anchor, double[] positive, List<(string key, double[] vector)> negatives,
        double temperature)
    {
        double posSim = ExpDot(anchor, positive, temperature);
        double negSum = posSim;

        foreach (var (_, neg) in negatives)
            negSum += ExpDot(anchor, neg, temperature);

        double posProb = posSim / Math.Max(negSum, 1e-9);

        var grad = new double[WeightDim];
        for (int i = 0; i < WeightDim; i++)
        {
            double negWeighted = 0;
            foreach (var (_, neg) in negatives)
            {
                double ns = ExpDot(anchor, neg, temperature);
                negWeighted += ns / negSum * neg[i];
            }

            grad[i] = (posProb * positive[i] - negWeighted) / temperature;
        }

        return grad;
    }

    private static double ExpDot(double[] a, double[] b, double temperature)
    {
        double dot = 0;
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
            dot += a[i] * b[i];
        return Math.Exp(dot / temperature);
    }

    public async Task<List<SynthesisBlock>> ConsolidateAndSynthesizeAsync()
    {
        await ConsolidateAsync();
        return await _structMemory.ConsolidateIfNeeded();
    }

    public List<PatternSnapshot> QueryWeights(string query, int topK = 10)
    {
        var queryVec = TextToVector(query);
        var scored = new List<(PatternSnapshot, double)>();

        lock (_lock)
        {
            foreach (var p in _patterns.TakeLast(300))
            {
                double sim = CosineSimilarity(queryVec, p.WeightVector);
                scored.Add((p, sim * p.Confidence));
            }
        }

        return scored
            .OrderByDescending(s => s.Item2)
            .Take(topK)
            .Select(s => s.Item1)
            .ToList();
    }

    public double[] GetBlendedWeight(string query)
    {
        var queryVec = TextToVector(query);
        var blended = new double[WeightDim];

        lock (_lock)
        {
            var matches = _patterns.TakeLast(300)
                .Where(p => CosineSimilarity(queryVec, p.WeightVector) > 0.5)
                .ToList();

            if (matches.Count == 0) return queryVec;

            foreach (var m in matches)
            {
                var alpha = CosineSimilarity(queryVec, m.WeightVector) * m.Confidence;
                for (int i = 0; i < WeightDim; i++)
                    blended[i] += m.WeightVector[i] * alpha;
            }

            double norm = Math.Sqrt(blended.Sum(v => v * v));
            if (norm > 1e-8)
                for (int i = 0; i < WeightDim; i++)
                    blended[i] = blended[i] / matches.Count * (1.0 / norm);
        }

        return blended;
    }

    public ConsolidationStats GetStats()
    {
        lock (_lock)
        {
            var patternTypes = _patterns.GroupBy(p => p.PatternType)
                .ToDictionary(g => g.Key, g => g.Count());

            return new ConsolidationStats
            {
                PatternsExtracted = _patterns.Count,
                PatternsConsolidated = _patterns.Count(p => p.ReplayCount > 0),
                ReplaysCompleted = _patterns.Sum(p => p.ReplayCount),
                AvgWeightNorm = _weightStore.Values.DefaultIfEmpty(new double[WeightDim])
                    .Average(v => Math.Sqrt(v.Sum(x => x * x))),
                LastInfoNceLoss = _lastInfoNceLoss,
                LastHebbLoss = _lastHebbLoss,
                PatternTypeDistribution = patternTypes
            };
        }
    }

    private static double[] TextToVector(string text, int dim = 64)
    {
        var vec = new double[dim];
        var hash = (uint)text.GetHashCode();
        var rng = new Random((int)hash);

        for (int i = 0; i < dim; i++)
            vec[i] = (rng.NextDouble() - 0.5) * 2.0;

        var words = text.Split(new[] { ' ', '\n', '\r', '\t', '。', '，', ',' },
            StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < words.Length; i++)
        {
            var w = words[i];
            var wHash = (int)(uint)w.GetHashCode();
            var idx = Math.Abs(wHash) % dim;
            vec[idx] += 1.0 / (i + 1);
        }

        double norm = Math.Sqrt(vec.Sum(v => v * v));
        if (norm > 1e-8)
            for (int i = 0; i < dim; i++)
                vec[i] /= norm;

        return vec;
    }

    private static double CosineSimilarity(double[] a, double[] b)
    {
        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB) + 1e-9);
    }

    private ConsolidationStats BuildStats(long elapsedMs)
    {
        lock (_lock)
        {
            return new ConsolidationStats
            {
                PatternsExtracted = _patterns.Count,
                PatternsConsolidated = _patterns.Count(p => p.ReplayCount > 0),
                ReplaysCompleted = _patterns.Sum(p => p.ReplayCount),
                AvgWeightNorm = _weightStore.Values.DefaultIfEmpty(new double[WeightDim])
                    .Average(v => Math.Sqrt(v.Sum(x => x * x))),
                LastRunMs = elapsedMs,
                LastInfoNceLoss = _lastInfoNceLoss,
                LastHebbLoss = _lastHebbLoss,
                PatternTypeDistribution = _patterns.GroupBy(p => p.PatternType)
                    .ToDictionary(g => g.Key, g => g.Count())
            };
        }
    }
}
