using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public record SpinSample
{
    public string Query { get; init; } = "";
    public string PlayerResponse { get; init; } = "";
    public string OpponentResponse { get; init; } = "";
    public float Reward { get; init; }
    public string Winner { get; init; } = ""; // "player", "opponent", "tie"
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

public record SpinEpochResult
{
    public int SamplesGenerated { get; init; }
    public int SamplesAccepted { get; init; }
    public float AvgReward { get; init; }
    public float PlayerWinRate { get; init; }
    public TimeSpan Duration { get; init; }
}

/// SPIN (Self-Play fIne-tuNing) — Model generates its own training data
/// by playing against itself. No human annotation needed.
/// Paper: Chen et al. 2024, "Self-Play Fine-Tuning Converts Weak Language
/// Models to Strong Language Models"
public sealed class SpinSelfPlayLoop
{
    private readonly TieredLoraManager _loraManager;
    private readonly AdaptiveDepthController _depthController;
    private readonly SynapticMemory? _synapticMemory;
    private readonly ILogger<SpinSelfPlayLoop> _logger;
    private readonly List<SpinSample> _playLog = new();
    private readonly int _maxLogSize;
    private SpinEpochResult? _lastResult;

    public SpinEpochResult? LastResult => _lastResult;

    public SpinSelfPlayLoop(
        TieredLoraManager loraManager,
        AdaptiveDepthController depthController,
        SynapticMemory? synapticMemory = null,
        ILogger<SpinSelfPlayLoop>? logger = null,
        int maxLogSize = 500)
    {
        _loraManager = loraManager;
        _depthController = depthController;
        _synapticMemory = synapticMemory;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SpinSelfPlayLoop>.Instance;
        _maxLogSize = maxLogSize;
    }

    /// Run one SPIN epoch: generate synthetic training pairs → score → retrain
    public async Task<SpinEpochResult> RunEpochAsync(
        int numRounds = 20,
        float acceptanceThreshold = 0.6f,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int generated = 0, accepted = 0;
        float totalReward = 0;
        int playerWins = 0, opponentWins = 0;

        var queries = GenerateQueries(numRounds);
        _logger.LogInformation("SPIN epoch start: {Rounds} rounds", queries.Count);

        foreach (var query in queries)
        {
            ct.ThrowIfCancellationRequested();
            generated++;

            try
            {
                // Player: L1 FastThink generates answer
                var (playerLabel, playerConf) = PredictWithTier(query, HrmReasoningTier.FastThink);

                // Opponent: L1 DeepThink generates answer (stronger reasoning)
                var (opponentLabel, opponentConf) = PredictWithTier(query, HrmReasoningTier.DeepThink);

                // Judge: DeepThink confidence vs FastThink confidence as reward signal
                // Higher-tier model with higher confidence = better answer → positive reward
                var reward = opponentConf * 0.6f + (1 - playerConf) * 0.4f;
                totalReward += reward;
                generated++;

                string winner = reward > 0.55f ? "player" : reward < 0.45f ? "opponent" : "tie";
                if (winner == "player") playerWins++;
                else if (winner == "opponent") opponentWins++;

                // Accept if reward crosses threshold → add to training
                if (reward >= acceptanceThreshold)
                {
                    var sample = new SpinSample
                    {
                        Query = query,
                        PlayerResponse = playerLabel,
                        OpponentResponse = opponentLabel,
                        Reward = reward, Winner = winner
                    };

                    lock (_playLog) { _playLog.Add(sample); TrimLog(); }

                    // Store as synaptic experience for training
                    _synapticMemory?.Store(new SynapticExperience
                    {
                        Id = LiteDB.ObjectId.NewObjectId(),
                        Type = SynapseType.Teaching,
                        Query = query,
                        Response = playerLabel,
                        Label = playerLabel,
                        Confidence = playerConf,
                        Reward = reward,
                        Metadata = $"spin_winner={winner}",
                        CreatedAt = DateTime.UtcNow
                    });

                    accepted++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SPIN round failed for query: {Query}",
                    query[..global::System.Math.Min(query.Length, 60)]);
            }
        }

        _lastResult = new SpinEpochResult
        {
            SamplesGenerated = generated,
            SamplesAccepted = accepted,
            AvgReward = generated > 0 ? totalReward / generated : 0,
            PlayerWinRate = generated > 0 ? (float)playerWins / generated : 0,
            Duration = sw.Elapsed
        };

        _logger.LogInformation(
            "SPIN epoch complete: generated={Gen} accepted={Acc} avgReward={Reward:F3} playerWR={WR:F2}",
            generated, accepted, _lastResult.AvgReward, _lastResult.PlayerWinRate);

        return _lastResult;
    }

    private (string label, float confidence) PredictWithTier(string query, HrmReasoningTier tier)
    {
        var network = _loraManager.GetNetwork(tier);
        if (network is null)
        {
            network = _loraManager.GetNetwork(HrmReasoningTier.FastThink);
            if (network is null) return ("chat", 0.5f);
        }

        var (classIdx, confidence) = network.Predict(query);
        return (network.MapClassLabel(classIdx), confidence);
    }

    private List<string> GenerateQueries(int count)
    {
        var templates = new[]
        {
            "How to {0} in {1}?", "Explain the concept of {0}.",
            "What is the difference between {0} and {1}?",
            "Write a function to {0}.", "Debug this: {0}.",
            "优化以下代码: {0}", "分析 {0} 的性能瓶颈",
            "设计一个 {0} 的架构方案", "比较 {0} 和 {1} 的优缺点"
        };
        var topics = new[] { "REST API", "database", "caching", "authentication",
            "并发", "索引", "微服务", "容器化", "机器学习", "数据流" };

        var queries = new List<string>();
        var rng = Random.Shared;
        for (int i = 0; i < count; i++)
        {
            var tmpl = templates[rng.Next(templates.Length)];
            var t1 = topics[rng.Next(topics.Length)];
            var t2 = topics[rng.Next(topics.Length)];
            var q = string.Format(tmpl, t1, t2);

            // Vary complexity
            if (rng.Next(3) == 0)
                q += " 请详细解释并提供代码示例。";
            if (rng.Next(4) == 0)
                q += " 请分析优缺点并给出建议。";

            queries.Add(q);
        }

        return queries;
    }

    public Dictionary<string, object> GetStats()
    {
        var samples = _playLog.ToList();
        return new Dictionary<string, object>
        {
            ["total_rounds"] = samples.Count,
            ["avg_reward"] = samples.Count > 0 ? samples.Average(s => s.Reward) : 0,
            ["player_wins"] = samples.Count(s => s.Winner == "player"),
            ["opponent_wins"] = samples.Count(s => s.Winner == "opponent"),
            ["ties"] = samples.Count(s => s.Winner == "tie"),
            ["last_epoch"] = _lastResult?.SamplesAccepted ?? 0
        };
    }

    private void TrimLog()
    {
        while (_playLog.Count > _maxLogSize)
            _playLog.RemoveAt(0);
    }
}
