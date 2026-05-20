using System.Collections.Concurrent;

namespace LTAI.Vector.Knowledge;

public sealed class SuperpositionEmbedding
{
    public string Subject { get; init; } = "";
    public double[] Vector { get; init; } = Array.Empty<double>();
    public Dictionary<string, double> AttributeWeights { get; init; } = new();
    public double Norm => Math.Sqrt(Vector.Sum(v => v * v));
    public int Dimension => Vector.Length;

    public double Dot(SuperpositionEmbedding other)
    {
        double sum = 0;
        for (int i = 0; i < Math.Min(Vector.Length, other.Vector.Length); i++)
            sum += Vector[i] * other.Vector[i];
        return sum;
    }

    public SuperpositionEmbedding Add(SuperpositionEmbedding other)
    {
        var dim = Math.Max(Vector.Length, other.Vector.Length);
        var result = new double[dim];
        for (int i = 0; i < dim; i++)
        {
            double a = i < Vector.Length ? Vector[i] : 0;
            double b = i < other.Vector.Length ? other.Vector[i] : 0;
            result[i] = a + b;
        }
        return new() { Subject = Subject, Vector = result, AttributeWeights = AttributeWeights };
    }

    public SuperpositionEmbedding Scale(double factor)
    {
        var result = new double[Vector.Length];
        for (int i = 0; i < Vector.Length; i++)
            result[i] = Vector[i] * factor;
        return new() { Subject = Subject, Vector = result, AttributeWeights = AttributeWeights };
    }
}

public sealed class GeometricRelationSelector
{
    private readonly ConcurrentDictionary<string, SuperpositionEmbedding> _subjectEmbeddings = new();
    private readonly ConcurrentDictionary<string, double[]> _attributeVectors = new();
    private readonly ConcurrentDictionary<string, RelationGate> _relationGates = new();
    private readonly int _embeddingDim;

    public GeometricRelationSelector(int embeddingDim = 64)
    {
        _embeddingDim = embeddingDim;
    }

    public void EncodeSubject(string subject, Dictionary<string, double> attributes)
    {
        var vector = new double[_embeddingDim];
        var weights = new Dictionary<string, double>();

        foreach (var (attr, value) in attributes)
        {
            var attrVec = GetOrCreateAttributeVector(attr);
            double weight = value;

            for (int i = 0; i < _embeddingDim; i++)
                vector[i] += attrVec[i] * weight;

            weights[attr] = weight;
        }

        var norm = Math.Sqrt(vector.Sum(v => v * v));
        if (norm > 1e-8)
        {
            for (int i = 0; i < _embeddingDim; i++)
                vector[i] /= norm;
        }

        _subjectEmbeddings[subject] = new SuperpositionEmbedding
        {
            Subject = subject,
            Vector = vector,
            AttributeWeights = weights
        };
    }

    private double[] GetOrCreateAttributeVector(string attribute)
    {
        return _attributeVectors.GetOrAdd(attribute, _ =>
        {
            var vec = new double[_embeddingDim];
            var hash = (uint)attribute.GetHashCode();
            var rng = new Random((int)hash);
            for (int i = 0; i < _embeddingDim; i++)
                vec[i] = (rng.NextDouble() - 0.5) * 2.0;
            var norm = Math.Sqrt(vec.Sum(v => v * v));
            if (norm > 1e-8)
                for (int i = 0; i < _embeddingDim; i++)
                    vec[i] /= norm;
            return vec;
        });
    }

    public void LearnRelationGate(string relation)
    {
        if (_subjectEmbeddings.Count < 2) return;

        var subjects = _subjectEmbeddings.Values.ToList();
        var gate = _relationGates.GetOrAdd(relation, _ => new RelationGate(_embeddingDim));

        for (int epoch = 0; epoch < 5; epoch++)
        {
            for (int i = 0; i < subjects.Count; i++)
            {
                for (int j = i + 1; j < subjects.Count; j++)
                {
                    var si = subjects[i].Vector;
                    var sj = subjects[j].Vector;
                    var diff = new double[_embeddingDim];
                    for (int d = 0; d < _embeddingDim; d++)
                        diff[d] = si[d] - sj[d];

                    gate.Update(diff, 0.01);
                }
            }
        }
    }

    public List<string> Select(string subject, string relation, int topK = 5)
    {
        if (!_subjectEmbeddings.TryGetValue(subject, out var subjEmb))
            return new List<string>();

        if (!_relationGates.TryGetValue(relation, out var gate))
            gate = _relationGates.GetOrAdd(relation, _ => new RelationGate(_embeddingDim));

        var candidates = _subjectEmbeddings.Values
            .Where(s => s.Subject != subject)
            .ToList();

        var scored = candidates.Select(c =>
        {
            double score = GatedDot(subjEmb.Vector, c.Vector, gate);
            return (Subject: c.Subject, Score: score);
        }).ToList();

        scored.Sort((a, b) => b.Score.CompareTo(a.Score));

        return scored
            .Where(s => s.Score > 0.1)
            .Take(topK)
            .Select(s => s.Subject)
            .ToList();
    }

    public List<(string Target, string Relation, double Score)> MultiHop(string subject, string[] relations, int topK = 3)
    {
        var results = new List<(string, string, double)>();
        var current = subject;
        var visited = new HashSet<string> { subject };

        foreach (var relation in relations)
        {
            var candidates = Select(current, relation, topK * 2);
            var stepResults = new List<(string target, double score)>();

            foreach (var candidate in candidates)
            {
                if (visited.Contains(candidate)) continue;

                if (!_subjectEmbeddings.TryGetValue(candidate, out var candEmb)) continue;

                var gate = _relationGates.GetOrAdd(relation, _ => new RelationGate(_embeddingDim));
                var score = GatedDot(
                    _subjectEmbeddings[current].Vector,
                    candEmb.Vector,
                    gate);

                stepResults.Add((candidate, score));
            }

            if (stepResults.Count == 0) break;

            var best = stepResults.OrderByDescending(s => s.score).First();
            results.Add((best.target, relation, best.score));
            visited.Add(best.target);
            current = best.target;
        }

        return results;
    }

    private static double GatedDot(double[] query, double[] key, RelationGate gate)
    {
        double score = 0;
        for (int i = 0; i < Math.Min(query.Length, key.Length); i++)
        {
            double preActivation = query[i] * key[i] + gate.Bias[i];
            double gate_i = Math.Max(0, preActivation);
            score += gate_i * gate.Weights[i] * (query[i] * key[i]);
        }
        return score / Math.Max(1, Math.Sqrt(query.Length));
    }

    public Dictionary<string, object> GetStats()
    {
        return new Dictionary<string, object>
        {
            ["subjects"] = _subjectEmbeddings.Count,
            ["attributes"] = _attributeVectors.Count,
            ["relations"] = _relationGates.Count,
            ["embedding_dim"] = _embeddingDim,
            ["total_params"] = _subjectEmbeddings.Count * _embeddingDim
                + _attributeVectors.Count * _embeddingDim
                + _relationGates.Count * _embeddingDim * 2
        };
    }
}

public sealed class RelationGate
{
    public double[] Weights { get; }
    public double[] Bias { get; }
    private readonly int _dim;

    public RelationGate(int dim)
    {
        _dim = dim;
        Weights = new double[dim];
        Bias = new double[dim];
        var rng = new Random(42);
        for (int i = 0; i < dim; i++)
        {
            Weights[i] = (rng.NextDouble() - 0.5) * 0.1;
            Bias[i] = rng.NextDouble() * 0.1;
        }
    }

    public void Update(double[] diff, double lr)
    {
        for (int i = 0; i < Math.Min(_dim, diff.Length); i++)
        {
            Weights[i] += lr * diff[i] * 0.1;
            Bias[i] += lr * diff[i] * diff[i] * 0.01;
        }
    }
}
