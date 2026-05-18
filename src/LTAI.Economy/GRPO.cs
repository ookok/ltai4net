using System.Collections.Concurrent;
using LTAI.Economy.Models;

namespace LTAI.Economy;

public sealed class LatentEncoder
{
    private readonly double[][] _w;
    private readonly double[] _b;
    private readonly int _inputDim;
    private readonly int _latentDim;
    private readonly Random _rng = new();

    public LatentEncoder(int inputDim = 30, int latentDim = 6)
    {
        _inputDim = inputDim;
        _latentDim = latentDim;
        _w = new double[latentDim][];
        _b = new double[latentDim];

        double scale = Math.Sqrt(2.0 / (inputDim + latentDim));
        for (int j = 0; j < latentDim; j++)
        {
            _w[j] = new double[inputDim];
            for (int i = 0; i < inputDim; i++)
                _w[j][i] = SampleNormal() * scale;
            _b[j] = 0.0;
        }
    }

    public double[] Encode(Dictionary<string, double> features, string[] featureOrder)
    {
        var z = new double[_latentDim];
        for (int j = 0; j < _latentDim; j++)
        {
            double sum = _b[j];
            for (int i = 0; i < featureOrder.Length && i < _inputDim; i++)
                sum += _w[j][i] * features.GetValueOrDefault(featureOrder[i], 0.0);
            z[j] = Math.Clamp(sum, -5.0, 5.0);
        }
        return z;
    }

    public double Update(Dictionary<string, double> features, string[] featureOrder, double[] targetZ, double lr = 0.01)
    {
        var z = Encode(features, featureOrder);
        double loss = 0.0;
        var errors = new double[_latentDim];

        for (int j = 0; j < _latentDim; j++)
        {
            errors[j] = targetZ[j] - z[j];
            loss += errors[j] * errors[j];
        }
        loss /= _latentDim;

        for (int j = 0; j < _latentDim; j++)
        {
            for (int i = 0; i < featureOrder.Length && i < _inputDim; i++)
                _w[j][i] += lr * errors[j] * features.GetValueOrDefault(featureOrder[i], 0.0);
            _b[j] += lr * errors[j];
        }

        return loss;
    }

    public double FeedbackAlign(Dictionary<string, double> features, string[] featureOrder, double[] errorSignal, double[][] feedbackMatrix, double lr = 0.01)
    {
        var z = Encode(features, featureOrder);
        var grad = new double[_inputDim];

        for (int i = 0; i < _inputDim && i < feedbackMatrix.Length; i++)
        {
            for (int j = 0; j < _latentDim && j < feedbackMatrix[i].Length; j++)
                grad[i] += feedbackMatrix[i][j] * errorSignal[j];
            grad[i] /= _latentDim;
        }

        for (int i = 0; i < featureOrder.Length && i < _inputDim; i++)
        {
            for (int j = 0; j < _latentDim; j++)
                _w[j][i] += lr * grad[i] * features.GetValueOrDefault(featureOrder[i], 0.0);
        }
        for (int j = 0; j < _latentDim; j++)
            _b[j] += lr * errorSignal[j];

        double al = ComputeAlignment(feedbackMatrix);
        return al;
    }

    private double ComputeAlignment(double[][] feedbackMatrix)
    {
        double dot = 0.0, normF = 0.0, normW = 0.0;
        int d = Math.Min(_latentDim, feedbackMatrix.Length);
        for (int i = 0; i < d; i++)
        {
            for (int j = 0; j < d && j < feedbackMatrix[i].Length; j++)
            {
                dot += feedbackMatrix[i][j] * _w[j][Math.Min(i, _inputDim - 1)];
                normF += feedbackMatrix[i][j] * feedbackMatrix[i][j];
            }
        }
        for (int j = 0; j < _latentDim; j++)
            for (int i = 0; i < _inputDim; i++)
                normW += _w[j][i] * _w[j][i];

        double denom = Math.Sqrt(normF * normW);
        return denom > 1e-10 ? dot / denom : 0.0;
    }

    private double SampleNormal()
    {
        double u1 = 1.0 - _rng.NextDouble();
        double u2 = 1.0 - _rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }
}

public sealed class LatentGRPO
{
    private readonly LatentEncoder _encoder;
    private readonly double[] _zStar;
    private readonly double[] _g2Dim;
    private readonly double[][] _feedbackMatrix;
    private readonly int _latentDim;
    private double _learningRate = 0.03;
    private readonly double _gamma = 0.3;
    private readonly double _epsilon = 1e-6;

    public int RoundId { get; private set; }
    public LatentEncoder Encoder => _encoder;
    public IReadOnlyList<double> ZStar => _zStar;

    public LatentGRPO(int latentDim = 6, double learningRate = 0.03)
    {
        _latentDim = latentDim;
        _learningRate = learningRate;
        _encoder = new LatentEncoder(30, latentDim);
        _zStar = new double[latentDim];
        _g2Dim = new double[latentDim];
        Array.Fill(_g2Dim, 1e-3);

        var rng = new Random();
        _feedbackMatrix = new double[latentDim][];
        for (int i = 0; i < latentDim; i++)
        {
            _feedbackMatrix[i] = new double[latentDim];
            for (int j = 0; j < latentDim; j++)
            {
                double u1 = 1.0 - rng.NextDouble();
                double u2 = 1.0 - rng.NextDouble();
                _feedbackMatrix[i][j] = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2) / Math.Sqrt(latentDim);
            }
        }
    }

    public LatentGRPOResult Optimize(
        IReadOnlyList<(string Action, Dictionary<string, double> Features)> group,
        IReadOnlyDictionary<string, double> actualOutcomes)
    {
        RoundId++;
        int n = group.Count;
        if (n == 0)
            return new LatentGRPOResult { RoundId = RoundId, Convergence = 0.0 };

        var featureOrder = group[0].Features.Keys.ToArray();
        var encoded = new double[n][];
        var scores = new double[n];
        var outcomes = new double[n];

        for (int i = 0; i < n; i++)
        {
            encoded[i] = _encoder.Encode(group[i].Features, featureOrder);
            double dist = 0.0;
            for (int j = 0; j < _latentDim; j++)
                dist += (encoded[i][j] - _zStar[j]) * (encoded[i][j] - _zStar[j]);
            scores[i] = Math.Exp(-dist / _latentDim);
            outcomes[i] = actualOutcomes.GetValueOrDefault(group[i].Action, 0.5);
        }

        double scoreMean = scores.Average();
        double scoreStd = StdDev(scores, scoreMean);
        double scoreStdSafe = Math.Max(scoreStd, 1e-8);

        double outcomeMean = outcomes.Average();
        double outcomeStd = StdDev(outcomes, outcomeMean);
        double outcomeStdSafe = Math.Max(outcomeStd, 1e-8);

        var advantages = new double[n];
        double totalWeight = 0.0;
        var zUpdate = new double[_latentDim];

        for (int i = 0; i < n; i++)
        {
            advantages[i] = 0.6 * (scores[i] - scoreMean) / scoreStdSafe
                          + 0.4 * (outcomes[i] - outcomeMean) / outcomeStdSafe;
            if (advantages[i] > 0)
            {
                totalWeight += advantages[i];
                for (int j = 0; j < _latentDim; j++)
                    zUpdate[j] += advantages[i] * (encoded[i][j] - _zStar[j]);
            }
        }

        if (totalWeight > 1e-10)
        {
            for (int j = 0; j < _latentDim; j++)
                zUpdate[j] /= totalWeight;
        }

        double effectiveG2 = 0.0;
        for (int j = 0; j < _latentDim; j++)
        {
            _g2Dim[j] = 0.95 * _g2Dim[j] + 0.05 * zUpdate[j] * zUpdate[j];
            effectiveG2 += (zUpdate[j] * zUpdate[j]) / (_g2Dim[j] + _epsilon);
        }

        double etaStar = _gamma / (effectiveG2 + _epsilon);

        for (int j = 0; j < _latentDim; j++)
        {
            double step = etaStar / (_g2Dim[j] + _epsilon) * zUpdate[j];
            _zStar[j] = Math.Clamp(_zStar[j] + step, -3.0, 3.0);
        }

        int bestIdx = 0;
        double bestScore = double.MinValue;
        for (int i = 0; i < n; i++)
        {
            if (scores[i] > bestScore)
            {
                bestScore = scores[i];
                bestIdx = i;
            }
        }

        double reconLoss = _encoder.Update(group[bestIdx].Features, featureOrder, _zStar, _learningRate * 0.5);

        double kl = ComputeKL();

        return new LatentGRPOResult
        {
            RoundId = RoundId,
            InputFeatures = group.Select(g => g.Features).ToList(),
            LatentZ = _zStar.ToArray().ToList(),
            Advantages = advantages.ToList(),
            LatentPolicyUpdate = zUpdate.ToArray().ToList(),
            ReconstructionLoss = reconLoss,
            KlDivergence = kl,
            Convergence = ComputeConvergence(scores)
        };
    }

    private double ComputeKL()
    {
        double kl = 0.0;
        for (int j = 0; j < _latentDim; j++)
        {
            double z2 = _zStar[j] * _zStar[j];
            kl += 0.5 * (-z2 - 1.0 + Math.Log(Math.Max(z2 + 1e-10, 1e-10)));
        }
        return kl;
    }

    private static double StdDev(double[] values, double mean)
    {
        if (values.Length < 2) return 0.0;
        double sum = 0.0;
        for (int i = 0; i < values.Length; i++)
            sum += (values[i] - mean) * (values[i] - mean);
        return Math.Sqrt(sum / (values.Length - 1));
    }

    private static double ComputeConvergence(IReadOnlyList<double> values, double multiplier = 1.0, int minCount = 10)
    {
        if (values.Count < minCount) return 0.0;
        int start = values.Count - minCount;
        double sum = 0.0, sumSq = 0.0;
        for (int i = start; i < values.Count; i++)
        {
            sum += values[i];
            sumSq += values[i] * values[i];
        }
        double mean = sum / minCount;
        double variance = sumSq / minCount - mean * mean;
        return Math.Clamp(1.0 - Math.Sqrt(Math.Max(variance, 0)) * multiplier, 0.0, 1.0);
    }

    private static Lazy<LatentGRPO> _instance = new(() => new LatentGRPO());
    public static LatentGRPO Instance => _instance.Value;
}

public sealed class SpatialGRPOOptimizer
{
    public double LearningRate { get; set; } = 0.05;
    public int GroupSize { get; set; } = 8;
    private readonly Dictionary<string, double> _spatialWeights = new();
    private readonly Random _rng = new();

    public double ScoreBaseline(Dictionary<string, double> features)
    {
        return features.GetValueOrDefault("capability", 0.5) * 0.4
             + (1.0 - Math.Min(features.GetValueOrDefault("latency_norm", 0.5), 0.95)) * 0.2
             + (1.0 - Math.Min(features.GetValueOrDefault("cost_norm", 0.5), 0.95)) * 0.2
             + features.GetValueOrDefault("reliability", 0.5) * 0.2;
    }

    public double ScoreTotal(Dictionary<string, double> features, SpatialContext spatial)
    {
        double baseline = ScoreBaseline(features);
        double center = features.GetValueOrDefault("centrality", 0.0);
        double density = spatial.GraphDensity;
        double prec = features.GetValueOrDefault("precedence", 0.0);
        double boundary = features.GetValueOrDefault("boundary", 0.0);
        double cycles = features.GetValueOrDefault("cycles", 0.0);
        double gravity = spatial.GravityFieldEntropy;

        double spatialContrib = center * 0.2 + density * 0.1 + prec * 0.2
            + boundary * 0.1 + cycles * 0.1 + gravity * 0.1;
        return baseline * 0.6 + Math.Clamp(spatialContrib, 0.0, 0.4) * 0.4;
    }

    public SGRPOResult Optimize(
        IReadOnlyList<(string Id, Dictionary<string, double> Features)> group,
        SpatialContext spatial,
        IReadOnlyDictionary<string, double> actualOutcomes)
    {
        int n = group.Count;
        var decisions = new List<SpatialReward>(n);
        double avgSpatialDelta = 0.0;
        string bestId = "";
        double bestScore = double.MinValue;

        foreach (var (id, features) in group)
        {
            double baseline = ScoreBaseline(features);
            double total = ScoreTotal(features, spatial);
            double delta = total - baseline;
            double outcome = actualOutcomes.GetValueOrDefault(id, 0.5);

            decisions.Add(new SpatialReward
            {
                StepId = id,
                TotalReward = total,
                BaselineReward = baseline,
                SpatialDelta = delta,
                ProvidersInvolved = features.GetValueOrDefault("providers", 0.0),
                SpatialFeaturesUsed = spatial.EntityCount,
                Timestamp = DateTime.UtcNow
            });

            avgSpatialDelta += delta;

            if (total > bestScore)
            {
                bestScore = total;
                bestId = id;
            }

            double advantage = outcome - outcome / Math.Max(n, 1);
            if (delta > 0.01 && advantage > 0)
            {
                string key = $"spatial_{id}";
                _spatialWeights.TryGetValue(key, out double w);
                _spatialWeights[key] = Math.Clamp(w + LearningRate * advantage * delta, -1.0, 1.0);
            }
        }

        avgSpatialDelta /= Math.Max(n, 1);

        return new SGRPOResult
        {
            RoundId = Environment.TickCount,
            Decisions = decisions,
            AvgSpatialDelta = avgSpatialDelta,
            BestDecisionId = bestId,
            SpatialPolicyUpdate = decisions.Select(d => d.SpatialDelta).ToList(),
            ConvergenceScore = 0.5
        };
    }

    public (string Status, double ClosureRate, double SpatialEfficiency) ClosedLoopValidation(
        IReadOnlyList<Dictionary<string, double>> actions,
        double baselineQuality,
        double withSpatialQuality)
    {
        int improved = 0, degraded = 0;
        double totalImprove = 0.0, totalDegrade = 0.0;

        foreach (var a in actions)
        {
            double delta = withSpatialQuality - baselineQuality;
            if (delta > 0.01) { improved++; totalImprove += delta; }
            else if (delta < -0.01) { degraded++; totalDegrade += Math.Abs(delta); }
        }

        int total = actions.Count;
        double closureRate = total > 0 ? (double)improved / total : 0.0;
        double efficiency = totalDegrade > 0.001 ? totalImprove / totalDegrade : totalImprove > 0 ? 999.0 : 0.0;

        string status = closureRate > 0.5 ? "closed" : closureRate > 0.3 ? "partial" : "open";
        return (status, closureRate, efficiency);
    }

    private static Lazy<SpatialGRPOOptimizer> _instance = new(() => new SpatialGRPOOptimizer());
    public static SpatialGRPOOptimizer Instance => _instance.Value;
}

public sealed class SurrogateRewardModel
{
    private readonly Dictionary<string, double> _weights = new();
    private double _bias;
    private readonly Dictionary<string, double> _g2Avg = new();
    private double _g2BiasAvg = 1e-3;
    private const double G2Decay = 0.95;
    public double LearningRate { get; set; } = 0.01;
    public int FeatureDim { get; } = 10;
    public double Gamma { get; set; } = 0.3;
    public double Epsilon { get; set; } = 1e-6;

    public SurrogateRewardModel(double learningRate = 0.01)
    {
        LearningRate = learningRate;
    }

    public double Predict(Dictionary<string, double> features)
    {
        double sum = _bias;
        foreach (var (key, value) in features)
        {
            _weights.TryGetValue(key, out double w);
            sum += w * value;
        }
        return Math.Clamp(sum, 0.0, 1.0);
    }

    public double TrainStep(Dictionary<string, double> features, double target)
    {
        double pred = Predict(features);
        double error = target - pred;

        foreach (var (key, value) in features)
        {
            double grad = -2.0 * error * value;
            if (!_g2Avg.ContainsKey(key)) _g2Avg[key] = 1e-3;
            _g2Avg[key] = G2Decay * _g2Avg[key] + (1.0 - G2Decay) * grad * grad;
            if (!_weights.ContainsKey(key)) _weights[key] = 0.0;
        }

        double biasGrad = -2.0 * error;
        _g2BiasAvg = G2Decay * _g2BiasAvg + (1.0 - G2Decay) * biasGrad * biasGrad;

        double effectiveG2 = 0.0;
        foreach (var (key, value) in features)
        {
            double grad = -2.0 * error * value;
            effectiveG2 += (grad * grad) / (_g2Avg[key] + Epsilon);
        }
        effectiveG2 += (biasGrad * biasGrad) / (_g2BiasAvg + Epsilon);

        double etaStar = Gamma * Math.Abs(error) / (effectiveG2 + Epsilon);

        foreach (var (key, value) in features)
        {
            double grad = -2.0 * error * value;
            _weights[key] -= etaStar / (_g2Avg[key] + Epsilon) * grad;
        }
        _bias -= etaStar / (_g2BiasAvg + Epsilon) * biasGrad;

        return error * error;
    }

    public double TrainBatch(IReadOnlyList<(Dictionary<string, double> Features, double Target)> batch)
    {
        if (batch.Count == 0) return 0.0;
        double totalLoss = 0.0;
        foreach (var (features, target) in batch)
            totalLoss += TrainStep(features, target);
        return totalLoss / batch.Count;
    }
}

public sealed class TDMRewardOptimizer
{
    private readonly SurrogateRewardModel _surrogate;
    private readonly List<TrajectoryReward> _trajectoryHistory = new();
    private const int MaxHistory = 50;
    private readonly Random _rng = new();

    private static readonly IReadOnlyDictionary<string, double> DefaultStagePriors = new Dictionary<string, double>
    {
        ["perceive"] = 0.08, ["cognize"] = 0.12, ["ontogrow"] = 0.08,
        ["plan"] = 0.15, ["simulate"] = 0.10, ["execute"] = 0.25,
        ["reflect"] = 0.12, ["evolve"] = 0.10,
        ["tree_decompose"] = 0.20, ["flow_refine"] = 0.35,
        ["skeleton_build"] = 0.15, ["diffusion_step"] = 0.30,
        ["rag_round_1"] = 0.30, ["rag_round_2"] = 0.25,
        ["rag_round_3"] = 0.20, ["rag_round_4"] = 0.15, ["rag_round_5"] = 0.10,
    };

    public TDMRewardOptimizer(double surrogateLr = 0.01)
    {
        _surrogate = new SurrogateRewardModel(surrogateLr);
    }

    public SurrogateRewardModel Surrogate => _surrogate;

    public TrajectoryReward DistributeRewards(
        string trajectoryId, string[] stageNames,
        IReadOnlyList<Dictionary<string, double>> stageContexts,
        double totalOutcome, RewardType rewardType,
        IReadOnlyDictionary<string, double>? stageWeights = null)
    {
        int n = stageNames.Length;
        var weights = new double[n];
        double weightSum = 0.0;

        for (int i = 0; i < n; i++)
        {
            double w = stageWeights?.GetValueOrDefault(stageNames[i], 0.0)
                ?? DefaultStagePriors.GetValueOrDefault(stageNames[i], 0.0);
            if (w <= 0) w = 1.0 / n;
            w *= 0.5 + (double)i / (n - 1);
            weights[i] = w;
            weightSum += w;
        }

        var steps = new List<PerStepReward>(n);
        double totalReward = 0.0;
        double totalSurrogate = 0.0;

        for (int i = 0; i < n; i++)
        {
            double contrib = weights[i] / Math.Max(weightSum, 1e-10);
            double raw = totalOutcome * contrib;
            double surrogate = stageContexts[i].Count > 0
                ? _surrogate.Predict(stageContexts[i])
                : 0.5;

            steps.Add(new PerStepReward
            {
                StepIndex = i,
                StepName = stageNames[i],
                RawReward = raw,
                SurrogateEstimate = surrogate,
                ContributionWeight = contrib,
                StepContext = stageContexts[i],
                RewardType = rewardType
            });

            totalReward += raw;
            totalSurrogate += surrogate;
        }

        var tr = new TrajectoryReward
        {
            TrajectoryId = trajectoryId,
            Steps = steps,
            TotalReward = totalReward,
            TotalSurrogate = totalSurrogate,
            RewardTypesUsed = [rewardType],
            TrajectoryLength = n,
            Timestamp = DateTime.UtcNow
        };

        lock (_trajectoryHistory)
        {
            _trajectoryHistory.Add(tr);
            while (_trajectoryHistory.Count > MaxHistory)
                _trajectoryHistory.RemoveAt(0);
        }

        return tr;
    }

    public double TrainSurrogate(IReadOnlyList<TrajectoryReward>? trajectories = null)
    {
        var items = new List<(Dictionary<string, double> Features, double Target)>();

        var source = trajectories ?? _trajectoryHistory;
        foreach (var tr in source)
        {
            foreach (var step in tr.Steps)
            {
                if (step.StepContext.Count > 0)
                    items.Add((step.StepContext, step.RawReward));
            }
        }

        if (items.Count == 0) return 0.0;
        return _surrogate.TrainBatch(items);
    }

    public TDMOptimizationResult OptimizePolicy(
        Dictionary<string, double> currentConfig,
        IReadOnlyList<Dictionary<string, double>> candidateConfigs,
        Func<Dictionary<string, double>, Dictionary<string, double>> configToFeatures)
    {
        var candidates = candidateConfigs.ToList();
        if (candidates.Count == 0)
            candidates.Add(currentConfig);

        var scores = new double[candidates.Count];
        for (int i = 0; i < candidates.Count; i++)
            scores[i] = _surrogate.Predict(configToFeatures(candidates[i]));

        int bestIdx = 0;
        for (int i = 1; i < scores.Length; i++)
            if (scores[i] > scores[bestIdx]) bestIdx = i;

        double mean = scores.Average();
        double gradNorm = 0.0;
        for (int i = 0; i < scores.Length; i++)
            gradNorm += (scores[i] - mean) * (scores[i] - mean);
        gradNorm = Math.Sqrt(gradNorm / scores.Length);

        double avgHistory = 0.5;
        if (_trajectoryHistory.Count > 0)
            avgHistory = _trajectoryHistory.Average(t => t.TotalReward);
        double improvement = scores[bestIdx] - avgHistory;

        double convergence = gradNorm < 0.01 ? 1.0 : Math.Max(0.0, 1.0 - gradNorm * 10.0);

        return new TDMOptimizationResult
        {
            RoundId = Environment.TickCount,
            SurrogateLoss = scores[bestIdx],
            PolicyGradientNorm = gradNorm,
            RewardImprovement = improvement,
            BestConfig = candidates[bestIdx],
            ConvergenceScore = convergence,
            PerStageContributions = new Dictionary<string, double>()
        };
    }

    public double AggregateRewards(
        IReadOnlyDictionary<RewardType, double> rewards,
        IReadOnlyDictionary<RewardType, double>? weights = null)
    {
        var w = weights ?? new Dictionary<RewardType, double>
        {
            [RewardType.BinarySuccess] = 0.30,
            [RewardType.QualityScore] = 0.25,
            [RewardType.HumanFeedback] = 0.15,
            [RewardType.BudgetCompliance] = 0.10,
            [RewardType.Latency] = 0.08,
            [RewardType.FormatValidity] = 0.07,
            [RewardType.SafetyCheck] = 0.05,
        };

        double total = 0.0;
        foreach (var (type, value) in rewards)
        {
            w.TryGetValue(type, out double weight);
            total += value * weight;
        }
        return total;
    }

    public Dictionary<string, double> SurrogateAnnealing(
        Dictionary<string, double> currentConfig,
        IReadOnlyList<Dictionary<string, double>> candidates,
        Func<Dictionary<string, double>, Dictionary<string, double>> configToFeatures,
        double temperature = 1.0, int maxSteps = 100)
    {
        var best = currentConfig;
        double bestScore = _surrogate.Predict(configToFeatures(currentConfig));
        int stagnation = 0;

        for (int step = 1; step <= maxSteps; step++)
        {
            double t = temperature / Math.Log(Math.E + step);

            foreach (var candidate in candidates)
            {
                double score = _surrogate.Predict(configToFeatures(candidate));

                if (score > bestScore)
                {
                    best = candidate;
                    bestScore = score;
                    stagnation = 0;
                }
                else if (t > 0.001)
                {
                    double dE = bestScore - score;
                    if (_rng.NextDouble() < Math.Exp(-dE / t))
                    {
                        best = candidate;
                        bestScore = score;
                        stagnation = 0;
                    }
                }
            }

            stagnation++;

            if (stagnation > 15 && t > 0.01)
            {
                int idx = _rng.Next(candidates.Count);
                double score = _surrogate.Predict(configToFeatures(candidates.ElementAt(idx)));
                double dE = bestScore - score;
                if (_rng.NextDouble() < Math.Exp(-dE / (t * 0.5)))
                {
                    best = candidates.ElementAt(idx);
                    bestScore = score;
                }
                stagnation = 0;
            }
        }

        return best;
    }

    private static Lazy<TDMRewardOptimizer> _instance = new(() => new TDMRewardOptimizer());
    public static TDMRewardOptimizer Instance => _instance.Value;
}

public sealed class GRPOOptimizer
{
    public LatentGRPO Latent { get; }
    public SpatialGRPOOptimizer Spatial { get; }
    public TDMRewardOptimizer Tdm { get; }

    public GRPOOptimizer(int latentDim = 6)
    {
        Latent = new LatentGRPO(latentDim);
        Spatial = new SpatialGRPOOptimizer();
        Tdm = new TDMRewardOptimizer();
    }

    private static Lazy<GRPOOptimizer> _instance = new(() => new GRPOOptimizer());
    public static GRPOOptimizer Instance => _instance.Value;

    public static LatentGRPO GetLatentGrpo(int latentDim = 6) => new LatentGRPO(latentDim);
    public static SpatialGRPOOptimizer GetSgrpo() => SpatialGRPOOptimizer.Instance;
    public static TDMRewardOptimizer GetTdmOptimizer() => TDMRewardOptimizer.Instance;
}
