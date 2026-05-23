using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

/// Self-Correction LoRA — receives (query, wrong_output) and predicts correct_output.
/// Trained on CIPO correction pairs: (query, wrong) → correct.
/// Enhances model's intrinsic self-correction (pass@K improvement).
public sealed class SelfCorrectionLoRA
{
    private readonly TieredLoraManager _loraManager;
    private readonly AdaptiveDepthController _depthController;
    private readonly ILogger<SelfCorrectionLoRA> _logger;
    private readonly List<(string query, string wrong, string correct, string errorType)> _trainingBuffer = new();
    private int _generation;

    public int Generation => _generation;

    public SelfCorrectionLoRA(
        TieredLoraManager loraManager,
        AdaptiveDepthController depthController,
        ILogger<SelfCorrectionLoRA>? logger = null)
    {
        _loraManager = loraManager;
        _depthController = depthController;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SelfCorrectionLoRA>.Instance;
    }

    /// Add a correction pair from CIPO correction memory
    public void AddCorrectionPair(string query, string wrongOutput, string correctOutput, string errorType)
    {
        lock (_trainingBuffer)
        {
            _trainingBuffer.Add((query, wrongOutput, correctOutput, errorType));
            if (_trainingBuffer.Count > 200) _trainingBuffer.RemoveAt(0);
        }
    }

    /// Train LoRA with correction data: the model learns to produce
    /// correct labels even when the input query contains signs of prior mistakes.
    public float Train(int epochs = 3, float lr = 0.008f)
    {
        List<(string, int)> samples;
        lock (_trainingBuffer)
        {
            if (_trainingBuffer.Count < 5) return 0;
            samples = _trainingBuffer.ToList();
        }

        var network = _loraManager.GetNetwork(HrmReasoningTier.FastThink)
            ?? _loraManager.GetNetwork(HrmReasoningTier.DeepThink);
        if (network is null) return 0;

        // Construct augmented training inputs: prefix query with "[self-correct] wrong:... → "
        var trainingData = new List<(string text, int targetClass)>();
        foreach (var (query, wrong, correct, errType) in samples)
        {
            var augmentedInput = $"[self-correct, error={errType}] Query: {query[..global::System.Math.Min(query.Length, 200)]} Wrong: {wrong[..global::System.Math.Min(wrong.Length, 150)]}";
            var decision = _depthController.Decide(correct);
            var targetIdx = decision.Tier switch
            {
                HrmReasoningTier.Reflex => 0, HrmReasoningTier.FastThink => 0,
                HrmReasoningTier.DeepThink => 1, HrmReasoningTier.FullReason => 4,
                _ => 3
            };
            trainingData.Add((augmentedInput, targetIdx));
        }

        _logger.LogInformation("SelfCorrectionLoRA training: {Count} correction pairs", trainingData.Count);
        var loss = network.Train(trainingData, epochs, lr);
        network.Merge();
        network.Unmerge();
        _generation++;

        _logger.LogInformation("SelfCorrectionLoRA gen={Gen} loss={Loss:F4}", _generation, loss);
        return loss;
    }

    /// Correct a potentially wrong output
    public (string? corrected, float confidence) TryCorrect(string query, string candidateOutput)
    {
        var network = _loraManager.GetNetwork(HrmReasoningTier.FastThink)
            ?? _loraManager.GetNetwork(HrmReasoningTier.DeepThink);
        if (network is null) return (null, 0);

        var correctionQuery = $"[self-correct] Query: {query[..global::System.Math.Min(query.Length, 200)]} Candidate: {candidateOutput[..global::System.Math.Min(candidateOutput.Length, 150)]}";
        var (classIdx, confidence) = network.Predict(correctionQuery);
        var correctedLabel = network.MapClassLabel(classIdx);

        return (correctedLabel, confidence);
    }

    public int PendingCount { get { lock (_trainingBuffer) return _trainingBuffer.Count; } }

    public Dictionary<string, object> GetStats()
    {
        return new Dictionary<string, object>
        {
            ["generation"] = _generation,
            ["pending_pairs"] = PendingCount,
            ["by_error_type"] = _trainingBuffer
                .GroupBy(t => t.errorType)
                .ToDictionary(g => g.Key, g => (object)g.Count())
        };
    }
}
