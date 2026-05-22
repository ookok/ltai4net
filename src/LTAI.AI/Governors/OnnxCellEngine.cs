using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace LTAI.AI.Governors;

public sealed record OnnxModelConfig
{
    public string Domain { get; init; } = "";
    public string ModelPath { get; init; } = "";
    public string TokenizerPath { get; init; } = "";
    public string[] Labels { get; init; } = Array.Empty<string>();
    public int MaxSequenceLength { get; init; } = 128;
    public float MinConfidence { get; init; } = 0.5f;
    public bool IsQuantized { get; init; }
    public long SizeBytes { get; init; }
    public string Source { get; init; } = "";
    public string Description { get; init; } = "";
}

public interface ICellEngine
{
    bool IsReady { get; }
    string Domain { get; }
    InferenceResult Predict(string text);
    void Dispose();
}

public sealed class OnnxCellEngine : ICellEngine
{
    private readonly OnnxModelConfig _config;
    private readonly ILogger<OnnxCellEngine> _logger;
    private InferenceSession? _session;
    private readonly object _lock = new();
    private volatile bool _isReady;

    public OnnxCellEngine(OnnxModelConfig config, ILogger<OnnxCellEngine>? logger = null)
    {
        _config = config;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<OnnxCellEngine>.Instance;
    }

    public async Task<bool> LoadAsync(CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(_config.ModelPath))
            {
                _logger.LogWarning("ONNX model not found: {Path}", _config.ModelPath);
                return false;
            }

            await Task.Run(() =>
            {
                lock (_lock)
                {
                    var sessionOptions = new SessionOptions
                    {
                        EnableCpuMemArena = true,
                        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
                    };

                    _session = new InferenceSession(_config.ModelPath, sessionOptions);
                    _isReady = true;
                }
            }, ct);

            var fileInfo = new FileInfo(_config.ModelPath);
            _logger.LogInformation(
                "ONNX cell engine loaded: domain={Domain} model={Model} size={SizeKB:F1}KB labels={Labels}",
                _config.Domain, Path.GetFileName(_config.ModelPath),
                fileInfo.Length / 1024.0, string.Join(", ", _config.Labels));

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load ONNX model: {Domain}", _config.Domain);
            return false;
        }
    }

    public InferenceResult Predict(string text)
    {
        if (!_isReady || _session == null)
        {
            return new InferenceResult
            {
                PredictedLabel = "unknown",
                Confidence = 0.0f,
                ModelType = "onnx_unavailable"
            };
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var inputIds = TokenizeText(text);
            var attentionMask = new int[inputIds.Length];
            Array.Fill(attentionMask, 1);

            var inputTensorX = new DenseTensor<int>(inputIds, new[] { 1, inputIds.Length });
            var inputTensorA = new DenseTensor<int>(attentionMask, new[] { 1, attentionMask.Length });

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", inputTensorX),
                NamedOnnxValue.CreateFromTensor("attention_mask", inputTensorA)
            };

            using var results = _session.Run(inputs);
            var output = results.First();

            var logits = output.AsTensor<float>().ToArray();
            var (label, confidence) = ExtractPrediction(logits);

            stopwatch.Stop();

            return new InferenceResult
            {
                PredictedLabel = label,
                Confidence = confidence,
                LatencyMs = (float)stopwatch.Elapsed.TotalMilliseconds,
                ModelType = "onnx"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ONNX prediction failed: {Domain}", _config.Domain);
            return new InferenceResult
            {
                PredictedLabel = "error",
                Confidence = 0.0f,
                ModelType = "onnx_error"
            };
        }
    }

    private (string Label, float Confidence) ExtractPrediction(float[] logits)
    {
        if (logits == null || logits.Length == 0)
            return ("unknown", 0.0f);

        var maxIndex = 0;
        var maxScore = float.MinValue;

        for (var i = 0; i < logits.Length; i++)
        {
            if (logits[i] > maxScore)
            {
                maxScore = logits[i];
                maxIndex = i;
            }
        }

        var confidence = Softmax(maxScore, logits);
        var label = maxIndex < _config.Labels.Length
            ? _config.Labels[maxIndex]
            : $"label_{maxIndex}";

        return (label, confidence);
    }

    private static float Softmax(float targetScore, float[] scores)
    {
        var max = scores.Max();
        var expSum = scores.Sum(s => MathF.Exp(s - max));
        var targetExp = MathF.Exp(targetScore - max);
        return targetExp / expSum;
    }

    private int[] TokenizeText(string text)
    {
        var tokens = new List<int> { 101 }; // [CLS] token

        var words = text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var word in words.Take(_config.MaxSequenceLength - 2))
        {
            tokens.AddRange(WordToTokenIds(word));
        }

        tokens.Add(102); // [SEP] token

        var result = new int[_config.MaxSequenceLength];
        Array.Copy(tokens.ToArray(), result, Math.Min(tokens.Count, _config.MaxSequenceLength));

        return result;
    }

    private static IEnumerable<int> WordToTokenIds(string word)
    {
        var ids = new List<int>();
        foreach (var c in word)
        {
            ids.Add((int)c + 1000);
        }
        return ids;
    }

    public bool IsReady => _isReady;
    public OnnxModelConfig Config => _config;
    public string Domain => _config.Domain;

    public void Dispose()
    {
        _session?.Dispose();
    }
}
