namespace LTAI.AI.Governors;

public sealed record QValueEstimate
{
    public string StateKey { get; init; } = "";
    public double Value { get; init; }
    public double PriorProbability { get; init; } = 1.0;
    public double Uncertainty { get; init; } = 1.0;
    public int HistoricalVisits { get; init; }
    public bool IsReliable => Uncertainty < 0.5 && HistoricalVisits >= 3;
}

public sealed class QValueEstimator
{
    private readonly KnowledgeGraphBridge _graphBridge;
    private readonly DualMemoryStore _memory;
    private readonly MetaCognitiveLayer _metaCognition;

    public QValueEstimator(
        KnowledgeGraphBridge graphBridge,
        DualMemoryStore memory,
        MetaCognitiveLayer metaCognition)
    {
        _graphBridge = graphBridge;
        _memory = memory;
        _metaCognition = metaCognition;
    }

    public QValueEstimate EstimateNode(string state, string? domain = null, int depth = 0, int maxDepth = 10)
    {
        var stateKey = NormalizeState(state);
        double kgScore = QueryKnowledgeGraph(stateKey, domain);
        double memoryScore = QueryMemoryStore(stateKey, domain);
        double depthPenalty = 1.0 - (double)depth / Math.Max(1, maxDepth);

        var value = 0.40 * kgScore + 0.40 * memoryScore + 0.20 * depthPenalty;
        var prior = value > 0.5 ? value : 0.3;

        var assessment = _metaCognition.Assess(state, (float)value, domain);
        var uncertainty = 1.0 - Math.Clamp(assessment.Certainty, 0, 1);

        var historicalVisits = (int)(value * 20);

        return new QValueEstimate
        {
            StateKey = stateKey,
            Value = Math.Clamp(value, 0.01, 1.0),
            PriorProbability = Math.Clamp(prior, 0.05, 1.0),
            Uncertainty = Math.Clamp(uncertainty, 0.01, 1.0),
            HistoricalVisits = historicalVisits
        };
    }

    public double EstimateRolloutValue(string state, string action, string? domain = null)
    {
        var key = $"{NormalizeState(state)}→{NormalizeState(action)}";
        var estimate = EstimateNode(key, domain);

        if (estimate.IsReliable)
            return estimate.Value;

        var relatedPaths = FindRelatedPathsInKG(key, domain);
        if (relatedPaths > 0)
            return Math.Max(0.3, (double)relatedPaths / Math.Max(1, relatedPaths + 1));

        return 0.15;
    }

    public List<QValueEstimate> RankBranches(
        string parentState,
        List<string> branches,
        string? domain = null,
        int depth = 0)
    {
        return branches
            .Select(b => EstimateNode($"{parentState}>{b}", domain, depth + 1))
            .OrderByDescending(e => e.Value)
            .ToList();
    }

    private double QueryKnowledgeGraph(string stateKey, string? domain)
    {
        try
        {
            var result = _graphBridge.QueryKnowledge(stateKey);
            if (result.FoundInGraph && result.SupportingTriplets.Count > 0)
            {
                var avgConfidence = result.SupportingTriplets.Average(t => t.Confidence);
                var pathBonus = result.RelatedEntities.Count > 0 ? 0.3 : 0;
                return Math.Clamp(avgConfidence + pathBonus, 0, 1);
            }

            var domainStats = _graphBridge.GetGraphStats();
            if (domainStats.TryGetValue("total_triplets", out var total) && total is int t && t > 100)
                return 0.1;
            return 0.05;
        }
        catch
        {
            return 0.05;
        }
    }

    private double QueryMemoryStore(string stateKey, string? domain)
    {
        try
        {
            var episodes = _memory.FindSimilarEpisodes(stateKey, domain, limit: 10);
            if (episodes.Count == 0)
                return 0.05;

            var avgReward = episodes.Average(e => (double)e.Reward);
            var avgConfidence = episodes.Average(e => (double)e.Confidence);
            var successRate = episodes.Count(e => e.WasSuccessful) / (double)Math.Max(1, episodes.Count);

            return Math.Clamp(0.30 * avgReward / 5.0 + 0.40 * avgConfidence + 0.30 * successRate, 0, 1);
        }
        catch
        {
            return 0.05;
        }
    }

    private int FindRelatedPathsInKG(string key, string? domain)
    {
        try
        {
            var result = _graphBridge.QueryKnowledge(key);
            return result.SupportingTriplets?.Count ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string NormalizeState(string state)
    {
        if (string.IsNullOrWhiteSpace(state))
            return "empty";

        var normalized = state.Trim().ToLowerInvariant();
        if (normalized.Length > 100)
            normalized = normalized[..100];

        return normalized;
    }
}
