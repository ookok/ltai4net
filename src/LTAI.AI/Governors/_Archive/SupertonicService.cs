using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace LTAI.AI.Governors;

public sealed class SupertonicService : IDisposable
{
    private InferenceSession? _ttsSession;
    private readonly string? _modelPath;
    private readonly string _assetsDir;
    private readonly ILogger<SupertonicService> _logger;
    private readonly ConcurrentDictionary<string, SupertonicVoiceStyle> _voices = new();

    private readonly ConcurrentQueue<double> _recentInferenceMs = new();
    private long _totalInferences;
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);
    private bool _disposed;

    public SupertonicStatus Status => new()
    {
        IsLoaded = _ttsSession != null,
        ModelPath = _modelPath ?? "",
        ModelSizeMb = _modelPath != null && File.Exists(_modelPath) ? new FileInfo(_modelPath).Length / 1024 / 1024 : 0,
        LoadedVoices = _voices.Count,
        SupportedLanguages = SupertonicLanguages.Supported.Count,
        TotalInferences = _totalInferences,
        AvgInferenceMs = _recentInferenceMs.Any() ? _recentInferenceMs.Average() : 0
    };

    public SupertonicService(
        string? modelPath = null,
        string? assetsDir = null,
        ILogger<SupertonicService>? logger = null)
    {
        _logger = logger ?? NullLogger<SupertonicService>.Instance;
        _modelPath = modelPath;
        _assetsDir = assetsDir ?? Path.Combine(Directory.GetCurrentDirectory(), "assets", "supertonic");

        if (!string.IsNullOrEmpty(_modelPath) && File.Exists(_modelPath))
            LoadModel();
        else
            _logger.LogInformation("Supertonic model not found at {Path}, set modelPath to load", _modelPath);

        RegisterBuiltInVoices();
    }

    public void LoadModel(string? modelPath = null)
    {
        var path = modelPath ?? _modelPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            _logger.LogWarning("Cannot load Supertonic model: path {Path} is invalid", path);
            return;
        }

        _ttsSession?.Dispose();

        try
        {
            var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
                InterOpNumThreads = 1
            };

            _ttsSession = new InferenceSession(path, options);
            _logger.LogInformation("Supertonic TTS model loaded: {Path} ({Size}MB, {Inputs} inputs, {Outputs} outputs)",
                path, new FileInfo(path).Length / 1024 / 1024,
                _ttsSession.InputMetadata.Count, _ttsSession.OutputMetadata.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load Supertonic ONNX model from {Path}", path);
            _ttsSession = null;
            throw;
        }
    }

    public async Task<SupertonicSynthesizeResult> SynthesizeAsync(
        SupertonicSynthesizeRequest request,
        CancellationToken ct = default)
    {
        if (_ttsSession == null)
        {
            return new SupertonicSynthesizeResult
            {
                Success = false,
                Error = "Supertonic TTS model not loaded. Call LoadModel() first.",
                VoiceName = request.VoiceName,
                Language = request.Language
            };
        }

        if (string.IsNullOrWhiteSpace(request.Text))
        {
            return new SupertonicSynthesizeResult
            {
                Success = false,
                Error = "Text cannot be empty",
                VoiceName = request.VoiceName,
                Language = request.Language
            };
        }

        var normalizedLang = SupertonicLanguages.NormalizeLanguage(request.Language);
        var voice = GetVoice(request.VoiceName);

        await _inferenceLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var processedText = ProcessExpressionTags(request.Text, request.ExpressionTags);
            var inputTensor = TokenizeText(processedText);

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", inputTensor),
                NamedOnnxValue.CreateFromTensor("attention_mask", CreateAttentionMask(inputTensor)),
                NamedOnnxValue.CreateFromTensor("lang_id", CreateLangTensor(normalizedLang)),
                NamedOnnxValue.CreateFromTensor("speed", new DenseTensor<float>(new[] { Math.Clamp(request.Speed, 0.3f, 3.0f) }, new[] { 1 }))
            };

            if (voice?.StyleEmbedding != null)
            {
                inputs.Add(NamedOnnxValue.CreateFromTensor("style_embedding",
                    new DenseTensor<float>(voice.StyleEmbedding, new[] { 1, voice.StyleEmbedding.Length })));
            }

            using var results = _ttsSession.Run(inputs);
            var audioTensor = results.FirstOrDefault(r => r.Name == "wav" || r.Name == "audio")?.AsTensor<float>();

            sw.Stop();

            if (audioTensor == null)
            {
                var outputNames = string.Join(", ", results.Select(r => r.Name));
                return new SupertonicSynthesizeResult
                {
                    Success = false,
                    Error = $"No audio output found in model results. Available outputs: [{outputNames}]",
                    VoiceName = request.VoiceName,
                    Language = normalizedLang,
                    InferenceMs = sw.ElapsedMilliseconds
                };
            }

            var samples = audioTensor.ToArray();
            var wavBytes = ConvertToWav(samples, 44100);

            var durationSec = (float)samples.Length / 44100f;
            _recentInferenceMs.Enqueue(sw.ElapsedMilliseconds);
            while (_recentInferenceMs.Count > 100) _recentInferenceMs.TryDequeue(out _);
            Interlocked.Increment(ref _totalInferences);

            _logger.LogDebug("Supertonic synthesized {Chars} chars → {Duration}s audio in {Ms}ms (lang={Lang}, voice={Voice})",
                request.Text.Length, durationSec.ToString("F2"), sw.ElapsedMilliseconds, normalizedLang, request.VoiceName);

            return new SupertonicSynthesizeResult
            {
                Success = true,
                WavBytes = wavBytes,
                AudioSamples = samples,
                DurationSeconds = durationSec,
                VoiceName = request.VoiceName,
                Language = normalizedLang,
                InferenceMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Supertonic synthesis failed for text length {Len}", request.Text.Length);
            return new SupertonicSynthesizeResult
            {
                Success = false,
                Error = $"Synthesis failed: {ex.Message}",
                VoiceName = request.VoiceName,
                Language = request.Language
            };
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    public Task<SupertonicSynthesizeResult> SpeakAsync(
        string text,
        string language = "en",
        string voiceName = "M1",
        float speed = 1.0f,
        CancellationToken ct = default)
    {
        return SynthesizeAsync(new SupertonicSynthesizeRequest
        {
            Text = text,
            Language = language,
            VoiceName = voiceName,
            Speed = speed,
            TotalSteps = 8
        }, ct);
    }

    public SupertonicVoiceStyle? GetVoice(string name) =>
        _voices.TryGetValue(name.ToLowerInvariant(), out var voice) ? voice : _voices.Values.FirstOrDefault();

    public IReadOnlyCollection<SupertonicVoiceStyle> ListVoices() => _voices.Values.ToList().AsReadOnly();

    public void RegisterVoice(SupertonicVoiceStyle voice)
    {
        _voices[voice.Name.ToLowerInvariant()] = voice;
        _logger.LogInformation("Registered Supertonic voice: {Name} ({Lang})", voice.Name, voice.Language);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ttsSession?.Dispose();
        _inferenceLock.Dispose();
    }

    private void RegisterBuiltInVoices()
    {
        var builtIn = new[]
        {
            ("M1", "en", "Deep male voice"),
            ("M2", "en", "Warm male voice"),
            ("M3", "en", "Bold male voice"),
            ("M4", "en", "Calm male voice"),
            ("M5", "en", "Friendly male voice"),
            ("F1", "en", "Clear female voice"),
            ("F2", "en", "Soft female voice"),
            ("F3", "en", "Bright female voice"),
            ("F4", "en", "Gentle female voice"),
            ("F5", "en", "Warm female voice"),
        };

        foreach (var (name, lang, desc) in builtIn)
        {
            if (!_voices.ContainsKey(name.ToLowerInvariant()))
            {
                _voices[name.ToLowerInvariant()] = new SupertonicVoiceStyle
                {
                    Name = name,
                    Language = lang,
                    Description = desc
                };
            }
        }
    }

    private static string ProcessExpressionTags(string text, List<string> explicitTags)
    {
        if (explicitTags.Count > 0) return text;

        return text.Replace("(laugh)", "<laugh>", StringComparison.OrdinalIgnoreCase)
                   .Replace("(breath)", "<breath>", StringComparison.OrdinalIgnoreCase)
                   .Replace("(sigh)", "<sigh>", StringComparison.OrdinalIgnoreCase)
                   .Replace("(cough)", "<cough>", StringComparison.OrdinalIgnoreCase)
                   .Replace("(chuckle)", "<chuckle>", StringComparison.OrdinalIgnoreCase)
                   .Replace("(gasp)", "<gasp>", StringComparison.OrdinalIgnoreCase);
    }

    private static DenseTensor<long> TokenizeText(string text)
    {
        const int maxLen = 512;
        var tokens = new long[maxLen];

        for (int i = 0; i < Math.Min(text.Length, maxLen); i++)
        {
            tokens[i] = (int)text[i] % 30522;
        }

        return new DenseTensor<long>(tokens, new[] { 1, maxLen });
    }

    private static DenseTensor<long> CreateAttentionMask(DenseTensor<long> inputTokens)
    {
        var shape = inputTokens.Dimensions;
        var mask = new long[shape[0] * shape[1]];

        for (int i = 0; i < shape[0]; i++)
        {
            for (int j = 0; j < shape[1]; j++)
            {
                mask[i * shape[1] + j] = inputTokens[i, j] != 0 ? 1 : 0;
            }
        }

        return new DenseTensor<long>(mask, shape);
    }

    private static DenseTensor<long> CreateLangTensor(string lang)
    {
        var langId = Math.Abs(string.GetHashCode(lang)) % 32;
        return new DenseTensor<long>(new[] { (long)langId }, new[] { 1 });
    }

    private static byte[] ConvertToWav(float[] samples, int sampleRate)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        var byteRate = sampleRate * 2;
        var dataSize = samples.Length * 2;

        bw.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
        bw.Write(36 + dataSize);
        bw.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });
        bw.Write(new[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)1);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write((short)2);
        bw.Write((short)16);
        bw.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
        bw.Write(dataSize);

        foreach (var sample in samples)
        {
            var clamped = Math.Clamp(sample * 32767f, -32768, 32767);
            bw.Write((short)clamped);
        }

        return ms.ToArray();
    }
}
