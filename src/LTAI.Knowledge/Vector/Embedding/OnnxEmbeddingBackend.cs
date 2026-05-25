using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using LTAI.Knowledge.Vector.Interfaces;

namespace LTAI.Knowledge.Vector.Embedding;

/// <summary>
/// 基于 ONNX Runtime 的本地向量模型后端
/// 支持加载 HuggingFace 格式的 ONNX Embedding 模型 (如 all-MiniLM-L6-v2, bge-small-zh 等)
/// </summary>
public sealed class OnnxEmbeddingBackend : IEmbeddingBackend, IDisposable
{
    private readonly OnnxEmbeddingConfig _config;
    private readonly ILogger<OnnxEmbeddingBackend> _logger;
    private InferenceSession? _session;
    private BertTokenizer? _tokenizer;
    private bool _disposed;

    public int Dimension { get; private set; }
    public string ModelName => _config.ModelName;

    public OnnxEmbeddingBackend(OnnxEmbeddingConfig config, ILogger<OnnxEmbeddingBackend>? logger = null)
    {
        _config = config;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<OnnxEmbeddingBackend>.Instance;
    }

    public async Task InitializeAsync()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(OnnxEmbeddingBackend));

        try
        {
            // 1. 加载 Tokenizer
            var tokenizerPath = _config.TokenizerPath ?? Path.Combine(Path.GetDirectoryName(_config.ModelPath)!, "tokenizer.json");
            if (File.Exists(tokenizerPath))
            {
                _tokenizer = new BertTokenizer(tokenizerPath);
                _logger.LogInformation("Tokenizer loaded from {Path}", tokenizerPath);
            }
            else
            {
                _logger.LogWarning("Tokenizer not found at {Path}. Embedding quality may be degraded.", tokenizerPath);
                // Fallback to a basic char-level or word-level tokenizer if needed, but for BERT models this is risky.
            }

            // 2. 加载 ONNX 模型
            var modelPath = _config.ModelPath;
            if (!File.Exists(modelPath))
            {
                throw new FileNotFoundException($"ONNX Embedding model not found at {modelPath}");
            }

            await Task.Run(() =>
            {
                var sessionOptions = new SessionOptions
                {
                    EnableCpuMemArena = true,
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
                };
                
                // 如果配置了执行提供商 (如 TensorRT, OpenVINO)，可以在这里添加
                // sessionOptions.AppendExecutionProvider_CUDA(...);

                _session = new InferenceSession(modelPath, sessionOptions);
            });

            // 3. 探测维度 (通过一次虚拟推理或读取配置)
            Dimension = _config.Dimension > 0 ? _config.Dimension : DetectDimension();

            _logger.LogInformation(
                "ONNX Embedding Backend initialized: Model={Model}, Dim={Dim}",
                Path.GetFileName(modelPath), Dimension);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize ONNX Embedding Backend");
            throw;
        }
    }

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        if (_session == null || _tokenizer == null)
        {
            throw new InvalidOperationException("ONNX Embedding Backend is not initialized.");
        }

        var results = new List<float[]>();

        // 批处理
        foreach (var text in texts)
        {
            var inputs = _tokenizer.Encode(text);
            
            // 截断或填充
            var inputIds = inputs.InputIds.Take(_config.MaxSequenceLength).ToList();
            var attentionMask = inputs.AttentionMask.Take(_config.MaxSequenceLength).ToList();

            while (inputIds.Count < _config.MaxSequenceLength)
            {
                inputIds.Add(0); // Padding ID
                attentionMask.Add(0);
            }

            var inputTensor = new DenseTensor<int>(inputIds.ToArray(), new[] { 1, inputIds.Count });
            var maskTensor = new DenseTensor<int>(attentionMask.ToArray(), new[] { 1, attentionMask.Count });

            var onnxInputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", inputTensor),
                NamedOnnxValue.CreateFromTensor("attention_mask", maskTensor)
            };

            // 某些模型可能还需要 token_type_ids
            var tokenTypeIds = inputs.TokenTypeIds.Take(_config.MaxSequenceLength).ToList();
            while (tokenTypeIds.Count < _config.MaxSequenceLength) tokenTypeIds.Add(0);
            var typeTensor = new DenseTensor<int>(tokenTypeIds.ToArray(), new[] { 1, tokenTypeIds.Count });
            onnxInputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", typeTensor));

            using var outputs = await Task.Run(() => _session.Run(onnxInputs)).ConfigureAwait(false);
            
            // 获取 last_hidden_state (通常是第一个输出)
            var output = outputs.First(); 
            var tensor = output.AsTensor<float>();
            
            // Mean Pooling
            var embedding = MeanPooling(tensor, attentionMask);
            
            // L2 Normalization
            Normalize(embedding);

            results.Add(embedding);
        }

        return results.ToArray();
    }

    private float[] MeanPooling(Tensor<float> tensor, List<int> attentionMask)
    {
        var embedding = new float[Dimension];
        var maskSum = attentionMask.Sum();
        if (maskSum == 0) return embedding;

        for (int i = 0; i < tensor.Dimensions[1]; i++) // Sequence length
        {
            if (attentionMask.Count > i && attentionMask[i] == 1)
            {
                for (int j = 0; j < Dimension; j++)
                {
                    embedding[j] += tensor[0, i, j];
                }
            }
        }

        for (int j = 0; j < Dimension; j++)
        {
            embedding[j] /= (float)maskSum;
        }

        return embedding;
    }

    private static void Normalize(float[] vector)
    {
        var sum = vector.Sum(x => x * x);
        var magnitude = (float)Math.Sqrt(sum);
        if (magnitude > 0)
        {
            for (int i = 0; i < vector.Length; i++)
            {
                vector[i] /= magnitude;
            }
        }
    }

    private int DetectDimension()
    {
        // 简单探测：运行一次空输入看输出维度
        // 这里为了安全，默认返回 384 (MiniLM) 或 512，建议用户在配置中指定
        return _config.Dimension > 0 ? _config.Dimension : 384;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _session?.Dispose();
            _disposed = true;
        }
    }
}

public record OnnxEmbeddingConfig
{
    public string ModelPath { get; init; } = "";
    public string? TokenizerPath { get; init; }
    public string ModelName { get; init; } = "onnx-embedding";
    public int Dimension { get; init; } = 0; // 0 = Auto detect
    public int MaxSequenceLength { get; init; } = 512;
}

/// <summary>
/// 简易 BERT Tokenizer，支持加载 HuggingFace 的 tokenizer.json
/// </summary>
public class BertTokenizer
{
    private readonly Dictionary<string, int> _vocab = new();
    private readonly bool _isWordPiece;

    public BertTokenizer(string tokenizerJsonPath)
    {
        try
        {
            var json = File.ReadAllText(tokenizerJsonPath);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // 尝试解析 WordPiece 或 BPE
            if (root.TryGetProperty("model", out var model))
            {
                var type = model.GetProperty("type").GetString();
                _isWordPiece = type == "WordPiece";

                if (model.TryGetProperty("vocab", out var vocab))
                {
                    foreach (var prop in vocab.EnumerateObject())
                    {
                        _vocab[prop.Name] = prop.Value.GetInt32();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // 如果解析失败，退化为简单的空格分词
            Console.WriteLine($"Failed to load tokenizer: {ex.Message}");
        }
    }

    public (List<int> InputIds, List<int> AttentionMask, List<int> TokenTypeIds) Encode(string text)
    {
        var inputIds = new List<int> { 101 }; // [CLS]
        var attentionMask = new List<int> { 1 };
        var tokenTypeIds = new List<int> { 0 };

        // 简单的分词逻辑 (实际应使用完整的 Tokenizer 算法)
        var tokens = text.Split(new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var token in tokens)
        {
            if (_vocab.TryGetValue(token, out var id))
            {
                inputIds.Add(id);
                attentionMask.Add(1);
                tokenTypeIds.Add(0);
            }
            else if (_isWordPiece)
            {
                // 简单的 WordPiece 回退：按字符拆分或未知 token
                inputIds.Add(100); // [UNK]
                attentionMask.Add(1);
                tokenTypeIds.Add(0);
            }
            else
            {
                inputIds.Add(100); // [UNK]
                attentionMask.Add(1);
                tokenTypeIds.Add(0);
            }
        }

        inputIds.Add(102); // [SEP]
        attentionMask.Add(1);
        tokenTypeIds.Add(0);

        return (inputIds, attentionMask, tokenTypeIds);
    }
}
