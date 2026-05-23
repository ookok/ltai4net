using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace LTAI.AI.Governors;

public record SpeculativeDecoderConfig
{
    public int DraftSteps { get; init; } = 6;
    public float AcceptanceTemperature { get; init; } = 0.0f;
    public bool UseGreedyDraft { get; init; } = true;
    public int MaxBatchTokens { get; init; } = 512;
    public int MinAcceptanceRate { get; init; } = 40; // fall back to normal if <40%
    public string? DraftModelPath { get; init; }
    public string? DraftTokenizerPath { get; init; }
}

public record SpeculativeStats
{
    public int TotalDraftTokens { get; set; }
    public int AcceptedTokens { get; set; }
    public int RejectedTokens { get; set; }
    public double AcceptanceRate => TotalDraftTokens > 0 ? (double)AcceptedTokens / TotalDraftTokens : 0;
    public int TotalForwardPasses { get; set; }
    public double EffectiveSpeedup => TotalDraftTokens > 0 ? (double)(AcceptedTokens + TotalForwardPasses) / TotalForwardPasses : 1.0;
    public int TimesFellBack { get; set; }
}

/// Speculative Decoding: draft model generates k candidate tokens,
/// target model verifies them all in one forward pass.
/// Typical acceptance rate: 70-85%, giving 2-3x effective speedup.
public sealed class SpeculativeDecoder : IDisposable
{
    private readonly ILogger<SpeculativeDecoder> _logger;
    private readonly SpeculativeDecoderConfig _config;
    private readonly SpeculativeStats _stats = new();

    private InferenceSession? _draftSession;
    private SimpleTokenizer? _draftTokenizer;
    private bool _draftReady;

    private readonly float[]? _draftProbs;
    private readonly int[]? _draftTokens;

    public SpeculativeStats Stats => _stats;
    public bool HasDraft => _draftReady;

    public SpeculativeDecoder(
        SpeculativeDecoderConfig? config = null,
        ILogger<SpeculativeDecoder>? logger = null)
    {
        _config = config ?? new SpeculativeDecoderConfig();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SpeculativeDecoder>.Instance;
        _draftProbs = new float[_config.MaxBatchTokens];
        _draftTokens = new int[_config.MaxBatchTokens];
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_config.DraftModelPath is null || !global::System.IO.File.Exists(_config.DraftModelPath))
        {
            _logger.LogInformation("No draft model configured, speculative decoding disabled");
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                _draftSession = new InferenceSession(_config.DraftModelPath, new SessionOptions
                {
                    EnableCpuMemArena = true,
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    IntraOpNumThreads = 1
                });
            }, ct);

            if (_config.DraftTokenizerPath is not null && global::System.IO.File.Exists(_config.DraftTokenizerPath))
                _draftTokenizer = new SimpleTokenizer(_config.DraftTokenizerPath);
            else
                _draftTokenizer = new SimpleTokenizer();

            _draftReady = true;
            _logger.LogInformation("SpeculativeDecoder initialized with draft model: {Path}",
                _config.DraftModelPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load draft model, speculative decoding disabled");
        }
    }

    /// Generate tokens using speculative decoding.
    /// Falls back to normal autoregressive if draft unavailable or acceptance rate drops.
    public async IAsyncEnumerable<int[]> GenerateSpeculativeAsync(
        List<int> inputIds, List<int> attentionMask,
        InferenceSession targetSession, SimpleTokenizer tokenizer,
        int maxNewTokens = 256,
        float temperature = 0.7f,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_draftReady || _stats.TimesFellBack > 10)
        {
            await foreach (var chunk in GenerateAutoregressiveAsync(
                inputIds, attentionMask, targetSession, tokenizer, maxNewTokens, temperature, ct))
                yield return chunk;
            yield break;
        }

        var generated = new List<int>(inputIds);
        var mask = new List<int>(attentionMask);

        while (generated.Count - inputIds.Count < maxNewTokens)
        {
            ct.ThrowIfCancellationRequested();

            // Step 1: Draft model generates k candidate tokens
            var (draftTokens, draftCount) = DraftTokens(generated, mask, inputIds.Count);

            if (draftCount == 0 || _stats.AcceptanceRate < _config.MinAcceptanceRate / 100.0
                && _stats.TotalDraftTokens > 50)
            {
                _stats.TimesFellBack++;
                yield return GenerateOneTarget(generated, mask, targetSession, temperature);
                continue;
            }

            // Step 2: Append draft tokens to context for target verification
            var extendedIds = new List<int>(generated) { Capacity = generated.Count + draftCount };
            for (int i = 0; i < draftCount; i++) extendedIds.Add(draftTokens[i]);
            var extendedMask = Enumerable.Repeat(1, extendedIds.Count).ToList();

            // Step 3: Single target forward pass with all draft tokens
            var targetLogits = RunTargetForward(targetSession, extendedIds, extendedMask, draftCount);
            if (targetLogits is null)
            {
                _stats.TimesFellBack++;
                yield return GenerateOneTarget(generated, mask, targetSession, temperature);
                continue;
            }

            _stats.TotalForwardPasses++;

            // Step 4: Verify each draft token against target logits
            int accepted = 0;
            int sampled = -1;
            for (int i = 0; i < draftCount && generated.Count - inputIds.Count < maxNewTokens; i++)
            {
                var logits = targetLogits[i];
                var draftToken = draftTokens[i];

                // Greedy verification: accept if draft token == target's top choice
                var targetTop = ArgMax(logits);
                if (draftToken == targetTop || _config.AcceptanceTemperature <= 0)
                {
                    generated.Add(draftToken); mask.Add(1);
                    accepted++;
                    _stats.AcceptedTokens++;
                }
                else
                {
                    // Rejection: sample from target's distribution
                    sampled = SampleToken(logits, temperature);
                    generated.Add(sampled); mask.Add(1);
                    _stats.RejectedTokens++;
                    break;
                }
            }

            // If all draft tokens were accepted, sample one extra from target's last logits
            if (accepted == draftCount && targetLogits.Length > draftCount)
            {
                var bonusToken = SampleToken(targetLogits[draftCount], temperature);
                generated.Add(bonusToken); mask.Add(1);
            }

            _stats.TotalDraftTokens += draftCount;
            yield return generated.Skip(generated.Count - draftCount - 1).ToArray();
        }
    }

    private (int[] tokens, int count) DraftTokens(List<int> generated, List<int> mask, int baseLen)
    {
        var draftCount = Math.Min(_config.DraftSteps, _config.MaxBatchTokens - (generated.Count - baseLen));
        if (draftCount <= 0) return (Array.Empty<int>(), 0);

        // Greedy draft generation
        var draftIds = new List<int>(generated);
        var draftMask = Enumerable.Repeat(1, draftIds.Count).ToList();

        for (int i = 0; i < draftCount; i++)
        {
            RunDraftForward(draftIds, draftMask, out var token, out var prob);
            if (token < 0) break;
            _draftTokens![i] = token;
            _draftProbs![i] = prob;
            draftIds.Add(token);
            draftMask.Add(1);
        }

        return (_draftTokens!, draftCount);
    }

    private void RunDraftForward(List<int> ids, List<int> mask, out int token, out float prob)
    {
        token = -1; prob = 0;

        try
        {
            var inputTensor = new DenseTensor<int>(ids.ToArray(), [1, ids.Count]);
            var maskTensor = new DenseTensor<int>(mask.ToArray(), [1, mask.Count]);
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", inputTensor),
                NamedOnnxValue.CreateFromTensor("attention_mask", maskTensor)
            };
            using var results = _draftSession!.Run(inputs);
            var logits = results.First().AsTensor<float>();
            var vocabSize = (int)logits.Dimensions[2];
            var lastLogits = new float[vocabSize];

            var offset = (ids.Count - 1) * vocabSize;
            for (int v = 0; v < vocabSize; v++)
                lastLogits[v] = logits.GetValue(offset + v);

            token = ArgMax(lastLogits);
            prob = lastLogits[token];
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Draft forward failed");
        }
    }

    private float[][]? RunTargetForward(InferenceSession session, List<int> ids, List<int> mask, int draftCount)
    {
        try
        {
            var inputTensor = new DenseTensor<int>(ids.ToArray(), [1, ids.Count]);
            var maskTensor = new DenseTensor<int>(mask.ToArray(), [1, mask.Count]);
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", inputTensor),
                NamedOnnxValue.CreateFromTensor("attention_mask", maskTensor)
            };
            using var results = session.Run(inputs);
            var logits = results.First().AsTensor<float>();
            var vocabSize = (int)logits.Dimensions[2];

            // Extract logits for each of the last draftCount+1 positions
            var logitRows = new float[draftCount + 1][];
            var seqLen = ids.Count;
            for (int i = 0; i < draftCount + 1; i++)
            {
                var pos = seqLen - draftCount - 1 + i;
                if (pos < 0 || pos >= seqLen) continue;
                var row = new float[vocabSize];
                var baseOffset = pos * vocabSize;
                for (int v = 0; v < vocabSize; v++)
                    row[v] = logits.GetValue(baseOffset + v);
                logitRows[i] = row;
            }

            return logitRows;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Target forward failed during speculative verification");
            return null;
        }
    }

    private static int[] GenerateOneTarget(List<int> generated, List<int> mask,
        InferenceSession session, float temperature)
    {
        var inputTensor = new DenseTensor<int>(generated.ToArray(), [1, generated.Count]);
        var maskTensor = new DenseTensor<int>(mask.ToArray(), [1, mask.Count]);
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", maskTensor)
        };
        using var results = session.Run(inputs);
        var logits = results.First().AsTensor<float>();
        var vocabSize = (int)logits.Dimensions[2];

        var lastLogits = new float[vocabSize];
        var offset = (generated.Count - 1) * vocabSize;
        for (int v = 0; v < vocabSize; v++)
            lastLogits[v] = logits.GetValue(offset + v);

        var token = SampleToken(lastLogits, temperature);
        generated.Add(token);
        mask.Add(1);
        return new[] { token };
    }

    private static int ArgMax(float[] values)
    {
        int best = 0;
        for (int i = 1; i < values.Length; i++)
            if (values[i] > values[best]) best = i;
        return best;
    }

    private static int SampleToken(float[] logits, float temperature)
    {
        if (temperature <= 1e-6f) return ArgMax(logits);

        var maxLogit = logits.Max();
        var sum = 0f;
        var probs = new float[logits.Length];
        for (int i = 0; i < logits.Length; i++)
        {
            probs[i] = MathF.Exp((logits[i] - maxLogit) / temperature);
            sum += probs[i];
        }

        var r = Random.Shared.NextSingle() * sum;
        float cum = 0;
        for (int i = 0; i < probs.Length; i++)
        {
            cum += probs[i];
            if (r <= cum) return i;
        }
        return ArgMax(logits);
    }

    private static async IAsyncEnumerable<int[]> GenerateAutoregressiveAsync(
        List<int> inputIds, List<int> attentionMask,
        InferenceSession session, SimpleTokenizer tokenizer,
        int maxTokens, float temperature,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var generated = new List<int>(inputIds);
        var mask = new List<int>(attentionMask);

        for (int i = 0; i < maxTokens; i++)
        {
            ct.ThrowIfCancellationRequested();
            yield return GenerateOneTarget(generated, mask, session, temperature);
        }
    }

    public void Dispose()
    {
        _draftSession?.Dispose();
        _draftReady = false;
    }
}
