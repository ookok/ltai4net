namespace LTAI.Agent.Memory;

public sealed class ShapleyEstimator
{
    private readonly int _numSamples;
    private readonly Random _rng = new();

    public ShapleyEstimator(int numSamples = 100)
    {
        _numSamples = numSamples;
    }

    public double[] Estimate(IReadOnlyList<string> snippets, string query)
    {
        var n = snippets.Count;
        if (n == 0) return [];
        if (n == 1) return [1.0];

        var shapleyValues = new double[n];
        var queryTerms = Tokenize(query);

        for (int sample = 0; sample < _numSamples; sample++)
        {
            var permutation = Enumerable.Range(0, n).OrderBy(_ => _rng.Next()).ToArray();
            var coalitionScore = 0.0;

            for (int pos = 0; pos < n; pos++)
            {
                var i = permutation[pos];
                var snippetScore = ComputeRelevance(snippets[i], queryTerms);

                var prevScore = coalitionScore;
                coalitionScore = 1.0 - (1.0 - coalitionScore) * (1.0 - snippetScore);

                var marginalContribution = coalitionScore - prevScore;
                shapleyValues[i] += marginalContribution;
            }
        }

        for (int i = 0; i < n; i++)
            shapleyValues[i] /= _numSamples;

        return shapleyValues;
    }

    public IReadOnlyList<(string Snippet, double ShapleyValue)> Filter(
        IReadOnlyList<string> snippets, string query,
        double threshold = 0.1, int maxResults = 10)
    {
        var values = Estimate(snippets, query);
        return snippets
            .Select((s, i) => (Snippet: s, ShapleyValue: values[i]))
            .Where(x => x.ShapleyValue > threshold)
            .OrderByDescending(x => x.ShapleyValue)
            .Take(maxResults)
            .ToList();
    }

    private static double ComputeRelevance(string snippet, Dictionary<string, int> queryTerms)
    {
        if (queryTerms.Count == 0 || string.IsNullOrEmpty(snippet)) return 0;

        var snippetTerms = Tokenize(snippet);
        double dot = 0, normQ = 0, normS = 0;

        foreach (var (term, qFreq) in queryTerms)
        {
            var sFreq = snippetTerms.GetValueOrDefault(term, 0);
            dot += qFreq * sFreq;
            normQ += qFreq * qFreq;
        }

        foreach (var sFreq in snippetTerms.Values)
            normS += sFreq * sFreq;

        var denom = Math.Sqrt(normQ) * Math.Sqrt(normS);
        return denom < 1e-10 ? 0 : dot / denom;
    }

    private static Dictionary<string, int> Tokenize(string text)
    {
        var tokens = new Dictionary<string, int>();
        if (string.IsNullOrEmpty(text)) return tokens;

        var span = text.AsSpan();
        var start = 0;
        for (int i = 0; i <= span.Length; i++)
        {
            if (i == span.Length || !char.IsLetterOrDigit(span[i]))
            {
                if (i > start)
                {
                    var token = span[start..i].ToString();
                    if (token.Length >= 2)
                        tokens[token] = tokens.GetValueOrDefault(token, 0) + 1;
                }
                start = i + 1;
            }
        }
        return tokens;
    }
}
