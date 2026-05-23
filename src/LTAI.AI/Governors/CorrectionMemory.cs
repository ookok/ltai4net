using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed class CorrectionSample
{
    public string Query { get; init; } = "";
    public string WrongOutput { get; init; } = "";
    public string CorrectOutput { get; set; } = "";
    public string ErrorType { get; set; } = "";    // logic, factual, incomplete, syntax
    public float OriginalReward { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public bool UsedForTraining { get; set; }
}

public record CorrectionBatchResult
{
    public int CorrectionsGenerated { get; init; }
    public int TrainingSamplesCreated { get; init; }
    public float AvgCorrectionQuality { get; init; }
    public TimeSpan Duration { get; init; }
}

/// CIPO (Correction-Oriented Policy Optimization) — arXiv:2605.14539
/// Converts failed trajectories into correction-oriented supervision.
/// When a model produces wrong output, the LLM generates a correction,
/// creating (query, wrong, correct) triples for self-correction training.
public sealed class CorrectionMemory
{
    private readonly IChatClient _llm;
    private readonly SynapticMemory _synapticMemory;
    private readonly ILogger<CorrectionMemory> _logger;
    private readonly List<CorrectionSample> _buffer = new();
    private readonly object _lock = new();
    private readonly int _maxBufferSize;

    private const string CorrectionPrompt =
@"You are a correction expert. Given a query and a wrong answer, produce the CORRECT answer.
Analyze what went wrong (logic error? factual error? incomplete? syntax error?) and fix it.
Output format:
ERROR_TYPE: <logic|factual|incomplete|syntax>
CORRECTION: <the complete correct answer>

Query: {0}
Wrong Answer: {1}
Correct Answer:";

    public CorrectionMemory(
        IChatClient llm,
        SynapticMemory synapticMemory,
        ILogger<CorrectionMemory>? logger = null,
        int maxBufferSize = 500)
    {
        _llm = llm;
        _synapticMemory = synapticMemory;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CorrectionMemory>.Instance;
        _maxBufferSize = maxBufferSize;
    }

    /// Record a failed output for later correction
    public void RecordFailure(string query, string wrongOutput, float reward, string? errorType = null)
    {
        var sample = new CorrectionSample
        {
            Query = query, WrongOutput = wrongOutput,
            OriginalReward = reward,
            ErrorType = errorType ?? "unknown"
        };

        lock (_lock)
        {
            _buffer.Add(sample);
            while (_buffer.Count > _maxBufferSize) _buffer.RemoveAt(0);
        }
    }

    /// Generate corrections for buffered failures using LLM
    public async Task<CorrectionBatchResult> GenerateCorrectionsAsync(
        int maxSamples = 20, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int generated = 0, trained = 0;
        float totalQuality = 0;

        List<CorrectionSample> pending;
        lock (_lock) { pending = _buffer.Where(s => !s.UsedForTraining).Take(maxSamples).ToList(); }

        foreach (var sample in pending)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var prompt = string.Format(CorrectionPrompt,
                    sample.Query[..global::System.Math.Min(sample.Query.Length, 500)],
                    sample.WrongOutput[..global::System.Math.Min(sample.WrongOutput.Length, 800)]);

                var response = await _llm.GetResponseAsync(prompt,
                    new ChatOptions { Temperature = 0.2f, MaxOutputTokens = 1000 }, ct);
                var corrected = response.Text ?? "";

                if (string.IsNullOrWhiteSpace(corrected)) continue;

                var (errorType, correction) = ParseCorrection(corrected);

                if (!string.IsNullOrWhiteSpace(correction) && correction.Length > 10)
                {
                    sample.CorrectOutput = correction;
                    sample.ErrorType = errorType;
                    generated++;

                    // Store as synaptic experience with high reward (corrected answer)
                    _synapticMemory.Store(new SynapticExperience
                    {
                        Id = LiteDB.ObjectId.NewObjectId(),
                        Type = SynapseType.Correction,
                        Query = sample.Query,
                        Response = correction,
                        Label = errorType,
                        Confidence = 0.9f,
                        Reward = 0.8f,
                        Metadata = $"cipo_correction|original_reward={sample.OriginalReward:F2}",
                        CreatedAt = DateTime.UtcNow
                    });
                    trained++;

                    totalQuality += EstimateCorrectionQuality(sample.WrongOutput, correction);
                }

                sample.UsedForTraining = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Correction generation failed for query: {Q}",
                    sample.Query[..global::System.Math.Min(sample.Query.Length, 60)]);
            }
        }

        _logger.LogInformation(
            "CIPO corrections: generated={Gen} trained={Train} quality={Q:F2}",
            generated, trained, generated > 0 ? totalQuality / generated : 0);

        return new CorrectionBatchResult
        {
            CorrectionsGenerated = generated,
            TrainingSamplesCreated = trained,
            AvgCorrectionQuality = generated > 0 ? totalQuality / generated : 0,
            Duration = sw.Elapsed
        };
    }

    /// Get correction pairs for training: (query, wrong → correct)
    public List<(string query, string wrongOutput, string correctOutput, string errorType)> GetTrainingPairs(int maxCount = 50)
    {
        lock (_lock)
        {
            return _buffer
                .Where(s => !string.IsNullOrEmpty(s.CorrectOutput) && s.UsedForTraining)
                .OrderByDescending(s => s.OriginalReward)
                .Take(maxCount)
                .Select(s => (s.Query, s.WrongOutput, s.CorrectOutput, s.ErrorType))
                .ToList();
        }
    }

    public int PendingCount { get { lock (_lock) return _buffer.Count(s => !s.UsedForTraining); } }
    public int CorrectedCount { get { lock (_lock) return _buffer.Count(s => s.UsedForTraining); } }

    private static (string errorType, string correction) ParseCorrection(string raw)
    {
        var errorType = "logic";
        var correction = raw;

        var lines = raw.Split('\n');
        for (int i = 0; i < global::System.Math.Min(lines.Length, 3); i++)
        {
            if (lines[i].StartsWith("ERROR_TYPE:", StringComparison.OrdinalIgnoreCase))
                errorType = lines[i]["ERROR_TYPE:".Length..].Trim().ToLowerInvariant();
            if (lines[i].StartsWith("CORRECTION:", StringComparison.OrdinalIgnoreCase))
                correction = string.Join('\n', lines.Skip(i + 1)).Trim();
        }

        // If no CORRECTION marker found, use everything after ERROR_TYPE
        if (correction == raw && lines.Length > 1)
            correction = string.Join('\n', lines.Skip(1)).Trim();

        return (errorType, correction);
    }

    private static float EstimateCorrectionQuality(string wrong, string correct)
    {
        // Heuristic: correction should be substantially different from wrong output
        var wrongLen = wrong.Length;
        var correctLen = correct.Length;
        if (wrongLen == 0 || correctLen == 0) return 0.5f;

        // Length ratio (good correction often adds detail)
        var lenRatio = (float)correctLen / wrongLen;
        var lenScore = global::System.Math.Clamp(lenRatio, 0.3f, 2.0f) / 2.0f;

        // Content difference (correction should differ from wrong output)
        var commonChars = wrong.Intersect(correct).Count();
        var diffScore = 1.0f - (float)commonChars / global::System.Math.Max(wrongLen, 1);

        return (lenScore * 0.3f + diffScore * 0.7f);
    }

    public Dictionary<string, object> GetStats()
    {
        return new Dictionary<string, object>
        {
            ["pending"] = PendingCount,
            ["corrected"] = CorrectedCount,
            ["by_error_type"] = _buffer.Where(s => s.UsedForTraining)
                .GroupBy(s => s.ErrorType)
                .ToDictionary(g => g.Key, g => (object)g.Count())
        };
    }
}
