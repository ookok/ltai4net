namespace LTAI.Knowledge.Core;

public sealed record Bm25Params
{
    public double K1 { get; init; } = 1.5;
    public double B { get; init; } = 0.75;
    public double Epsilon { get; init; } = 0.25;
}

public sealed record Bm25ScoredDoc
{
    public string Id { get; init; } = "";
    public string Content { get; init; } = "";
    public double Bm25Score { get; init; }
    public double FtsScore { get; init; }
    public double VectorScore { get; init; }
    public double RrfScore { get; init; }
    public string Source { get; init; } = "";
}

public sealed class Bm25Scorer
{
    private readonly Dictionary<string, int> _docLengths = new();
    private readonly Dictionary<string, Dictionary<string, int>> _termFreqs = new();
    private readonly Dictionary<string, int> _docFreqs = new();
    private int _totalDocs;
    private double _avgDocLength;

    private readonly Bm25Params _params;
    private readonly object _lock = new();

    public Bm25Scorer(Bm25Params? parameters = null)
    {
        _params = parameters ?? new Bm25Params();
    }

    public void IndexDocuments(IEnumerable<(string id, string content)> documents)
    {
        lock (_lock)
        {
            foreach (var (id, content) in documents)
            {
                var terms = Tokenize(content);
                _docLengths[id] = terms.Count;
                _totalDocs++;
                _avgDocLength = _docLengths.Values.Average();

                var docFreqs = new Dictionary<string, int>();
                foreach (var term in terms)
                {
                    if (!_termFreqs.ContainsKey(id))
                        _termFreqs[id] = new Dictionary<string, int>();

                    _termFreqs[id].TryGetValue(term, out var tf);
                    _termFreqs[id][term] = tf + 1;

                    docFreqs[term] = 1;
                }

                foreach (var term in docFreqs.Keys)
                {
                    _docFreqs.TryGetValue(term, out var df);
                    _docFreqs[term] = df + 1;
                }
            }
        }
    }

    public void AddDocument(string id, string content)
    {
        IndexDocuments(new[] { (id, content) });
    }

    public List<(string id, double score)> Search(string query, int topK = 20, string? domain = null)
    {
        lock (_lock)
        {
            var queryTerms = Tokenize(query);
            if (queryTerms.Count == 0) return new();

            var queryTermFreqs = new Dictionary<string, int>();
            foreach (var term in queryTerms)
            {
                queryTermFreqs.TryGetValue(term, out var qtf);
                queryTermFreqs[term] = qtf + 1;
            }

            var scores = new List<(string id, double score)>();

            foreach (var (docId, termFreqs) in _termFreqs)
            {
                if (domain != null)
                    continue;

                var score = ComputeBm25(docId, termFreqs, queryTermFreqs);
                if (score > 1e-9)
                    scores.Add((docId, score));
            }

            return scores.OrderByDescending(s => s.score).Take(topK).ToList();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _docLengths.Clear();
            _termFreqs.Clear();
            _docFreqs.Clear();
            _totalDocs = 0;
            _avgDocLength = 0;
        }
    }

    private double ComputeBm25(
        string docId,
        Dictionary<string, int> docTermFreqs,
        Dictionary<string, int> queryTermFreqs)
    {
        double score = 0;
        var docLen = _docLengths.GetValueOrDefault(docId, 1);

        foreach (var (term, qtf) in queryTermFreqs)
        {
            if (!docTermFreqs.TryGetValue(term, out var tf))
                continue;

            var df = _docFreqs.GetValueOrDefault(term, 0);
            var idf = ComputeIdf(df);

            var numerator = tf * (_params.K1 + 1);
            var denominator = tf + _params.K1 * (1 - _params.B + _params.B * docLen / Math.Max(1, _avgDocLength));

            score += idf * (numerator / Math.Max(_params.Epsilon, denominator)) * qtf;
        }

        return score;
    }

    private double ComputeIdf(int documentFrequency)
    {
        var numerator = _totalDocs - documentFrequency + 0.5;
        var denominator = documentFrequency + 0.5;
        return Math.Max(0, Math.Log(1 + numerator / Math.Max(1, denominator)));
    }

    public static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();

        foreach (var part in text.Split(new[] { ' ', '\n', '\r', '\t', '。', '，', '；', '、', '：' },
            StringSplitOptions.RemoveEmptyEntries))
        {
            var cleaned = part.Trim().ToLowerInvariant();
            if (cleaned.Length < 1) continue;

            if (cleaned.Length <= 20)
            {
                tokens.Add(cleaned);
                continue;
            }

            foreach (System.Text.RegularExpressions.Match c in System.Text.RegularExpressions.Regex.Matches(cleaned, @"[\u4e00-\u9fff]|[A-Za-z0-9]+|[^\s]"))
            {
                var bt = c.Value.ToLowerInvariant();
                if (bt.Length >= 1)
                    tokens.Add(bt);
            }
        }

        return tokens.Distinct().ToList();
    }

    public Dictionary<string, object> GetStats()
    {
        lock (_lock)
        {
            return new()
            {
                ["total_docs"] = _totalDocs,
                ["unique_terms"] = _docFreqs.Count,
                ["avg_doc_length"] = Math.Round(_avgDocLength, 1)
            };
        }
    }
}
