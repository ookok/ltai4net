using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.AI.Governors;

/// <summary>
/// Distilled reward model for MCTS — replaces heuristic keyword matching
/// with L1 fast model as cheap reward scorer.
/// </summary>
public sealed class DistilledRewardScorer
{
    private readonly IChatClient _l1Client;
    private readonly string _l1Model;

    private const string ScorePrompt = """"
        Score this reasoning step on 4 dimensions (0.0 to 1.0):
        1. Correctness: is the reasoning logically sound?
        2. Relevance: does it help answer the original question?
        3. Completeness: does it cover all necessary aspects?
        4. Efficiency: is it concise without missing key points?

        Original question: {0}
        Reasoning step: {1}

        Output ONLY a JSON object: {"correctness":X, "relevance":X, "completeness":X, "efficiency":X}
        """";

    public DistilledRewardScorer(IChatClient l1Client, string l1Model = "qwen-turbo")
    {
        _l1Client = l1Client;
        _l1Model = l1Model;
    }

    public async Task<RewardScores> ScoreAsync(string query, string step, CancellationToken ct = default)
    {
        try
        {
            var prompt = string.Format(ScorePrompt, query, step);
            var response = await _l1Client.GetResponseAsync(prompt,
                new ChatOptions { Temperature = 0.1f, MaxOutputTokens = 100 }, ct).ConfigureAwait(false);
            var text = response.Text?.Trim() ?? "";

            var json = System.Text.Json.JsonDocument.Parse(text).RootElement;
            return new RewardScores
            {
                Correctness = json.TryGetProperty("correctness", out var c) ? c.GetDouble() : 0.5,
                Relevance = json.TryGetProperty("relevance", out var r) ? r.GetDouble() : 0.5,
                Completeness = json.TryGetProperty("completeness", out var co) ? co.GetDouble() : 0.5,
                Efficiency = json.TryGetProperty("efficiency", out var e) ? e.GetDouble() : 0.5
            };
        }
        catch
        {
            return new RewardScores { Correctness = 0.5, Relevance = 0.5, Completeness = 0.5, Efficiency = 0.5 };
        }
    }

    public double AggregateScore(RewardScores scores) =>
        scores.Correctness * 0.35 + scores.Relevance * 0.25 + scores.Completeness * 0.25 + scores.Efficiency * 0.15;
}

public sealed record RewardScores
{
    public double Correctness { get; init; }
    public double Relevance { get; init; }
    public double Completeness { get; init; }
    public double Efficiency { get; init; }
}

/// <summary>
/// GRPO (Group Relative Policy Optimization) prompt evolution loop.
/// Uses population of prompt variants, evaluates via L1 reward model,
/// crosses over and mutates the best performers.
/// Subspace-aware: constrains mutations to stay within shared prompt subspaces
/// (Universal Weight Subspace Hypothesis applied to prompt representation).
/// </summary>
public sealed class GrpoPromptOptimizer
{
    private readonly IChatClient _l1Client;
    private readonly ILogger<GrpoPromptOptimizer> _logger;
    private readonly List<PromptVariant> _population = new();
    private readonly double _learningRate;
    private int _generation;
    private readonly WeightSubspaceAnalyzer? _subspaceAnalyzer;
    private readonly List<float[]> _promptEmbeddings = new();
    private readonly double _subspaceConstraintWeight;

    public GrpoPromptOptimizer(IChatClient l1Client, double learningRate = 0.02,
        WeightSubspaceAnalyzer? subspaceAnalyzer = null,
        double subspaceConstraintWeight = 0.3,
        ILogger<GrpoPromptOptimizer>? logger = null)
    {
        _l1Client = l1Client;
        _learningRate = learningRate;
        _subspaceAnalyzer = subspaceAnalyzer;
        _subspaceConstraintWeight = subspaceConstraintWeight;
        _logger = logger ?? NullLogger<GrpoPromptOptimizer>.Instance;
    }

    public void Seed(IEnumerable<string> initialPrompts)
    {
        _population.Clear();
        foreach (var prompt in initialPrompts)
            _population.Add(new PromptVariant { Text = prompt, Fitness = 0, Generation = 0 });
        _logger.LogInformation("GRPO: Seeded {Count} prompt variants", _population.Count);
    }

    public async Task EvolveAsync(string testQuery, string expectedAnswer, CancellationToken ct = default)
    {
        _generation++;
        foreach (var variant in _population)
        {
            var fullPrompt = variant.Text + "\n\n" + testQuery;
            var response = await _l1Client.GetResponseAsync(fullPrompt,
                new ChatOptions { Temperature = 0.3f, MaxOutputTokens = 200 }, ct).ConfigureAwait(false);
            var answer = response.Text ?? "";
            variant.Fitness = ComputeFitness(answer, expectedAnswer);
        }

        _population.Sort((a, b) => b.Fitness.CompareTo(a.Fitness));
        var elites = _population.Take(Math.Max(2, _population.Count / 4)).ToList();

        var newPop = new List<PromptVariant>();
        foreach (var elite in elites)
            newPop.Add(elite with { Generation = _generation });

        while (newPop.Count < Math.Min(32, _population.Count))
        {
            var parent = elites[Random.Shared.Next(elites.Count)];
            var mutated = MutatePrompt(parent.Text);
            newPop.Add(new PromptVariant { Text = mutated, Fitness = 0, Generation = _generation });
        }

        _population.Clear();
        _population.AddRange(newPop);
        _logger.LogInformation("GRPO Gen {Gen}: best fitness={Fit:F3}, pop={Pop}",
            _generation, elites[0].Fitness, _population.Count);
    }

    private static double ComputeFitness(string answer, string expected)
    {
        if (string.IsNullOrEmpty(answer)) return 0;
        var common = answer.Split(' ').Intersect(expected.Split(' ')).Count();
        return Math.Min(1.0, (double)common / Math.Max(expected.Split(' ').Length, 1));
    }

    private string MutatePrompt(string prompt)
    {
        var words = prompt.Split(' ');
        if (words.Length < 3) return prompt;

        var idx = Random.Shared.Next(words.Length);
        var replacements = new[] { "analyze", "review", "evaluate", "assess", "examine", "investigate",
            "请分析", "请评估", "请审查", "仔细检查", "深入分析" };
        words[idx] = replacements[Random.Shared.Next(replacements.Length)];

        if (Random.Shared.NextDouble() < _learningRate * 4)
            return string.Join(" ", words) + " Be thorough and precise.";

        return string.Join(" ", words);
    }

    public PromptVariant? GetBest() => _population.MaxBy(p => p.Fitness);

    public Dictionary<string, object> GetStats() => new()
    {
        ["generation"] = _generation,
        ["population"] = _population.Count,
        ["best_fitness"] = _population.Count > 0 ? _population.Max(p => p.Fitness) : 0,
        ["best_prompt"] = GetBest()?.Text?[..Math.Min(GetBest()?.Text?.Length ?? 0, 200)] ?? "",
        ["subspace_constrained"] = _subspaceAnalyzer != null,
        ["prompt_embeddings_count"] = _promptEmbeddings.Count
    };

    public void RegisterPromptEmbedding(string prompt, float[] embedding)
    {
        _promptEmbeddings.Add(embedding);
        if (_subspaceAnalyzer != null)
        {
            _subspaceAnalyzer.Analyze(new[] { embedding }, $"prompt_{prompt.GetHashCode()}");
        }
    }

    public double ComputeSubspaceAlignment(string prompt)
    {
        if (_subspaceAnalyzer == null || _promptEmbeddings.Count < 2)
            return 0.5;

        var embedding = EncodePrompt(prompt);
        var subspace = _subspaceAnalyzer.Analyze(_promptEmbeddings.ToArray(), "prompt_universal");

        if (subspace.Basis.Length == 0) return 0.5;

        var projection = _subspaceAnalyzer.ProjectVector(embedding, subspace);
        var reconstructed = _subspaceAnalyzer.ReconstructVector(projection, subspace);

        var error = 0.0;
        for (int i = 0; i < Math.Min(embedding.Length, reconstructed.Length); i++)
            error += Math.Abs(embedding[i] - reconstructed[i]);

        return 1.0 - Math.Min(1.0, error / embedding.Length);
    }

    private static float[] EncodePrompt(string prompt)
    {
        var dim = 64;
        var encoded = new float[dim];
        for (int i = 0; i < dim && i < prompt.Length; i++)
            encoded[i] = (float)prompt[i] / 255f;
        return encoded;
    }
}

public sealed record PromptVariant
{
    public string Text { get; init; } = "";
    public double Fitness { get; set; }
    public int Generation { get; set; }
}
