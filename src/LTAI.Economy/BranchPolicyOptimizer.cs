using LTAI.TreeLLM.Session;
using Microsoft.Extensions.Logging;

namespace LTAI.Economy;

public sealed record BranchPolicyStats
{
    public List<BranchDecision> Decisions { get; init; } = new();
    public double AvgBranchReward { get; init; }
    public double BestBranchCount { get; init; }
    public double OptimalDepth { get; init; }
    public double BranchSuccessRate { get; init; }
    public Dictionary<int, double> DepthSuccessRates { get; init; } = new();
    public Dictionary<int, double> BranchCountRewards { get; init; } = new();
}

public sealed record BranchTrainingResult
{
    public double AvgLoss { get; init; }
    public double PolicyImprovement { get; init; }
    public List<BranchPolicyStats> EpochStats { get; init; } = new();
    public int TotalDecisions { get; init; }
    public int Epochs { get; init; }
    public double ElapsedMs { get; init; }
}

public sealed class BranchPolicyOptimizer
{
    private readonly Dictionary<int, double> _depthBranchWeights = new();
    private readonly Dictionary<int, double> _branchCountRewards = new();
    private readonly List<BranchDecision> _history = new();
    private const double LearningRate = 0.03;
    private const int MaxHistory = 500;
    private readonly object _lock = new();
    private readonly ILogger<BranchPolicyOptimizer>? _logger;

    private static readonly int[] DefaultBranchCounts = { 1, 2, 3, 4, 5, 6 };

    public BranchPolicyOptimizer(ILogger<BranchPolicyOptimizer>? logger = null)
    {
        _logger = logger;

        for (int d = 0; d <= 10; d++)
            _depthBranchWeights[d] = 0.5;
        for (int b = 1; b <= 6; b++)
            _branchCountRewards[b] = 0.5;
    }

    public (bool shouldBranch, int numBranches) Decide(
        string nodeTask, int depth, double nodeConfidence,
        int maxBranches = 4, int maxDepth = 5)
    {
        if (depth >= maxDepth)
            return (false, 0);

        if (nodeConfidence < 0.2)
            return (false, 0);

        lock (_lock)
        {
            var depthWeight = _depthBranchWeights.GetValueOrDefault(depth, 0.5);
            var shouldBranch = depthWeight > 0.3 && nodeConfidence > 0.3;

            if (!shouldBranch)
                return (false, 0);

            var bestBranchCount = 1;
            double bestReward = 0;

            for (int b = 1; b <= maxBranches; b++)
            {
                var reward = _branchCountRewards.GetValueOrDefault(b, 0.5);
                if (reward > bestReward)
                {
                    bestReward = reward;
                    bestBranchCount = b;
                }
            }

            var taskComplexity = Math.Min(1.0, nodeTask.Length / 500.0);
            var adjustedBranches = (int)Math.Round(bestBranchCount * (0.7 + taskComplexity * 0.3));
            adjustedBranches = Math.Max(1, Math.Min(maxBranches, adjustedBranches));

            return (true, adjustedBranches);
        }
    }

    public void RecordOutcome(List<BranchDecision> decisions, ParallelGraphResult result)
    {
        lock (_lock)
        {
            foreach (var decision in decisions)
            {
                decision.OutcomeReward = CalculateOutcomeReward(decision, result);
                decision.WasBeneficial = decision.OutcomeReward > 0.5;

                var nodeResult = result.AllNodes
                    .FirstOrDefault(n => n.Id == decision.NodeId);

                UpdateDepthWeights(decision, nodeResult);
                UpdateBranchCountRewards(decision);

                _history.Add(decision);
            }

            while (_history.Count > MaxHistory)
                _history.RemoveAt(0);
        }
    }

    public BranchTrainingResult Train(
        List<(List<BranchDecision> Decisions, ParallelGraphResult Result)> traces,
        int epochs = 5)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var epochStats = new List<BranchPolicyStats>();
        double prevLoss = double.MaxValue;

        for (int epoch = 0; epoch < epochs; epoch++)
        {
            double totalLoss = 0;
            int totalDecisions = 0;

            foreach (var (decisions, result) in traces)
            {
                foreach (var decision in decisions)
                {
                    decision.OutcomeReward = CalculateOutcomeReward(decision, result);
                    decision.WasBeneficial = decision.OutcomeReward > 0.5;

                    UpdateDepthWeights(decision, null);
                    UpdateBranchCountRewards(decision);

                    var predictedReward = _branchCountRewards.GetValueOrDefault(decision.NumBranches, 0.5);
                    var loss = Math.Pow(predictedReward - decision.OutcomeReward, 2);
                    totalLoss += loss;
                    totalDecisions++;
                }
            }

            var avgLoss = totalDecisions > 0 ? totalLoss / totalDecisions : 0;
            var improvement = prevLoss != double.MaxValue ? prevLoss - avgLoss : 0;
            prevLoss = avgLoss;

            epochStats.Add(GetPolicyStats());
        }

        sw.Stop();

        return new BranchTrainingResult
        {
            AvgLoss = prevLoss,
            PolicyImprovement = epochStats.Count >= 2
                ? epochStats[0].AvgBranchReward - epochStats.Last().AvgBranchReward
                : 0,
            EpochStats = epochStats,
            TotalDecisions = traces.Sum(t => t.Decisions.Count),
            Epochs = epochs,
            ElapsedMs = sw.ElapsedMilliseconds
        };
    }

    public BranchPolicyStats GetPolicyStats()
    {
        lock (_lock)
        {
            var beneficial = _history.Where(d => d.WasBeneficial).ToList();

            return new BranchPolicyStats
            {
                Decisions = _history.TakeLast(50).ToList(),
                AvgBranchReward = _history.Count > 0 ? _history.Average(d => d.OutcomeReward) : 0,
                BestBranchCount = _branchCountRewards.OrderByDescending(kv => kv.Value).First().Key,
                OptimalDepth = _depthBranchWeights.OrderByDescending(kv => kv.Value).First().Key,
                BranchSuccessRate = _history.Count > 0
                    ? (double)beneficial.Count / _history.Count
                    : 0,
                DepthSuccessRates = _history
                    .GroupBy(d => d.Depth)
                    .ToDictionary(g => g.Key, g => g.Any()
                        ? (double)g.Count(d => d.WasBeneficial) / g.Count()
                        : 0),
                BranchCountRewards = new(_branchCountRewards)
            };
        }
    }

    public Dictionary<int, double> GetLearnedBranchRewards() { lock (_lock) return new(_branchCountRewards); }
    public Dictionary<int, double> GetDepthWeights() { lock (_lock) return new(_depthBranchWeights); }

    private void UpdateDepthWeights(BranchDecision decision, ParallelNode? nodeResult)
    {
        var depth = decision.Depth;
        var current = _depthBranchWeights.GetValueOrDefault(depth, 0.5);

        var reward = decision.OutcomeReward;
        if (nodeResult != null)
            reward = (reward + nodeResult.Confidence) / 2.0;

        _depthBranchWeights[depth] = current * (1.0 - LearningRate) + reward * LearningRate;
        _depthBranchWeights[depth] = Math.Max(0.1, Math.Min(0.95, _depthBranchWeights[depth]));

        for (int d = depth - 1; d >= 0; d--)
        {
            var parentWeight = _depthBranchWeights.GetValueOrDefault(d, 0.5);
            _depthBranchWeights[d] = parentWeight * (1.0 - LearningRate * 0.5) + reward * LearningRate * 0.5 * 0.5;
            _depthBranchWeights[d] = Math.Max(0.1, Math.Min(0.95, _depthBranchWeights[d]));
        }
    }

    private void UpdateBranchCountRewards(BranchDecision decision)
    {
        var count = decision.NumBranches;
        var current = _branchCountRewards.GetValueOrDefault(count, 0.5);
        _branchCountRewards[count] = current * (1.0 - LearningRate) + decision.OutcomeReward * LearningRate;
        _branchCountRewards[count] = Math.Max(0.1, Math.Min(0.95, _branchCountRewards[count]));

        for (int b = 1; b <= 6; b++)
        {
            if (b == count) continue;
            var dist = Math.Abs(b - count);
            var decay = Math.Pow(0.8, dist);
            var other = _branchCountRewards.GetValueOrDefault(b, 0.5);
            _branchCountRewards[b] = other * (1.0 - LearningRate * 0.1 * decay)
                + decision.OutcomeReward * LearningRate * 0.1 * decay;
            _branchCountRewards[b] = Math.Max(0.1, Math.Min(0.95, _branchCountRewards[b]));
        }
    }

    private static double CalculateOutcomeReward(BranchDecision decision, ParallelGraphResult result)
    {
        double reward = 0.5;

        var childNodes = result.AllNodes
            .Where(n => n.ParentIds.Contains(decision.NodeId))
            .ToList();

        if (childNodes.Count > 0)
        {
            var avgChildConfidence = childNodes.Average(n => n.Confidence);
            reward += avgChildConfidence * 0.3;
        }

        var allChildIds = childNodes.Select(n => n.Id).ToHashSet();
        var descendantNodes = result.AllNodes
            .Where(n => n.Parent != null && IsDescendantOf(n, decision.NodeId, result.AllNodes))
            .ToList();

        if (descendantNodes.Count > 0)
        {
            reward += Math.Min(0.2, descendantNodes.Count * 0.02);
        }

        if (childNodes.Count > 1)
        {
            var diversityBonus = CalculateDiversityBonus(childNodes);
            reward += diversityBonus * 0.15;
        }

        var depthPenalty = decision.Depth * 0.03;
        reward -= depthPenalty;

        if (decision.NumBranches == 0)
            reward -= 0.2;

        return Math.Max(0.05, Math.Min(0.98, reward));
    }

    private static bool IsDescendantOf(ParallelNode node, string ancestorId, List<ParallelNode> allNodes)
    {
        var current = node.Parent;
        while (current != null)
        {
            if (current.Id == ancestorId) return true;
            current = current.Parent;
        }
        return false;
    }

    private static double CalculateDiversityBonus(List<ParallelNode> childNodes)
    {
        if (childNodes.Count < 2) return 0;

        var texts = childNodes
            .Select(n => n.Result ?? "")
            .Where(t => t.Length > 10)
            .ToList();

        if (texts.Count < 2) return 0;

        double totalOverlap = 0;
        int pairs = 0;

        for (int i = 0; i < texts.Count; i++)
        {
            for (int j = i + 1; j < texts.Count; j++)
            {
                var shorter = texts[i].Length < texts[j].Length ? texts[i] : texts[j];
                var longer = texts[i].Length >= texts[j].Length ? texts[i] : texts[j];
                var overlap = (double)CountCommonWords(shorter, longer) / Math.Max(1, shorter.Split(' ').Length);
                totalOverlap += overlap;
                pairs++;
            }
        }

        var avgOverlap = pairs > 0 ? totalOverlap / pairs : 1.0;
        return 1.0 - avgOverlap;
    }

    private static int CountCommonWords(string a, string b)
    {
        var wordsA = new HashSet<string>(a.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.ToLowerInvariant()));
        var wordsB = new HashSet<string>(b.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.ToLowerInvariant()));
        return wordsA.Intersect(wordsB).Count();
    }
}
