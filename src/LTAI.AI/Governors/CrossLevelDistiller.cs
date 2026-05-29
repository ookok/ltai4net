using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public record DistillationSample
{
    public string Query { get; init; } = "";
    public float Complexity { get; init; }
    public HrmReasoningTier SourceTier { get; init; }
    public HrmReasoningTier TargetTier { get; init; }
    public string TeacherResponse { get; init; } = "";
    public string StudentPrediction { get; init; } = "";
    public float KLDistance { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

public record CrossLevelDistillResult
{
    public HrmReasoningTier SourceTier { get; init; }
    public HrmReasoningTier TargetTier { get; init; }
    public int SamplesUsed { get; init; }
    public float AvgKLDistance { get; init; }
    public float StudentAccuracyBefore { get; init; }
    public float StudentAccuracyAfter { get; init; }
    public bool Success { get; init; }
}

/// Cross-level knowledge distillation for HRM.
/// Two directions:
///   L2 → L1: Deep model (teacher) distills reasoning into L1 LoRA (student)
///   L1 → L2: Fast LoRA judgment enriches L2 prompt with tier context
public sealed class CrossLevelDistiller
{
    private readonly TieredLoraManager _loraManager;
    private readonly AdaptiveDepthController _depthController;
    private readonly ILogger<CrossLevelDistiller> _logger;
    private readonly List<DistillationSample> _distillLog = new();
    private readonly int _maxLogSize;

    public CrossLevelDistiller(
        TieredLoraManager loraManager,
        AdaptiveDepthController depthController,
        ILogger<CrossLevelDistiller>? logger = null,
        int maxLogSize = 1000)
    {
        _loraManager = loraManager;
        _depthController = depthController;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CrossLevelDistiller>.Instance;
        _maxLogSize = maxLogSize;
    }

    /// L2 → L1 distillation: distill L2 reasoning into L1 LoRA network.
    /// Teacher: L2 IChatClient response. Student: L1 LoRA network.
    public async Task<CrossLevelDistillResult> DistillL2ToL1Async(
        IChatClient l2Client,
        List<TrainingSample> samples,
        HrmReasoningTier targetTier = HrmReasoningTier.Fast,
        CancellationToken ct = default)
    {
        var studentNetwork = _loraManager.GetNetwork(targetTier);
        if (studentNetwork is null)
        {
            return new CrossLevelDistillResult
            {
                SourceTier = HrmReasoningTier.Deep,
                TargetTier = targetTier,
                Success = false
            };
        }

        // Baseline accuracy before distillation
        var baselineAcc = EvaluateAccuracy(studentNetwork, samples);
        _logger.LogInformation("L2→L1 distill: target={Tier}, baseline_acc={Acc:F3}, samples={N}",
            targetTier, baselineAcc, samples.Count);

        var distillationData = new List<(string text, int targetClass)>();
        float totalKL = 0;

        foreach (var sample in samples.Take(200)) // batch limit
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                // Get teacher (L2) response
                var teacherResponse = await l2Client.GetResponseAsync(
                    new ChatMessage(ChatRole.User, sample.Text),
                    cancellationToken: ct).ConfigureAwait(false);
                var teacherText = teacherResponse.Text ?? "";

                // Get student (L1) prediction before training
                var (predIdx, predConf) = studentNetwork.Predict(sample.Text);
                var studentLabel = _loraManager.GetNetwork(targetTier)
                    ?.MapClassLabel(predIdx) ?? "unknown";

                // Compute KL-style distillation: teacher distribution → student target
                // Simplified: use teacher's response to derive target class via depth controller
                var teacherDecision = _depthController.Decide(teacherText);
                var targetIdx = MapTierToLabelIndex(teacherDecision.Tier);

                distillationData.Add((sample.Text, targetIdx));

                // Log KL approximation (simple: |teacher_complexity - student_confidence|)
                var kl = MathF.Abs(teacherDecision.Complexity - predConf);
                totalKL += kl;

                lock (_distillLog)
                {
                    _distillLog.Add(new DistillationSample
                    {
                        Query = sample.Text[..Math.Min(sample.Text.Length, 100)],
                        Complexity = teacherDecision.Complexity,
                        SourceTier = HrmReasoningTier.Deep,
                        TargetTier = targetTier,
                        TeacherResponse = teacherText[..Math.Min(teacherText.Length, 200)],
                        StudentPrediction = studentLabel,
                        KLDistance = kl
                    });
                    TrimLog();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Distill sample failed: {Query}", sample.Text[..Math.Min(sample.Text.Length, 50)]);
            }
        }

        if (distillationData.Count < 5)
        {
            return new CrossLevelDistillResult
            {
                SourceTier = HrmReasoningTier.Deep,
                TargetTier = targetTier, SamplesUsed = distillationData.Count, Success = false
            };
        }

        // Train LoRA with distilled targets
        var avgKL = distillationData.Count > 0 ? totalKL / distillationData.Count : 0;
        var preTrainAcc = EvaluateAccuracy(studentNetwork, samples);

        var tieredSamples = new Dictionary<HrmReasoningTier, List<TrainingSample>>
        {
            [targetTier] = distillationData.Select(d =>
                new TrainingSample { Text = d.text, Label = MapIndexToLabel(d.targetClass), Weight = 1.0f })
                .ToList()
        };
        await _loraManager.TrainAllTiersAsync(tieredSamples, ct).ConfigureAwait(false);

        var postTrainAcc = EvaluateAccuracy(studentNetwork, samples);

        _logger.LogInformation(
            "L2→L1 distill complete: tier={Tier} acc={Pre:F3}→{Post:F3} kl={KL:F4} samples={N}",
            targetTier, preTrainAcc, postTrainAcc, avgKL, distillationData.Count);

        return new CrossLevelDistillResult
        {
            SourceTier = HrmReasoningTier.Deep,
            TargetTier = targetTier,
            SamplesUsed = distillationData.Count,
            AvgKLDistance = avgKL,
            StudentAccuracyBefore = preTrainAcc,
            StudentAccuracyAfter = postTrainAcc,
            Success = postTrainAcc >= preTrainAcc * 0.95f // tolerate 5% regression
        };
    }

    /// L1 → L2 enhancement: enrich L2 prompt with tier context from L1 fast judgment.
    public async Task<string> EnhancePromptForL2Async(
        string originalQuery,
        IChatClient l2Client,
        CancellationToken ct = default)
    {
        var decision = _depthController.Decide(originalQuery);
        var fastNetwork = _loraManager.GetNetwork(HrmReasoningTier.Fast);
        var deepNetwork = _loraManager.GetNetwork(HrmReasoningTier.Fast);

        var contextParts = new List<string>
        {
            $"[HRM Tier Context] Original query complexity: {decision.Complexity:F2}",
            $"Recommended tier: {decision.Tier}, Pattern: {decision.Pattern}"
        };

        // Add L1 FastThink prediction as context hint
        if (fastNetwork is not null)
        {
            try
            {
                var (idx, conf) = fastNetwork.Predict(originalQuery);
                contextParts.Add($"L1-Fast: intent={_loraManager.GetNetwork(HrmReasoningTier.Fast)
                    ?.MapClassLabel(idx) ?? "unknown"} (conf={conf:F2})");
            }
            catch { }
        }

        // Add L1 DeepThink prediction if available
        if (deepNetwork is not null && decision.Complexity > 0.3f)
        {
            try
            {
                var (idx, conf) = deepNetwork.Predict(originalQuery);
                contextParts.Add($"L1-Deep: intent={_loraManager.GetNetwork(HrmReasoningTier.Fast)
                    ?.MapClassLabel(idx) ?? "unknown"} (conf={conf:F2})");
            }
            catch { }
        }

        // Add difficulty hints
        if (decision.IsHard)
            contextParts.Add("NOTE: Query classified as HARD — consider chain-of-thought and verification.");
        if (decision.Pattern == CollaborationPattern.Sequential)
            contextParts.Add("SUGGEST: Sequential pattern (plan → execute → verify).");
        else if (decision.Pattern == CollaborationPattern.Mixture)
            contextParts.Add("SUGGEST: Multi-angle analysis recommended for this domain-spanning query.");

        var enhancedPrompt = string.Join("\n", contextParts) + $"\n\n--- Original Query ---\n{originalQuery}";

        // If L2 needs to reason, return enhanced; otherwise just pass through
        if (decision.Tier == HrmReasoningTier.Fast)
        {
            _logger.LogDebug("L1→L2: Tier={Tier} does not need L2, skipping prompt enhancement", decision.Tier);
            return originalQuery;
        }

        _logger.LogDebug("L1→L2: Enhanced prompt for L2 (tier={Tier}, complexity={Complexity:F2})",
            decision.Tier, decision.Complexity);

        return enhancedPrompt;
    }

    /// Get distillation stats
    public Dictionary<string, object> GetStats()
    {
        var samples = _distillLog.ToList();
        return new Dictionary<string, object>
        {
            ["total_distilled"] = samples.Count,
            ["avg_kl_distance"] = samples.Count > 0 ? samples.Average(s => s.KLDistance) : 0,
            ["by_source_tier"] = samples.GroupBy(s => s.SourceTier)
                .ToDictionary(g => g.Key.ToString(), g => (object)g.Count()),
            ["by_target_tier"] = samples.GroupBy(s => s.TargetTier)
                .ToDictionary(g => g.Key.ToString(), g => (object)g.Count())
        };
    }

    public IReadOnlyList<DistillationSample> RecentSamples(int count = 20)
    {
        lock (_distillLog) return _distillLog.TakeLast(count).ToList();
    }

    private static float EvaluateAccuracy(IntentClassifierNetwork network, List<TrainingSample> samples)
    {
        if (samples.Count == 0) return 0;
        int correct = 0;
        foreach (var s in samples)
        {
            var (pred, _) = network.Predict(s.Text);
            var expectedIdx = MapLabelToIndex(s.Label);
            if (pred == expectedIdx) correct++;
        }
        return (float)correct / samples.Count;
    }

    private static int MapLabelToIndex(string label)
    {
        return label.ToLowerInvariant() switch
        {
            var l when l.Contains("fast") || l.Contains("reflex") => 0,
            var l when l.Contains("deep") || l.Contains("reason") => 1,
            var l when l.Contains("code") => 2,
            var l when l.Contains("chat") || l.Contains("general") => 3,
            _ => 3
        };
    }

    private static int MapTierToLabelIndex(HrmReasoningTier tier)
    {
        return tier switch
        {
            HrmReasoningTier.Fast => 0,
            HrmReasoningTier.Deep => 4,
            _ => 3
        };
    }

    private static string MapIndexToLabel(int idx)
    {
        return idx switch { 0 => "fast", 1 => "deep", 2 => "code", 3 => "chat", 4 => "deep", _ => "chat" };
    }

    private void TrimLog()
    {
        while (_distillLog.Count > _maxLogSize)
            _distillLog.RemoveAt(0);
    }
}
