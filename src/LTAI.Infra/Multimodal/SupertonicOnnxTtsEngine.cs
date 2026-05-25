using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LTAI.Core.Multimodal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace LTAI.Infra.Multimodal;

public sealed class SupertonicOnnxTtsEngine : ITtsEngine, IDisposable
{
    private readonly SupertonicOnnxTtsConfig _config;
    private readonly ILogger<SupertonicOnnxTtsEngine> _logger;
    private readonly InferenceSession _dpOrt;
    private readonly InferenceSession _textEncOrt;
    private readonly InferenceSession _vectorEstOrt;
    private readonly InferenceSession _vocoderOrt;
    private readonly UnicodeIndexer _unicodeIndexer;
    private readonly Dictionary<string, Style> _voiceCache = new();
    private readonly Lock _voiceLock = new();

    private readonly int _sampleRate;
    private readonly int _baseChunkSize;
    private readonly int _chunkCompressFactor;
    private readonly int _ldim;

    private static readonly Regex s_paragraphRegex = new(
        @"\n\s*\n+",
        RegexOptions.Compiled);
    private static readonly Regex s_sentenceRegex = new(
        @"(?<!Mr\.|Mrs\.|Ms\.|Dr\.|Prof\.|Sr\.|Jr\.|etc\.|e\.g\.|i\.e\.|vs\.)(?<!\b[A-Z]\.)(?<=[.!?])\s+",
        RegexOptions.Compiled);

    public string EngineName => "Supertonic ONNX";
    public bool IsAvailable => true;

    public SupertonicOnnxTtsEngine(SupertonicOnnxTtsConfig config, ILogger<SupertonicOnnxTtsEngine>? logger = null)
    {
        _config = config;
        _logger = logger ?? NullLogger<SupertonicOnnxTtsEngine>.Instance;

        var opts = new SessionOptions
        {
            EnableCpuMemArena = true,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };

        var onnxDir = ResolveOnnxDir();
        var dpPath = Path.Combine(onnxDir, "duration_predictor.onnx");
        var textEncPath = Path.Combine(onnxDir, "text_encoder.onnx");
        var vectorEstPath = Path.Combine(onnxDir, "vector_estimator.onnx");
        var vocoderPath = Path.Combine(onnxDir, "vocoder.onnx");

        _dpOrt = new InferenceSession(dpPath, opts);
        _textEncOrt = new InferenceSession(textEncPath, opts);
        _vectorEstOrt = new InferenceSession(vectorEstPath, opts);
        _vocoderOrt = new InferenceSession(vocoderPath, opts);

        _unicodeIndexer = new UnicodeIndexer(Path.Combine(onnxDir, "unicode_indexer.json"));

        var cfgPath = Path.Combine(onnxDir, "tts.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(cfgPath));
        var root = doc.RootElement;
        _sampleRate = root.GetProperty("ae").GetProperty("sample_rate").GetInt32();
        _baseChunkSize = root.GetProperty("ae").GetProperty("base_chunk_size").GetInt32();
        _chunkCompressFactor = root.GetProperty("ttl").GetProperty("chunk_compress_factor").GetInt32();
        _ldim = root.GetProperty("ttl").GetProperty("latent_dim").GetInt32();

        _logger.LogInformation("Supertonic ONNX TTS initialized: 4 models loaded, sr={SampleRate}, ldim={Ldim}",
            _sampleRate, _ldim);
    }

    private string ResolveOnnxDir()
    {
        if (Directory.Exists(_config.OnnxDir))
            return _config.OnnxDir;

        var alt = Path.Combine(AppContext.BaseDirectory, "assets", "onnx");
        if (Directory.Exists(alt))
            return alt;

        throw new DirectoryNotFoundException($"Supertonic ONNX models not found at {_config.OnnxDir} or {alt}. Clone https://huggingface.co/Supertone/supertonic-3 to the assets directory.");
    }

    private string ResolveVoiceStyleDir()
    {
        if (Directory.Exists(_config.VoiceStyleDir))
            return _config.VoiceStyleDir;

        var alt = Path.Combine(AppContext.BaseDirectory, "assets", "voice_styles");
        if (Directory.Exists(alt))
            return alt;

        return _config.VoiceStyleDir;
    }

    public async Task<TtsResult> SynthesizeAsync(string text, TtsSynthesisOptions? options = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new TtsResult { Error = "Empty text" };

        options ??= TtsSynthesisOptions.Default;
        var voice = options.Voice ?? _config.DefaultVoice;
        var lang = options.Lang ?? "en";
        var totalStep = options.TotalSteps > 0 ? options.TotalSteps : _config.TotalSteps;
        var speed = options.Speed > 0 ? options.Speed : _config.Speed;

        try
        {
            var style = LoadVoice(voice);
            var textList = ChunkText(text, lang);
            var wavCat = new List<float>();
            float durCat = 0.0f;

            for (int chunkIdx = 0; chunkIdx < textList.Count; chunkIdx++)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = textList[chunkIdx];
                var (wav, duration) = await Task.Run(() =>
                    Infer(new List<string> { chunk }, new List<string> { lang }, style, totalStep, speed), ct).ConfigureAwait(false);

                if (wavCat.Count == 0)
                {
                    wavCat.AddRange(wav);
                    durCat = duration[0];
                }
                else
                {
                    int silenceLen = (int)(_config.SilenceDuration * _sampleRate);
                    var silence = new float[silenceLen];
                    wavCat.AddRange(silence);
                    wavCat.AddRange(wav);
                    durCat += duration[0] + _config.SilenceDuration;
                }
            }

            var wavBytes = FloatSamplesToWavBytes(wavCat.ToArray(), _sampleRate);
            return new TtsResult
            {
                AudioBytes = wavBytes,
                Format = "wav",
                Voice = voice,
                DurationSeconds = durCat,
                SampleRate = _sampleRate,
                Ok = true
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Supertonic ONNX TTS synthesis failed");
            return new TtsResult { Ok = false, Error = ex.Message };
        }
    }

    public async Task<VoiceInfo[]> GetVoicesAsync(CancellationToken ct = default)
    {
        var styleDir = ResolveVoiceStyleDir();
        if (!Directory.Exists(styleDir))
            return Array.Empty<VoiceInfo>();

        var files = Directory.GetFiles(styleDir, "*.json");
        var voices = new List<VoiceInfo>();
        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            string? description = null;
            try
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false));
                if (doc.RootElement.TryGetProperty("description", out var desc))
                    description = desc.GetString();
            }
            catch { }
            voices.Add(new VoiceInfo { Name = name, Description = description });
        }
        return voices.ToArray();
    }

    private Style LoadVoice(string voiceName)
    {
        lock (_voiceLock)
        {
            if (_voiceCache.TryGetValue(voiceName, out var cached))
                return cached;
        }

        var styleDir = ResolveVoiceStyleDir();
        var stylePath = Path.Combine(styleDir, $"{voiceName}.json");
        if (!File.Exists(stylePath))
        {
            stylePath = Path.Combine(styleDir, $"{voiceName}.json");
            var altLookup = Directory.GetFiles(styleDir, $"{voiceName}*.json").FirstOrDefault();
            if (altLookup != null) stylePath = altLookup;
            else throw new FileNotFoundException($"Voice style not found: {voiceName}.json in {styleDir}");
        }

        var style = Style.FromJsonFile(stylePath);

        lock (_voiceLock)
        {
            _voiceCache[voiceName] = style;
        }

        return style;
    }

    private (float[] wav, float[] duration) Infer(
        List<string> textList, List<string> langList, Style style, int totalStep, float speed)
    {
        int bsz = textList.Count;

        var (textIds, textMask) = _unicodeIndexer.Process(textList, langList);
        var textIdsShape = new long[] { bsz, textIds[0].Length };
        var textMaskShape = new long[] { bsz, 1, textMask[0][0].Length };

        var textIdsTensor = IntArrayToTensor(textIds, textIdsShape);
        var textMaskTensor = ArrayToTensor(textMask, textMaskShape);

        var styleTtlTensor = new DenseTensor<float>(style.Ttl, style.TtlShape.Select(x => (int)x).ToArray());
        var styleDpTensor = new DenseTensor<float>(style.Dp, style.DpShape.Select(x => (int)x).ToArray());

        var dpInputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("text_ids", textIdsTensor),
            NamedOnnxValue.CreateFromTensor("style_dp", styleDpTensor),
            NamedOnnxValue.CreateFromTensor("text_mask", textMaskTensor)
        };
        using var dpOutputs = _dpOrt.Run(dpInputs);
        var durOnnx = dpOutputs.First(o => o.Name == "duration").AsTensor<float>().ToArray();
        for (int i = 0; i < durOnnx.Length; i++)
            durOnnx[i] /= speed;

        var textEncInputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("text_ids", textIdsTensor),
            NamedOnnxValue.CreateFromTensor("style_ttl", styleTtlTensor),
            NamedOnnxValue.CreateFromTensor("text_mask", textMaskTensor)
        };
        using var textEncOutputs = _textEncOrt.Run(textEncInputs);
        var textEmbTensor = textEncOutputs.First(o => o.Name == "text_emb").AsTensor<float>();

        var (xt, latentMask) = SampleNoisyLatent(durOnnx);
        var latentShape = new long[] { bsz, xt[0].Length, xt[0][0].Length };
        var latentMaskShape = new long[] { bsz, 1, latentMask[0][0].Length };
        var totalStepArray = Enumerable.Repeat((float)totalStep, bsz).ToArray();

        for (int step = 0; step < totalStep; step++)
        {
            var currentStepArray = Enumerable.Repeat((float)step, bsz).ToArray();
            var vectorEstInputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("noisy_latent", ArrayToTensor(xt, latentShape)),
                NamedOnnxValue.CreateFromTensor("text_emb", textEmbTensor),
                NamedOnnxValue.CreateFromTensor("style_ttl", styleTtlTensor),
                NamedOnnxValue.CreateFromTensor("text_mask", textMaskTensor),
                NamedOnnxValue.CreateFromTensor("latent_mask", ArrayToTensor(latentMask, latentMaskShape)),
                NamedOnnxValue.CreateFromTensor("total_step", new DenseTensor<float>(totalStepArray, new[] { bsz })),
                NamedOnnxValue.CreateFromTensor("current_step", new DenseTensor<float>(currentStepArray, new[] { bsz }))
            };

            using var vectorEstOutputs = _vectorEstOrt.Run(vectorEstInputs);
            var denoisedLatent = vectorEstOutputs.First(o => o.Name == "denoised_latent").AsTensor<float>();

            int idx = 0;
            for (int b = 0; b < bsz; b++)
                for (int d = 0; d < xt[b].Length; d++)
                    for (int t = 0; t < xt[b][d].Length; t++)
                        xt[b][d][t] = denoisedLatent.GetValue(idx++);
        }

        var vocoderInputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("latent", ArrayToTensor(xt, latentShape))
        };
        using var vocoderOutputs = _vocoderOrt.Run(vocoderInputs);
        var wavTensor = vocoderOutputs.First(o => o.Name == "wav_tts").AsTensor<float>();
        return (wavTensor.ToArray(), durOnnx);
    }

    private (float[][][] noisyLatent, float[][][] latentMask) SampleNoisyLatent(float[] duration)
    {
        int bsz = duration.Length;
        float wavLenMax = duration.Max() * _sampleRate;
        var wavLengths = duration.Select(d => (long)(d * _sampleRate)).ToArray();
        int chunkSize = _baseChunkSize * _chunkCompressFactor;
        int latentLen = (int)((wavLenMax + chunkSize - 1) / chunkSize);
        int latentDim = _ldim * _chunkCompressFactor;

        var random = new Random();
        var noisyLatent = new float[bsz][][];
        for (int b = 0; b < bsz; b++)
        {
            noisyLatent[b] = new float[latentDim][];
            for (int d = 0; d < latentDim; d++)
            {
                noisyLatent[b][d] = new float[latentLen];
                for (int t = 0; t < latentLen; t++)
                {
                    double u1 = 1.0 - random.NextDouble();
                    double u2 = 1.0 - random.NextDouble();
                    noisyLatent[b][d][t] = (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
                }
            }
        }

        var latentMask = GetLatentMask(wavLengths);
        for (int b = 0; b < bsz; b++)
            for (int d = 0; d < latentDim; d++)
                for (int t = 0; t < latentLen; t++)
                    noisyLatent[b][d][t] *= latentMask[b][0][t];

        return (noisyLatent, latentMask);
    }

    private float[][][] GetLatentMask(long[] wavLengths)
    {
        int latentSize = _baseChunkSize * _chunkCompressFactor;
        var latentLengths = wavLengths.Select(len => (len + latentSize - 1) / latentSize).ToArray();
        return LengthToMask(latentLengths);
    }

    private static float[][][] LengthToMask(long[] lengths)
    {
        long maxLen = lengths.Max();
        var mask = new float[lengths.Length][][];
        for (int i = 0; i < lengths.Length; i++)
        {
            mask[i] = new float[1][];
            mask[i][0] = new float[maxLen];
            for (int j = 0; j < maxLen; j++)
                mask[i][0][j] = j < lengths[i] ? 1.0f : 0.0f;
        }
        return mask;
    }

    private static List<string> ChunkText(string text, string lang)
    {
        int maxLen = (lang == "ko" || lang == "ja") ? 120 : 300;
        if (text.Length <= maxLen)
            return new List<string> { text };

        var chunks = new List<string>();
        var paragraphRegex = s_paragraphRegex;
        var paragraphs = paragraphRegex.Split(text.Trim())
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList();

        var sentenceRegex = s_sentenceRegex;
        foreach (var paragraph in paragraphs)
        {
            var sentences = sentenceRegex.Split(paragraph);
            string current = "";
            foreach (var sentence in sentences)
            {
                if (string.IsNullOrEmpty(sentence)) continue;
                if (current.Length + sentence.Length + 1 <= maxLen)
                {
                    current = string.IsNullOrEmpty(current) ? sentence : current + " " + sentence;
                }
                else
                {
                    if (!string.IsNullOrEmpty(current)) chunks.Add(current.Trim());
                    current = sentence;
                }
            }
            if (!string.IsNullOrEmpty(current)) chunks.Add(current.Trim());
        }

        return chunks.Count > 0 ? chunks : new List<string> { text.Trim() };
    }

    private static DenseTensor<float> ArrayToTensor(float[][][] array, long[] dims)
    {
        var flat = new List<float>();
        foreach (var batch in array)
            foreach (var row in batch)
                flat.AddRange(row);
        return new DenseTensor<float>(flat.ToArray(), dims.Select(x => (int)x).ToArray());
    }

    private static DenseTensor<long> IntArrayToTensor(long[][] array, long[] dims)
    {
        var flat = new List<long>();
        foreach (var row in array)
            flat.AddRange(row);
        return new DenseTensor<long>(flat.ToArray(), dims.Select(x => (int)x).ToArray());
    }

    private static byte[] FloatSamplesToWavBytes(float[] audioData, int sampleRate)
    {
        int numChannels = 1;
        int bitsPerSample = 16;
        int byteRate = sampleRate * numChannels * bitsPerSample / 8;
        short blockAlign = (short)(numChannels * bitsPerSample / 8);
        int dataSize = audioData.Length * bitsPerSample / 8;

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)numChannels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write((short)bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);
        foreach (var sample in audioData)
        {
            float clamped = Math.Max(-1.0f, Math.Min(1.0f, sample));
            writer.Write((short)(clamped * 32767));
        }
        writer.Flush();
        return ms.ToArray();
    }

    public void Dispose()
    {
        _dpOrt.Dispose();
        _textEncOrt.Dispose();
        _vectorEstOrt.Dispose();
        _vocoderOrt.Dispose();
    }
}

public record SupertonicOnnxTtsConfig
{
    public string OnnxDir { get; init; } = "assets/onnx";
    public string VoiceStyleDir { get; init; } = "assets/voice_styles";
    public string DefaultVoice { get; init; } = "M1";
    public int TotalSteps { get; init; } = 8;
    public float Speed { get; init; } = 1.05f;
    public float SilenceDuration { get; init; } = 0.3f;
}

public static class Languages
{
    public static readonly string[] Available =
    {
        "en", "ko", "ja", "ar", "bg", "cs", "da", "de", "el", "es", "et",
        "fi", "fr", "hi", "hr", "hu", "id", "it", "lt", "lv", "nl", "pl",
        "pt", "ro", "ru", "sk", "sl", "sv", "tr", "uk", "vi", "na"
    };
}

internal sealed class Style
{
    public float[] Ttl { get; }
    public long[] TtlShape { get; }
    public float[] Dp { get; }
    public long[] DpShape { get; }

    private Style(float[] ttl, long[] ttlShape, float[] dp, long[] dpShape)
    {
        Ttl = ttl; TtlShape = ttlShape; Dp = dp; DpShape = dpShape;
    }

    public static Style FromJsonFile(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var ttlDims = ParseInt64Array(root.GetProperty("style_ttl").GetProperty("dims"));
        var dpDims = ParseInt64Array(root.GetProperty("style_dp").GetProperty("dims"));

        var ttlData3D = ParseFloat3DArray(root.GetProperty("style_ttl").GetProperty("data"));
        var ttlFlat = new List<float>();
        foreach (var b in ttlData3D) foreach (var r in b) ttlFlat.AddRange(r);

        var dpData3D = ParseFloat3DArray(root.GetProperty("style_dp").GetProperty("data"));
        var dpFlat = new List<float>();
        foreach (var b in dpData3D) foreach (var r in b) dpFlat.AddRange(r);

        var ttlShape = new long[] { ttlDims[0], ttlDims[1], ttlDims[2] };
        var dpShape = new long[] { dpDims[0], dpDims[1], dpDims[2] };
        return new Style(ttlFlat.ToArray(), ttlShape, dpFlat.ToArray(), dpShape);
    }

    private static float[][][] ParseFloat3DArray(JsonElement el)
    {
        var result = new List<float[][]>();
        foreach (var batch in el.EnumerateArray())
        {
            var b2 = new List<float[]>();
            foreach (var row in batch.EnumerateArray())
            {
                var rd = new List<float>();
                foreach (var v in row.EnumerateArray())
                    rd.Add(v.GetSingle());
                b2.Add(rd.ToArray());
            }
            result.Add(b2.ToArray());
        }
        return result.ToArray();
    }

    private static long[] ParseInt64Array(JsonElement el)
    {
        var list = new List<long>();
        foreach (var v in el.EnumerateArray())
            list.Add(v.GetInt64());
        return list.ToArray();
    }
}

internal sealed class UnicodeIndexer
{
    private readonly Dictionary<int, long> _indexer;

    public UnicodeIndexer(string path)
    {
        var json = File.ReadAllText(path);
        var arr = JsonSerializer.Deserialize<long[]>(json) ?? Array.Empty<long>();
        _indexer = new Dictionary<int, long>();
        for (int i = 0; i < arr.Length; i++)
            _indexer[i] = arr[i];
    }

    public (long[][] textIds, float[][][] textMask) Process(List<string> textList, List<string> langList)
    {
        var processed = textList.Select((t, i) => PreprocessText(t, langList[i])).ToList();
        var textIdsLengths = processed.Select(t => (long)t.Length).ToArray();
        long maxLen = textIdsLengths.Length > 0 ? textIdsLengths.Max() : 0;

        var textIds = new long[textList.Count][];
        for (int i = 0; i < processed.Count; i++)
        {
            textIds[i] = new long[maxLen];
            var chars = processed[i];
            for (int j = 0; j < chars.Length; j++)
            {
                var charVal = char.IsHighSurrogate(chars[j]) && j + 1 < chars.Length && char.IsLowSurrogate(chars[j + 1])
                    ? char.ConvertToUtf32(chars[j], chars[j + 1])
                    : (int)chars[j];

                if (_indexer.TryGetValue(charVal, out var val))
                    textIds[i][j] = val;
            }
        }

        var textMask = LengthToMask(textIdsLengths);
        return (textIds, textMask);
    }

    private static string PreprocessText(string text, string lang)
    {
        text = text.Normalize(NormalizationForm.FormKD);
        text = RemoveEmojis(text);

        var replacements = new Dictionary<string, string>
        {
            {"\u2013", "-"}, {"\u2014", "-"}, {"\u2011", "-"},
            {"_", " "}, {"\u201C", "\""}, {"\u201D", "\""},
            {"\u2018", "'"}, {"\u2019", "'"},
            {"[", " "}, {"]", " "}, {"|", " "}, {"/", " "}, {"#", " "},
            {"\u2192", " "}, {"\u2190", " "}, {"\u00B4", "'"}, {"`", "'"}
        };
        foreach (var kvp in replacements)
            text = text.Replace(kvp.Key, kvp.Value);

        text = Regex.Replace(text, @"[\u2665\u2606\u2661\u00A9\\]", "");
        text = text.Replace("@", " at ").Replace("e.g.,", "for example, ").Replace("i.e.,", "that is, ");

        text = Regex.Replace(text, @" ,", ",");
        text = Regex.Replace(text, @" \.", ".");
        text = Regex.Replace(text, @" !", "!");
        text = Regex.Replace(text, @" \?", "?");
        text = Regex.Replace(text, @" ;", ";");
        text = Regex.Replace(text, @" :", ":");

        while (text.Contains("\"\"")) text = text.Replace("\"\"", "\"");
        while (text.Contains("''")) text = text.Replace("''", "'");
        text = Regex.Replace(text, @"\s+", " ").Trim();

        if (!Regex.IsMatch(text, @"[.!?;:,'\u0022\u201C\u201D\u2018\u2019)\]}…。」』】〉》›»]$"))
            text += ".";

        if (!Languages.Available.Contains(lang))
            throw new ArgumentException($"Invalid language: {lang}");

        return $"<{lang}>{text}</{lang}>";
    }

    private static string RemoveEmojis(string text)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            int cp = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])
                ? char.ConvertToUtf32(text[i], text[i + 1])
                : text[i];

            bool isEmoji = (cp >= 0x1F600 && cp <= 0x1FAFF) || (cp >= 0x2600 && cp <= 0x27BF) || (cp >= 0x1F1E6 && cp <= 0x1F1FF);
            if (!isEmoji)
                sb.Append(cp > 0xFFFF ? char.ConvertFromUtf32(cp) : ((char)cp).ToString());

            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                i++;
        }
        return sb.ToString();
    }

    private static float[][][] LengthToMask(long[] lengths)
    {
        long maxLen = lengths.Length > 0 ? lengths.Max() : 0;
        var mask = new float[lengths.Length][][];
        for (int i = 0; i < lengths.Length; i++)
        {
            mask[i] = new float[1][];
            mask[i][0] = new float[maxLen];
            for (int j = 0; j < maxLen; j++)
                mask[i][0][j] = j < lengths[i] ? 1.0f : 0.0f;
        }
        return mask;
    }
}
