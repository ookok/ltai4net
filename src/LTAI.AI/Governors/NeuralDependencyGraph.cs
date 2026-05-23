using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public record NeuronCluster
{
    public int ClusterId { get; init; }
    public List<int> MemberIndices { get; init; } = new();
    public float InternalCorrelation { get; init; }
    public string? DomainLabel { get; set; }
    public int ClusterSize => MemberIndices.Count;
}

public record HubNeuron
{
    public int Index { get; init; }
    public float ConnectivityScore { get; init; }
    public List<int> ConnectedClusters { get; init; } = new();
}

public record DependencyGraphReport
{
    public int TotalNeurons { get; init; }
    public List<NeuronCluster> Clusters { get; init; } = new();
    public List<HubNeuron> Hubs { get; init; } = new();
    public List<(int a, int b, float corr)> RedundancyPairs { get; init; } = new();
    public float SparsityScore { get; init; }
    public DateTime AnalyzedAt { get; init; } = DateTime.UtcNow;
}

/// Neural Dependency Graph — directly inspired by:
/// "Direct dependencies between neurons explain activity" (Lynn, Nature Physics 2025)
///
/// Core insight: neurons form sparse, functionally modular clusters with hubs.
/// Activity patterns emerge from connection topology, not individual neuron complexity.
/// This enables: (1) redundancy pruning, (2) expert auto-discovery, (3) hub identification.
public sealed class NeuralDependencyGraph
{
    private readonly ILogger<NeuralDependencyGraph> _logger;
    private DependencyGraphReport? _lastReport;

    public DependencyGraphReport? LastReport => _lastReport;

    public NeuralDependencyGraph(ILogger<NeuralDependencyGraph>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<NeuralDependencyGraph>.Instance;
    }

    /// Analyze LoRA weight matrix to build neuron dependency graph.
    /// Each row in the A matrix is treated as a "neuron" (logistic unit).
    /// We compute pairwise correlations between rows to find:
    ///   1. Co-active clusters (functional modules)
    ///   2. Hub neurons (high total connectivity)
    ///   3. Redundancy pairs (highly correlated → can prune one)
    public DependencyGraphReport Analyze(LoraLayer lora, string[]? domainLabels = null)
    {
        var rows = lora.OutputDim;
        var cols = lora.Rank;

        // Extract row vectors (each row = one "neuron")
        var neurons = new float[rows][];
        for (int i = 0; i < rows; i++)
        {
            neurons[i] = new float[cols];
            for (int j = 0; j < cols; j++)
                neurons[i][j] = lora.GetA(i, j);
        }

        // Compute pairwise Pearson correlation matrix
        var corrMatrix = ComputeCorrelationMatrix(neurons);

        // 1. Find clusters via thresholded correlation
        var clusters = FindClusters(corrMatrix, rows, threshold: 0.6f);

        // Auto-label clusters if domain labels provided
        if (domainLabels is not null)
        {
            foreach (var cluster in clusters)
            {
                var bestLabel = InferClusterLabel(cluster.MemberIndices, domainLabels);
                cluster.DomainLabel = bestLabel;
            }
        }

        // 2. Identify hub neurons (high total absolute correlation)
        var hubs = FindHubs(corrMatrix, rows, topK: 5);

        // 3. Find redundant pairs (correlation > 0.85)
        var redundant = FindRedundancyPairs(corrMatrix, rows, threshold: 0.85f);

        // Sparsity score: % of near-zero correlations
        int sparseCount = 0;
        int totalPairs = rows * (rows - 1) / 2;
        int pairIdx = 0;
        for (int i = 0; i < rows; i++)
        for (int j = i + 1; j < rows; j++, pairIdx++)
            if (MathF.Abs(corrMatrix[i, j]) < 0.1f) sparseCount++;
        var sparsityScore = totalPairs > 0 ? (float)sparseCount / totalPairs : 0;

        _lastReport = new DependencyGraphReport
        {
            TotalNeurons = rows, Clusters = clusters, Hubs = hubs,
            RedundancyPairs = redundant, SparsityScore = sparsityScore
        };

        _logger.LogInformation(
            "Dependency graph: neurons={N} clusters={C} hubs={H} redundant={R} sparsity={S:F2}",
            rows, clusters.Count, hubs.Count, redundant.Count, sparsityScore);

        return _lastReport;
    }

    /// Prune redundant neurons from a cluster, keeping only the strongest.
    /// Returns indices to keep and indices to remove.
    public (int[] keep, int[] remove) RecommendPrune(
        LoraLayer lora, int maxClusterSize = 3, float redundancyThreshold = 0.85f)
    {
        var report = Analyze(lora);
        var removeSet = new HashSet<int>();

        foreach (var cluster in report.Clusters)
        {
            if (cluster.ClusterSize <= maxClusterSize) continue;

            // Within each large cluster, keep the top-N neurons by norm (strongest signal)
            var ranked = cluster.MemberIndices
                .Select(idx =>
                {
                    float norm = 0;
                    for (int r = 0; r < lora.Rank; r++)
                        norm += lora.GetA(idx, r) * lora.GetA(idx, r);
                    return (idx, norm: MathF.Sqrt(norm));
                })
                .OrderByDescending(x => x.norm)
                .ToList();

            foreach (var (idx, _) in ranked.Skip(maxClusterSize))
                removeSet.Add(idx);
        }

        // Also remove neurons in redundancy pairs (keep the one with higher norm)
        foreach (var (a, b, _) in report.RedundancyPairs)
        {
            float normA = 0, normB = 0;
            for (int r = 0; r < lora.Rank; r++)
            {
                normA += lora.GetA(a, r) * lora.GetA(a, r);
                normB += lora.GetA(b, r) * lora.GetA(b, r);
            }
            if (normA >= normB) removeSet.Add(b);
            else removeSet.Add(a);
        }

        var keepIndices = Enumerable.Range(0, lora.OutputDim)
            .Where(i => !removeSet.Contains(i)).ToArray();
        var removeIndices = removeSet.ToArray();

        _logger.LogInformation(
            "Prune recommendation: keep={Keep}/{Total} remove={Remove} ({Pct:F0}%)",
            keepIndices.Length, lora.OutputDim, removeIndices.Length,
            100f * removeIndices.Length / lora.OutputDim);

        return (keepIndices, removeIndices);
    }

    private static float[,] ComputeCorrelationMatrix(float[][] neurons)
    {
        int n = neurons.Length;
        var corr = new float[n, n];

        // Compute means and stds once
        var means = new float[n];
        var stds = new float[n];
        for (int i = 0; i < n; i++)
        {
            means[i] = neurons[i].Average();
            var variance = neurons[i].Average(v => (v - means[i]) * (v - means[i]));
            stds[i] = MathF.Sqrt(variance);
        }

        for (int i = 0; i < n; i++)
        for (int j = i; j < n; j++)
        {
            if (i == j) { corr[i, j] = 1f; continue; }

            float cov = 0;
            int cols = neurons[i].Length;
            for (int k = 0; k < cols; k++)
                cov += (neurons[i][k] - means[i]) * (neurons[j][k] - means[j]);
            cov /= cols;

            var denom = stds[i] * stds[j];
            corr[i, j] = corr[j, i] = denom > 1e-8f ? cov / denom : 0;
        }

        return corr;
    }

    private static List<NeuronCluster> FindClusters(float[,] corr, int n, float threshold)
    {
        var visited = new bool[n];
        var clusters = new List<NeuronCluster>();

        for (int i = 0; i < n; i++)
        {
            if (visited[i]) continue;

            var cluster = new List<int>();
            var queue = new Queue<int>();
            queue.Enqueue(i);
            visited[i] = true;

            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                cluster.Add(node);

                for (int j = 0; j < n; j++)
                {
                    if (!visited[j] && MathF.Abs(corr[node, j]) >= threshold)
                    {
                        visited[j] = true;
                        queue.Enqueue(j);
                    }
                }
            }

            if (cluster.Count > 1) // Only meaningful clusters (size > 1)
            {
                float internalCorr = 0;
                int pairs = 0;
                foreach (var a in cluster)
                foreach (var b in cluster)
                    if (a < b) { internalCorr += MathF.Abs(corr[a, b]); pairs++; }
                internalCorr = pairs > 0 ? internalCorr / pairs : 0;

                clusters.Add(new NeuronCluster
                {
                    ClusterId = clusters.Count, MemberIndices = cluster,
                    InternalCorrelation = internalCorr
                });
            }

            visited[i] = true; // solo neurons not in any cluster
        }

        return clusters.OrderByDescending(c => c.ClusterSize).ToList();
    }

    private static List<HubNeuron> FindHubs(float[,] corr, int n, int topK)
    {
        var scores = new (int idx, float score)[n];
        for (int i = 0; i < n; i++)
        {
            float total = 0;
            for (int j = 0; j < n; j++)
                if (i != j) total += MathF.Abs(corr[i, j]);
            scores[i] = (i, total / (n - 1));
        }

        return scores.OrderByDescending(s => s.score)
            .Take(topK)
            .Select(s => new HubNeuron
            {
                Index = s.idx, ConnectivityScore = s.score,
                ConnectedClusters = FindHubConnections(corr, s.idx, n)
            })
            .ToList();
    }

    private static List<int> FindHubConnections(float[,] corr, int hubIdx, int n)
    {
        var connections = new List<int>();
        for (int j = 0; j < n; j++)
            if (j != hubIdx && MathF.Abs(corr[hubIdx, j]) > 0.5f)
                connections.Add(j);
        return connections;
    }

    private static List<(int a, int b, float corr)> FindRedundancyPairs(
        float[,] corr, int n, float threshold)
    {
        var pairs = new List<(int, int, float)>();
        for (int i = 0; i < n; i++)
        for (int j = i + 1; j < n; j++)
            if (corr[i, j] >= threshold)
                pairs.Add((i, j, corr[i, j]));
        return pairs.OrderByDescending(p => p.Item3).ToList();
    }

    private static string? InferClusterLabel(List<int> members, string[] labels)
    {
        if (members.Count == 0) return null;
        var best = labels.Where(l => members.Any(m =>
            MathF.Abs(m.GetHashCode() % (labels.Length + 1)) ==
            MathF.Abs(l.GetHashCode() % (labels.Length + 1))))
            .GroupBy(l => l).OrderByDescending(g => g.Count())
            .Select(g => g.Key).FirstOrDefault();
        return best;
    }
}
