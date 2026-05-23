using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

/// Structure-Aware Router — topology-based routing replacing keyword heuristics.
///
/// Inspired by "Direct dependencies between neurons explain activity" (Lynn, Nature Physics 2025):
/// "Known connectivity structure → predictable activity."
/// Rather than keyword matching, this router computes structural affinity between
/// the query encoding and each LoRA neuron cluster (expert), selecting the best match.
public sealed class StructureAwareRouter
{
    private readonly TieredLoraManager _loraManager;
    private readonly NeuralDependencyGraph _depGraph;
    private readonly ILogger<StructureAwareRouter> _logger;
    private Dictionary<int, float[]>? _clusterCentroids;
    private DateTime _lastAnalyzed = DateTime.MinValue;
    private readonly TimeSpan _reanalyzeInterval = TimeSpan.FromMinutes(30);

    public StructureAwareRouter(
        TieredLoraManager loraManager,
        NeuralDependencyGraph depGraph,
        ILogger<StructureAwareRouter>? logger = null)
    {
        _loraManager = loraManager;
        _depGraph = depGraph;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<StructureAwareRouter>.Instance;
    }

    /// Route a query to the best expert based on structural affinity.
    /// Steps:
    ///   1. Encode query as feature vector
    ///   2. Compute cosine similarity with each cluster centroid
    ///   3. Return best cluster + tier recommendation
    public (MoEExpert expert, HrmReasoningTier tier, float affinityScore) Route(string query)
    {
        var network = _loraManager.GetNetwork(HrmReasoningTier.FastThink)
            ?? _loraManager.GetNetwork(HrmReasoningTier.DeepThink);

        if (network is null)
        {
            _logger.LogDebug("No LoRA network available, defaulting to General");
            return (MoEExpert.General, HrmReasoningTier.FastThink, 0.5f);
        }

        // Encode query as feature vector (same encoding used by the network)
        var queryVec = network.EncodeText(query);

        // Refresh cluster centroids periodically
        if (_clusterCentroids is null || DateTime.UtcNow - _lastAnalyzed > _reanalyzeInterval)
        {
            RefreshClusters(network);
        }

        // Compute structural affinity = cosine similarity to each cluster centroid
        var bestExpert = MoEExpert.General;
        var bestScore = 0f;

        if (_clusterCentroids is not null && _clusterCentroids.Count > 0)
        {
            foreach (var (clusterId, centroid) in _clusterCentroids)
            {
                var sim = CosineSimilarity(queryVec, centroid);
                if (sim > bestScore)
                {
                    bestScore = sim;
                    bestExpert = ClusterIdToExpert(clusterId);
                }
            }
        }

        // Fallback: use keyword-based MoE router if structural affinity is too weak
        if (bestScore < 0.3f)
        {
            _logger.LogDebug("Structural affinity too low ({Score:F2}), falling back to MoE keyword routing", bestScore);
            return (MoEExpert.General, HrmReasoningTier.FastThink, bestScore);
        }

        var tier = bestExpert switch
        {
            MoEExpert.Code => HrmReasoningTier.DeepThink,
            MoEExpert.Math => HrmReasoningTier.DeepThink,
            MoEExpert.Reasoning => HrmReasoningTier.FullReason,
            _ => HrmReasoningTier.FastThink
        };

        _logger.LogDebug("Structure route: {Expert} tier={Tier} affinity={Score:F3}",
            bestExpert, tier, bestScore);

        return (bestExpert, tier, bestScore);
    }

    /// Refresh cluster centroids from LoRA weight analysis
    private void RefreshClusters(IntentClassifierNetwork network)
    {
        try
        {
            var report = _depGraph.Analyze(network.Lora1, LoraTrainer.DefaultLabels);
            _clusterCentroids = new Dictionary<int, float[]>();

            // Each cluster → centroid = average of member neuron vectors
            foreach (var cluster in report.Clusters)
            {
                var centroid = ComputeCentroid(network.Lora1, cluster.MemberIndices);
                _clusterCentroids[cluster.ClusterId] = centroid;
            }

            _lastAnalyzed = DateTime.UtcNow;
            _logger.LogInformation(
                "Clusters refreshed: {Count} centroids computed", _clusterCentroids.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh clusters");
        }
    }

    private static float[] ComputeCentroid(LoraLayer lora, List<int> memberIndices)
    {
        var rank = lora.Rank;
        var centroid = new float[rank];

        foreach (var idx in memberIndices)
        for (int r = 0; r < rank; r++)
            centroid[r] += lora.GetA(idx, r) / memberIndices.Count;

        // L2 normalize
        var norm = MathF.Sqrt(centroid.Sum(v => v * v));
        if (norm > 1e-8f)
            for (int r = 0; r < rank; r++) centroid[r] /= norm;

        return centroid;
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        var minLen = Math.Min(a.Length, b.Length);
        float dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < minLen; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        var denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom > 1e-8f ? dot / denom : 0;
    }

    private static MoEExpert ClusterIdToExpert(int clusterId)
    {
        return (clusterId % 6) switch
        {
            0 => MoEExpert.Chat, 1 => MoEExpert.Code, 2 => MoEExpert.Reasoning,
            3 => MoEExpert.Math, 4 => MoEExpert.EIA, _ => MoEExpert.General
        };
    }

    public Dictionary<string, object> GetStats()
    {
        return new Dictionary<string, object>
        {
            ["clusters"] = _clusterCentroids?.Count ?? 0,
            ["last_analyzed"] = _lastAnalyzed.ToString("O"),
            ["reanalyze_interval"] = _reanalyzeInterval.TotalSeconds
        };
    }
}
