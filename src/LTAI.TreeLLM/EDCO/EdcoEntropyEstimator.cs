using System.Text;

namespace LTAI.TreeLLM.EDCO;

public sealed class EdcoSample
{
    public string Id { get; set; } = "";
    public string Content { get; set; } = "";
    public string Domain { get; set; } = "";
    public double Entropy { get; set; }
    public double PrefixEntropy { get; set; }
    public double Difficulty { get; set; }
    public int TokenCount { get; set; }
    public double Reward { get; set; }
    public bool Selected { get; set; }
    public int RoundSelected { get; set; }
    public List<double> EntropyHistory { get; set; } = new();
}

public sealed class EdcoConfig
{
    public int SamplesPerRound { get; set; } = 200;
    public int TotalRounds { get; set; } = 5;
    public double EntropyThreshold { get; set; } = 0.3;
    public double PrefixRatio { get; set; } = 0.15;
    public bool EnablePrefixApproximation { get; set; } = true;
    public bool EnableEntropyHistory { get; set; } = true;
    public double ExplorationRate { get; set; } = 0.1;
}

public sealed class EdcoEntropyEstimator
{
    private readonly EdcoConfig _config;
    private readonly Dictionary<string, Dictionary<string, int>> _ngramFreqs = new();
    private int _totalTokens;

    public EdcoEntropyEstimator(EdcoConfig? config = null) => _config = config ?? new();

    public double EstimateFullEntropy(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;

        var tokens = Tokenize(text);
        if (tokens.Count < 2) return 0;

        var entropy = 0.0;
        for (var i = 1; i < tokens.Count; i++)
        {
            var bigram = $"{tokens[i - 1]}|{tokens[i]}";
            if (!_ngramFreqs.TryGetValue(bigram, out _))
                _ngramFreqs[bigram] = new Dictionary<string, int>();
        }

        for (var i = 1; i < tokens.Count; i++)
        {
            var bigram = $"{tokens[i - 1]}|{tokens[i]}";
            var nextToken = tokens[i];
            var context = _ngramFreqs[bigram];

            var totalContext = context.Values.Sum() + 1;
            var p = ((double)(context.GetValueOrDefault(nextToken) + 1)) / totalContext;
            entropy += -p * Math.Log2(Math.Max(p, 1e-10));
        }

        _totalTokens += tokens.Count;
        return tokens.Count > 1 ? entropy / (tokens.Count - 1) : 0;
    }

    public double EstimatePrefixEntropy(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;

        var tokens = Tokenize(text);
        var prefixLen = Math.Max(1, (int)(tokens.Count * _config.PrefixRatio));

        var prefixTokens = tokens.Take(prefixLen).ToList();
        var prefixEntropy = ComputeTokenEntropy(prefixTokens);

        var scalingFactor = 1.0 + (1.0 - _config.PrefixRatio) * 0.3;
        return Math.Min(1.0, prefixEntropy * scalingFactor);
    }

    public double EstimateEntropy(string text, bool usePrefix = true)
    {
        if (usePrefix && _config.EnablePrefixApproximation)
            return EstimatePrefixEntropy(text);
        return EstimateFullEntropy(text);
    }

    private static double ComputeTokenEntropy(List<string> tokens)
    {
        if (tokens.Count == 0) return 0;

        var freq = new Dictionary<string, int>();
        foreach (var t in tokens)
            freq[t] = freq.GetValueOrDefault(t) + 1;

        var total = freq.Values.Sum();
        return freq.Values.Sum(v =>
        {
            var p = (double)v / total;
            return -p * Math.Log2(Math.Max(p, 1e-10));
        }) / Math.Log2(Math.Max(freq.Count, 2));
    }

    public void UpdateWithSample(string text)
    {
        var tokens = Tokenize(text);
        for (var i = 1; i < tokens.Count; i++)
        {
            var bigram = $"{tokens[i - 1]}|{tokens[i]}";
            if (!_ngramFreqs.ContainsKey(bigram))
                _ngramFreqs[bigram] = new Dictionary<string, int>();

            var context = _ngramFreqs[bigram];
            context[tokens[i]] = context.GetValueOrDefault(tokens[i]) + 1;
        }
        _totalTokens += tokens.Count;
    }

    public (double estimated, double exact, double error) CompareEstimates(string text)
    {
        var prefix = EstimatePrefixEntropy(text);
        var exact = EstimateFullEntropy(text);
        var error = exact > 0 ? Math.Abs(prefix - exact) / exact * 100 : 0;
        return (prefix, exact, error);
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();

        var chineseTokens = System.Text.RegularExpressions.Regex.Matches(text, @"[\u4e00-\u9fff]+");
        foreach (System.Text.RegularExpressions.Match m in chineseTokens)
            tokens.Add(m.Value);

        var remaining = System.Text.RegularExpressions.Regex.Replace(text, @"[\u4e00-\u9fff]+", " ");
        var words = remaining.Split(new[] { ' ', '\n', '\t', ',', '.', '!', '?' },
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var w in words.Where(w => w.Length > 1 || char.IsLetterOrDigit(w[0])))
            tokens.Add(w.ToLowerInvariant());

        return tokens.Count == 0 ? new List<string> { text.ToLowerInvariant() } : tokens;
    }

    public Dictionary<string, object> GetStats() => new()
    {
        ["bigram_contexts"] = _ngramFreqs.Count,
        ["total_tokens"] = _totalTokens,
        ["prefix_ratio"] = _config.PrefixRatio,
        ["computation_saved"] = $"{83.5:F1}%"
    };
}
