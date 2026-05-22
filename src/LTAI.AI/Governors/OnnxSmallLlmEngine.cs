using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace LTAI.AI.Governors;

// ==================== 本地小模型引擎 ====================

public interface ILocalLlmEngine
{
    bool IsReady { get; }
    string ModelName { get; }
    Task<string> GenerateAsync(string prompt, float temperature = 0.7f, int maxTokens = 256, CancellationToken ct = default);
    void Dispose();
}

public record SmallLlmConfig
{
    public string ModelPath { get; init; } = "";
    public string TokenizerPath { get; init; } = "";
    public string ModelName { get; init; } = "local-llm";
    public int MaxContextLength { get; init; } = 2048;
    public int MaxNewTokens { get; init; } = 256;
}

public sealed class OnnxSmallLlmEngine : IL1InferenceEngine
{
    private readonly SmallLlmConfig _config;
    private readonly ILogger<OnnxSmallLlmEngine> _logger;
    private InferenceSession? _session;
    private SimpleTokenizer? _tokenizer;
    private readonly object _lock = new();
    private bool _isReady;

    public OnnxSmallLlmEngine(SmallLlmConfig config, ILogger<OnnxSmallLlmEngine>? logger = null)
    {
        _config = config;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<OnnxSmallLlmEngine>.Instance;
    }

    public bool IsReady => _isReady;
    public string ModelName => _config.ModelName;
    public string EngineType => "onnx";
    public long ModelSizeMB => File.Exists(_config.ModelPath) ? new FileInfo(_config.ModelPath).Length / 1024 / 1024 : 0;
    public int HiddenDimension => 768; // ONNX 小模型默认隐藏维度

    public async Task InitializeAsync(string? modelPath = null, CancellationToken ct = default)
    {
        var pathToLoad = modelPath ?? _config.ModelPath;
        try
        {
            if (!File.Exists(_config.ModelPath))
            {
                _logger.LogWarning("Model file not found: {Path}", _config.ModelPath);
                return;
            }

            await Task.Run(() =>
            {
                lock (_lock)
                {
                    var options = new SessionOptions
                    {
                        EnableCpuMemArena = true,
                        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
                    };
                    _session = new InferenceSession(_config.ModelPath, options);
                }
            }, ct);

            // 加载 Tokenizer
            if (File.Exists(_config.TokenizerPath))
            {
                _tokenizer = new SimpleTokenizer(_config.TokenizerPath);
            }
            else
            {
                _logger.LogWarning("Tokenizer not found, using fallback char-level tokenization");
                _tokenizer = new SimpleTokenizer(); // 使用默认 fallback
            }

            _isReady = true;
            _logger.LogInformation("Local LLM initialized: {Name}", _config.ModelName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Local LLM");
        }
    }

    public async Task<string> GenerateAsync(string prompt, float temperature = 0.7f, int maxTokens = 256, CancellationToken ct = default)
    {
        if (!_isReady || _session == null || _tokenizer == null)
            return "";

        return await Task.Run(() =>
        {
            var inputIds = _tokenizer.Encode(prompt);
            var attentionMask = Enumerable.Repeat(1, inputIds.Count).ToList();

            // 截断过长的输入
            if (inputIds.Count > _config.MaxContextLength)
            {
                inputIds = inputIds.Skip(inputIds.Count - _config.MaxContextLength).ToList();
                attentionMask = attentionMask.Skip(attentionMask.Count - _config.MaxContextLength).ToList();
            }

            var generatedTokens = new List<int>(inputIds);
            var generatedMask = new List<int>(attentionMask);

            for (int i = 0; i < Math.Min(maxTokens, _config.MaxNewTokens); i++)
            {
                if (ct.IsCancellationRequested) break;

                var inputTensor = new DenseTensor<int>(generatedTokens.ToArray(), new[] { 1, generatedTokens.Count });
                var maskTensor = new DenseTensor<int>(generatedMask.ToArray(), new[] { 1, generatedMask.Count });

                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("input_ids", inputTensor),
                    NamedOnnxValue.CreateFromTensor("attention_mask", maskTensor)
                };

                using var results = _session.Run(inputs);
                var logits = results.First().AsTensor<float>();

                // 获取最后一个 token 的 logits
                var vocabSize = (int)logits.Dimensions[2];
                var lastLogits = new float[vocabSize];
                for (int j = 0; j < vocabSize; j++)
                {
                    lastLogits[j] = logits[0, logits.Dimensions[1] - 1, j];
                }

                // 采样
                var nextToken = SampleToken(lastLogits, temperature);
                if (nextToken == _tokenizer.EosTokenId) break;

                generatedTokens.Add(nextToken);
                generatedMask.Add(1);
            }

            // 解码生成的部分（跳过 prompt）
            var newTokens = generatedTokens.Skip(inputIds.Count).ToList();
            return _tokenizer.Decode(newTokens);
        }, ct);
    }

    private static int SampleToken(float[] logits, float temperature)
    {
        // 应用温度
        if (temperature > 0)
        {
            for (int i = 0; i < logits.Length; i++)
            {
                logits[i] /= temperature;
            }
        }

        // Softmax
        var maxLogit = logits.Max();
        var sumExp = logits.Sum(l => Math.Exp(l - maxLogit));
        var probs = logits.Select(l => Math.Exp(l - maxLogit) / sumExp).ToArray();

        // 多项式采样
        var rand = new Random().NextDouble();
        var cumulative = 0.0;
        for (int i = 0; i < probs.Length; i++)
        {
            cumulative += probs[i];
            if (rand <= cumulative) return i;
        }
        return probs.Length - 1;
    }

    public void Dispose()
    {
        _session?.Dispose();
        _isReady = false;
    }

    public async Task<LatentState> EncodeToLatentAsync(string text, CancellationToken ct = default)
    {
        if (!_isReady || _session == null || _tokenizer == null)
            return LatentState.Create(Array.Empty<float>());

        return await Task.Run(() =>
        {
            var inputIds = _tokenizer.Encode(text);
            var attentionMask = Enumerable.Repeat(1, inputIds.Count).ToList();
            
            var inputTensor = new DenseTensor<int>(inputIds.ToArray(), new[] { 1, inputIds.Count });
            var maskTensor = new DenseTensor<int>(attentionMask.ToArray(), new[] { 1, attentionMask.Count });

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", inputTensor),
                NamedOnnxValue.CreateFromTensor("attention_mask", maskTensor)
            };

            using var results = _session.Run(inputs);
            var hiddenStates = results.Last().AsTensor<float>();
            
            // 提取最后一层隐藏状态 [batch, seq, hidden]
            var hiddenDim = (int)hiddenStates.Dimensions[2];
            var lastHidden = new float[hiddenDim];
            var seqLen = (int)hiddenStates.Dimensions[1];
            for (int i = 0; i < hiddenDim; i++)
                lastHidden[i] = hiddenStates[0, seqLen - 1, i];
            
            return LatentState.Create(lastHidden, source: _config.ModelName);
        }, ct);
    }

    public async Task<LatentState> RefineLatentAsync(LatentState latent, float temperature = 0.6f, CancellationToken ct = default)
    {
        if (!_isReady || _session == null) return latent;
        
        // 简化实现：直接返回输入潜状态 (实际应执行前向传播)
        await Task.Delay(1, ct);
        return latent with { RecursionDepth = latent.RecursionDepth + 1 };
    }

    public async Task<string> DecodeFromLatentAsync(LatentState latent, CancellationToken ct = default)
    {
        if (!_isReady || _tokenizer == null) return "";
        
        await Task.Delay(1, ct);
        return "[ONNX Latent Decode - Placeholder]";
    }
}

// ==================== 简易 Tokenizer ====================

public class SimpleTokenizer
{
    private readonly Dictionary<string, int> _encoder = new();
    private readonly Dictionary<int, string> _decoder = new();
    public int EosTokenId { get; private set; } = 50256; // GPT-2 default

    public SimpleTokenizer()
    {
        // Fallback: char-level
        for (int i = 0; i < 256; i++)
        {
            var c = ((char)i).ToString();
            _encoder[c] = i;
            _decoder[i] = c;
        }
    }

    public SimpleTokenizer(string tokenizerJsonPath)
    {
        try
        {
            var json = File.ReadAllText(tokenizerJsonPath);
            var doc = JsonDocument.Parse(json);
            var model = doc.RootElement.GetProperty("model");
            
            if (model.TryGetProperty("vocab", out var vocab))
            {
                foreach (var prop in vocab.EnumerateObject())
                {
                    _encoder[prop.Name] = prop.Value.GetInt32();
                    _decoder[prop.Value.GetInt32()] = prop.Name;
                }
            }

            if (doc.RootElement.TryGetProperty("added_tokens", out var added))
            {
                foreach (var token in added.EnumerateArray())
                {
                    var id = token.GetProperty("id").GetInt32();
                    var content = token.GetProperty("content").GetString();
                    if (content != null)
                    {
                        _encoder[content] = id;
                        _decoder[id] = content;
                        if (content == "<|endoftext|>") EosTokenId = id;
                    }
                }
            }
        }
        catch (Exception)
        {
            // Fallback to char-level if parsing fails
            new SimpleTokenizer(); 
        }
    }

    public List<int> Encode(string text)
    {
        var tokens = new List<int>();
        // 简单实现：尝试匹配最长子串，否则按字符
        // 为简化代码，这里仅做字符级或简单空格分词
        // 实际生产应使用完整的 BPE 算法
        var words = text.Split(' ');
        foreach (var word in words)
        {
            if (_encoder.TryGetValue(word, out var id))
            {
                tokens.Add(id);
            }
            else
            {
                foreach (var c in word)
                {
                    var s = c.ToString();
                    if (_encoder.TryGetValue(s, out var cid)) tokens.Add(cid);
                }
            }
            tokens.Add(220); // Space token ID (GPT-2)
        }
        return tokens;
    }

    public string Decode(List<int> tokens)
    {
        var sb = new StringBuilder();
        foreach (var id in tokens)
        {
            if (_decoder.TryGetValue(id, out var s))
            {
                sb.Append(s);
            }
        }
        return sb.ToString();
    }
}
