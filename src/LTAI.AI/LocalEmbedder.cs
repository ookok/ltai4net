// Copyright (c) LTAI. All rights reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace LTAI.AI;

/// <summary>
/// Local ONNX-based sentence embedding using configurable embedding models.
/// Supports runtime model switching, downloading, deletion, and listing.
/// Falls back gracefully if no model file is found (Available = false).
///
/// <b>Consumers:</b> EmbeddingClient (used as local fallback when API embedder unavailable).
/// Registered in AddLTAIAI() as singleton.
/// </summary>
public sealed class LocalEmbedder : IDisposable
{
    private const int MaxLength = 512;
    private const int DefaultDimension = 384;
    private static readonly System.Text.RegularExpressions.Regex WhitespaceRegex = new(@"\s+", System.Text.RegularExpressions.RegexOptions.Compiled);

    private InferenceSession? _session;
    private Dictionary<string, int>? _vocab;
    private string? _modelPath;
    private string? _vocabPath;
    private string? _currentModelName;
    private int _actualDimension = DefaultDimension;
    private bool _loadAttempted;
    private bool _disposed;
    private readonly object _loadLock = new();
    private string? _activeExecutionProvider; // P13.2 telemetry
    private bool _usingQuantizedModel;        // P13.1 telemetry

    /// <summary>
    /// P13.1 + P13.2: configuration for local ONNX model loading. Set globally
    /// before <see cref="AddLTAIAI"/> resolves <see cref="LocalEmbedder"/>, or
    /// per-instance via the <see cref="LocalEmbedder(EmbeddingOptions)"/>
    /// constructor. When unset, defaults are <c>Gpu = auto</c> +
    /// <c>Quantization = auto</c>.
    /// </summary>
    public static EmbeddingOptions Options { get; set; } = new();

    /// <summary>P13.2: name of the active execution provider (after load): <c>DML</c> / <c>CUDA</c> / <c>CPU</c>. Null until first load.</summary>
    public string? ActiveExecutionProvider => _activeExecutionProvider;

    /// <summary>P13.1: true if the loaded model is the INT8/UINT8 quantized variant.</summary>
    public bool UsingQuantizedModel => _usingQuantizedModel;

    /// <summary>Known ONNX models with download URLs.</summary>
    /// <remarks>
    /// P13.1: <see cref="ModelInfo.QuantizedModelUrl"/> points to an INT8/UINT8
    /// quantized variant on HuggingFace when available. MiniLM ships with
    /// multiple quantized versions; BGE does not (yet) so the field is null
    /// and the local model stays at FP32. Quantized models are ~4× smaller on
    /// disk + RAM and ~2-3× faster inference on modern CPUs (AVX2/AVX-512 with
    /// VNNI). When <see cref="EmbeddingOptions.Quantization"/> is <c>auto</c>
    /// (default) and a quantized file is present, the loader prefers it.
    /// </remarks>
    public static readonly Dictionary<string, ModelInfo> KnownModels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["minilm-l6-v2"] = new(
            DisplayName: "all-MiniLM-L6-v2",
            Description: "384维 英文通用 (推荐)",
            ModelUrl: "https://hf-mirror.com/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx",
            VocabUrl: "https://hf-mirror.com/sentence-transformers/all-MiniLM-L6-v2/resolve/main/vocab.txt",
            QuantizedModelUrl: "https://hf-mirror.com/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model_qint8_avx512_vnni.onnx",
            QuantizedFileName: "model.int8.onnx",
            Dimension: 384
        ),
        ["bge-small-zh"] = new(
            DisplayName: "BAAI/bge-small-zh-v1.5",
            Description: "384维 中文通用",
            ModelUrl: "https://hf-mirror.com/BAAI/bge-small-zh-v1.5/resolve/main/onnx/model.onnx",
            VocabUrl: "https://hf-mirror.com/BAAI/bge-small-zh-v1.5/resolve/main/vocab.txt",
            QuantizedModelUrl: null,
            QuantizedFileName: null,
            Dimension: 384
        ),
        ["bge-small-en"] = new(
            DisplayName: "BAAI/bge-small-en-v1.5",
            Description: "384维 英文通用",
            ModelUrl: "https://hf-mirror.com/BAAI/bge-small-en-v1.5/resolve/main/onnx/model.onnx",
            VocabUrl: "https://hf-mirror.com/BAAI/bge-small-en-v1.5/resolve/main/vocab.txt",
            QuantizedModelUrl: null,
            QuantizedFileName: null,
            Dimension: 384
        ),
    };

    /// <summary>
    /// P12.3: Global disable flag. When <c>true</c>, the embedder ctor skips
    /// model detection + eager pre-warm; <see cref="Available"/> always returns
    /// <c>false</c>. Set by <c>AddLTAIAI()</c> when any remote embedding API
    /// key is present (to avoid wasting 200 MB RAM + 5-10 s cold start on a
    /// model the user won't use). Defaults to <c>false</c> for offline / no-key
    /// deployments.
    /// </summary>
    public static bool DefaultDisabled { get; set; }

    /// <summary>Whether a model is loaded and available. Triggers lazy load on first check.</summary>
    public bool Available
    {
        get
        {
            if (DefaultDisabled) return false;
            if (!_loadAttempted) EnsureLoaded();
            return _session != null;
        }
    }

    /// <summary>Eagerly load the model on a background thread. Safe to call multiple times.</summary>
    public async Task PreWarmAsync()
    {
        if (DefaultDisabled || _loadAttempted) return;
        await Task.Run(() => EnsureLoaded()).ConfigureAwait(false);
    }

    /// <summary>Actual embedding dimension of the loaded model.</summary>
    public int Dim => _actualDimension;

    /// <summary>Name of the currently active model (directory name, e.g. "minilm-l6-v2").</summary>
    public string? CurrentModelName => _currentModelName;

    /// <summary>Base directory containing model subdirectories.</summary>
    public static string? BaseModelsDirectory { get; private set; }

    // Special tokens for BERT
    private const int ClsTokenId = 101;
    private const int SepTokenId = 102;
    private const int PadTokenId = 0;
    private const int UnkTokenId = 100;

    /// <summary>
    /// Initialize embedder. Auto-detects the models directory and current model.
    /// Model is loaded lazily on first use. Skips model detection + pre-warm
    /// when <see cref="DefaultDisabled"/> is <c>true</c> (P12.3: remote API
    /// available, no need for local).
    /// </summary>
    public LocalEmbedder() : this(null) { }

    /// <summary>
    /// P13.1 + P13.2: initialize with explicit options (overrides the static
    /// <see cref="Options"/>). Use the parameterless ctor for global config.
    /// </summary>
    public LocalEmbedder(EmbeddingOptions? options)
    {
        if (options != null) Options = options;
        if (DefaultDisabled)
        {
            // P12.3: remote embedding API will be used; don't waste RAM/CPU
            // on a 90 MB model we won't touch. Available returns false.
            return;
        }
        BaseModelsDirectory ??= FindBaseModelsDirectory();
        var detected = DetectCurrentModelWithQuant();
        _currentModelName = detected.name;
        _modelPath = detected.modelPath;
        _vocabPath = detected.vocabPath;
        _usingQuantizedModel = detected.usingQuant;
        // Eager pre-warm on background thread to avoid blocking first use
        _ = PreWarmAsync();
    }

    private static (string? name, string? modelPath, string? vocabPath, bool usingQuant) DetectCurrentModelWithQuant()
    {
        var baseDir = BaseModelsDirectory;
        if (baseDir == null || !Directory.Exists(baseDir)) return (null, null, null, false);
        foreach (var subDir in Directory.GetDirectories(baseDir))
        {
            var name = Path.GetFileName(subDir);
            var (modelFile, vocabFile, usingQuant) = ResolveModelFiles(subDir, name);
            if (modelFile != null && vocabFile != null)
                return (name, modelFile, vocabFile, usingQuant);
        }
        return (null, null, null, false);
    }

    private void EnsureLoaded()
    {
        if (_loadAttempted) return;
        lock (_loadLock)
        {
            if (_loadAttempted) return;
            _loadAttempted = true;
            if (_modelPath == null || _vocabPath == null) return;
            try
            {
                var opts = new SessionOptions();
                opts.ExecutionMode = ExecutionMode.ORT_PARALLEL;
                opts.IntraOpNumThreads = Environment.ProcessorCount;
                opts.InterOpNumThreads = 2;
                if (Options.EnableGraphOptimization)
                {
                    opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                }

                // P13.2: probe execution providers in priority order, fall
                // back gracefully. Avoids the previous code's pattern of
                // always appending all three (DML + CUDA + CPU) which masked
                // which EP actually ran. We track the chosen EP via
                // _activeExecutionProvider for telemetry (P9 DevUI dashboard).
                var gpuPref = (Options.Gpu ?? "auto").ToLowerInvariant();
                if (gpuPref is "dml" or "auto" && TryAppendDml(opts, Options.DeviceId, out var dmlErr))
                {
                    _activeExecutionProvider = "DML";
                }
                else if (gpuPref is "cuda" or "auto" && TryAppendCuda(opts, Options.DeviceId, out var cudaErr))
                {
                    _activeExecutionProvider = "CUDA";
                }
                else
                {
                    if (gpuPref is "dml" || gpuPref is "cuda")
                    {
                        // User explicitly requested a GPU EP that wasn't available
                        throw new InvalidOperationException(
                            $"LocalEmbedder: requested GPU='{gpuPref}' but provider unavailable. " +
                            (gpuPref == "dml" ? "DirectML requires Windows 10+ with WDDM 2.0+ GPU drivers." :
                             "CUDA requires NVIDIA GPU + CUDA toolkit + cuDNN runtime."));
                    }
                    _activeExecutionProvider = "CPU";
                }

                // CPU fallback is always available; safe to append after GPU EP
                opts.AppendExecutionProvider_CPU();
                _session = new InferenceSession(_modelPath, opts);

                // Detect actual dimension from model metadata
                try { _actualDimension = _session.InputMetadata["input_ids"].Dimensions[^1]; }
                catch { _actualDimension = DefaultDimension; }

                _vocab = LoadVocab(_vocabPath);
            }
            catch
            {
                _session = null;
                _vocab = null;
                _activeExecutionProvider = null;
            }
        }
    }

    /// <summary>P13.2: try to attach DirectML EP; returns true on success.</summary>
    private static bool TryAppendDml(SessionOptions opts, int deviceId, out Exception? err)
    {
        err = null;
        try
        {
            // DirectML API: AppendExecutionProvider_DML(int deviceId)
            opts.AppendExecutionProvider_DML(deviceId);
            return true;
        }
        catch (Exception ex)
        {
            err = ex;
            return false;
        }
    }

    /// <summary>P13.2: try to attach CUDA EP via reflection (CUDA package optional).</summary>
    private static bool TryAppendCuda(SessionOptions opts, int deviceId, out Exception? err)
    {
        err = null;
        try
        {
            // CUDA EP requires Microsoft.ML.OnnxRuntime.Gpu package + native
            // CUDA libraries. If the package is not referenced, this throws
            // TypeLoadException / EntryPointNotFoundException at runtime.
            opts.AppendExecutionProvider_CUDA(deviceId);
            return true;
        }
        catch (Exception ex)
        {
            err = ex;
            return false;
        }
    }

    /// <summary>
    /// Generate embedding vector for the given text.
    /// Uses sliding window for texts exceeding 512 tokens
    /// (window=510, stride=256, 50% overlap), mean-pooling across chunks.
    /// </summary>
    public float[] Generate(string text)
    {
        var (session, vocab) = GetLoadedModel();
        if (session == null || vocab == null)
            throw new InvalidOperationException(
                "LocalEmbedder not available. Use /model download to download an embedding model.");

        var normalized = NormalizeText(text);
        var words = SplitWords(normalized);

        // Collect all raw pieces without [CLS]/[SEP], no truncation
        var allPieces = new List<string>();
        foreach (var word in words)
            allPieces.AddRange(WordPiece(word, vocab));

        int totalWithSpecials = allPieces.Count + 2; // + [CLS] + [SEP]
        if (totalWithSpecials <= MaxLength)
        {
            // Short text: single pass, existing fast path
            var tokens = BuildTokens(allPieces, 0, allPieces.Count, vocab);
            return L2Normalize(EmbedTokens(session, tokens));
        }

        // Long text: sliding window with 50% overlap
        const int window = MaxLength - 2; // room for [CLS] and [SEP]
        const int stride = 256;
        var chunkEmbs = new List<float[]>();

        for (int start = 0; start < allPieces.Count; start += stride)
        {
            int end = Math.Min(start + window, allPieces.Count);
            var tokens = BuildTokens(allPieces, start, end, vocab);
            var pooled = EmbedTokens(session, tokens);
            chunkEmbs.Add(pooled);
        }

        // Mean-pool across all chunks, then L2 normalize
        var result = new float[DefaultDimension];
        foreach (var emb in chunkEmbs)
            for (int i = 0; i < DefaultDimension; i++)
                result[i] += emb[i];
        for (int i = 0; i < DefaultDimension; i++)
            result[i] /= chunkEmbs.Count;

        return L2Normalize(result);
    }

    /// <summary>
    /// P11.1a: Batched embedding — N texts in 1 session.Run.
    /// 5-10x throughput vs single-text calls because:
    ///   - 1 native call vs N
    ///   - ONNX runtime amortizes graph setup, allocator warmup
    ///   - GPU exec providers (DML/CUDA) prefer large batches
    /// Each text is tokenized with [CLS]/[SEP] and padded to the batch's max
    /// sequence length (capped at <see cref="MaxLength"/>). Texts exceeding
    /// MaxLength are truncated to MaxLength-1 tokens + [SEP] (sliding window
    /// is not applied in batch mode — would require multi-pass with the same
    /// complication; call <see cref="Generate"/> on long texts if needed).
    /// Returns L2-normalized vectors in the same order as the input.
    /// </summary>
    public IReadOnlyList<float[]> GenerateBatch(IReadOnlyList<string> texts)
    {
        if (texts.Count == 0) return Array.Empty<float[]>();
        if (texts.Count == 1) return new[] { Generate(texts[0]) };

        var (session, vocab) = GetLoadedModel();
        if (session == null || vocab == null)
        {
            throw new InvalidOperationException(
                "LocalEmbedder not available. Use /model download to download an embedding model.");
        }

        // Tokenize each text (returns list of token IDs with attention mask; padded to MaxLength)
        var perTextTokens = new List<Token[]>(texts.Count);
        int actualMaxLen = 0;
        for (int i = 0; i < texts.Count; i++)
        {
            var t = texts[i];
            var toks = TokenizeToIds(t, vocab);
            // Find true length (first pad) so we can size the batch tensor tightly
            int trueLen = toks.Length;
            while (trueLen > 0 && toks[trueLen - 1].InputId == PadTokenId) trueLen--;
            if (trueLen > actualMaxLen) actualMaxLen = trueLen;
            perTextTokens.Add(toks);
        }
        if (actualMaxLen == 0) actualMaxLen = 1; // safety

        // Build batched tensors [N, actualMaxLen]
        var inputIds = new DenseTensor<long>(new[] { texts.Count, actualMaxLen });
        var attentionMask = new DenseTensor<long>(new[] { texts.Count, actualMaxLen });
        var tokenTypeIds = new DenseTensor<long>(new[] { texts.Count, actualMaxLen });

        for (int i = 0; i < texts.Count; i++)
        {
            var t = perTextTokens[i];
            for (int j = 0; j < actualMaxLen; j++)
            {
                if (j < t.Length)
                {
                    inputIds[i, j] = t[j].InputId;
                    attentionMask[i, j] = t[j].AttentionMask;
                }
                else
                {
                    inputIds[i, j] = PadTokenId;
                    attentionMask[i, j] = 0;
                }
                tokenTypeIds[i, j] = 0;
            }
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
            NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds),
        };

        using var results = session.Run(inputs);
        var output = results.First().AsTensor<float>();
        // output dimensions: [N, actualMaxLen, hiddenDim]
        int hiddenDim = output.Dimensions[2];

        // Mean-pool per row using the actual attention mask, then L2 normalize
        var embeddings = new float[texts.Count][];
        for (int i = 0; i < texts.Count; i++)
        {
            var pooled = new float[hiddenDim];
            int validTokens = 0;
            for (int j = 0; j < actualMaxLen; j++)
            {
                if (attentionMask[i, j] == 0) continue;
                validTokens++;
                for (int k = 0; k < hiddenDim; k++)
                    pooled[k] += output[i, j, k];
            }
            if (validTokens > 0)
            {
                for (int k = 0; k < hiddenDim; k++)
                    pooled[k] /= validTokens;
            }
            embeddings[i] = L2Normalize(pooled);
        }
        return embeddings;
    }

    /// <summary>Tokenize text to a fixed-length Token[] (padded to MaxLength).</summary>
    private Token[] TokenizeToIds(string text, Dictionary<string, int> vocab)
    {
        var normalized = NormalizeText(text);
        var words = SplitWords(normalized);
        var pieces = new List<string>(MaxLength) { "[CLS]" };
        foreach (var word in words)
        {
            pieces.AddRange(WordPiece(word, vocab));
            if (pieces.Count >= MaxLength - 1) break;
        }
        pieces.Add("[SEP]");
        if (pieces.Count > MaxLength)
        {
            pieces = pieces.Take(MaxLength - 1).ToList();
            pieces.Add("[SEP]");
        }
        var tokens = new Token[MaxLength];
        for (int i = 0; i < pieces.Count; i++)
        {
            tokens[i] = new Token(vocab.GetValueOrDefault(pieces[i], UnkTokenId), 1);
        }
        for (int i = pieces.Count; i < MaxLength; i++)
        {
            tokens[i] = new Token(PadTokenId, 0);
        }
        return tokens;
    }

    /// <summary>Build padded token list for a range of raw pieces.</summary>
    private List<Token> BuildTokens(List<string> allPieces, int start, int end, Dictionary<string, int> vocab)
    {
        var tokens = new List<Token>(MaxLength);
        // [CLS]
        tokens.Add(new Token(vocab.GetValueOrDefault("[CLS]", ClsTokenId), 1));
        for (int i = start; i < end; i++)
        {
            var id = vocab.GetValueOrDefault(allPieces[i], UnkTokenId);
            tokens.Add(new Token(id, 1));
        }
        // [SEP]
        tokens.Add(new Token(vocab.GetValueOrDefault("[SEP]", SepTokenId), 1));
        // Pad
        while (tokens.Count < MaxLength)
            tokens.Add(new Token(PadTokenId, 0));
        return tokens;
    }

    /// <summary>Run ONNX inference and return mean-pooled embedding.</summary>
    private float[] EmbedTokens(InferenceSession session, List<Token> tokens)
    {
        var inputIds = new DenseTensor<long>(new[] { 1, tokens.Count });
        var attentionMask = new DenseTensor<long>(new[] { 1, tokens.Count });
        var tokenTypeIds = new DenseTensor<long>(new[] { 1, tokens.Count });

        for (int i = 0; i < tokens.Count; i++)
        {
            inputIds[0, i] = tokens[i].InputId;
            attentionMask[0, i] = tokens[i].AttentionMask;
            tokenTypeIds[0, i] = 0;
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
            NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds),
        };

        using var results = session.Run(inputs);
        var embedding = results.First().AsTensor<float>();
        return MeanPool(embedding, attentionMask);
    }

    // ═══════════════════════════════════════════
    //  BERT WordPiece Tokenizer
    // ═══════════════════════════════════════════

    private List<Token> Tokenize(string text, Dictionary<string, int> vocab)
    {
        // Normalize: lowercase for CJK mixed text, collapse whitespace
        var normalized = NormalizeText(text);
        var words = SplitWords(normalized);
        var pieces = new List<string>();

        pieces.Add("[CLS]");

        foreach (var word in words)
        {
            var wordPieces = WordPiece(word, vocab);
            pieces.AddRange(wordPieces);

            if (pieces.Count >= MaxLength - 1) break;
        }

        pieces.Add("[SEP]");

        // Truncate if needed
        if (pieces.Count > MaxLength)
        {
            pieces = pieces.Take(MaxLength - 1).ToList();
            pieces.Add("[SEP]");
        }

        // Create tokens with attention mask
        var tokens = new List<Token>();
        foreach (var piece in pieces)
        {
            var id = vocab.GetValueOrDefault(piece, UnkTokenId);
            tokens.Add(new Token(id, 1));
        }

        // Pad to MaxLength
        while (tokens.Count < MaxLength)
            tokens.Add(new Token(PadTokenId, 0));

        return tokens;
    }

    private static string NormalizeText(string text)
    {
        // For BGE: keep original casing (BGE preserves case for code/English terms)
        // Replace common whitespace variants
        text = text.Replace('\r', ' ')
                   .Replace('\n', ' ')
                   .Replace('\t', ' ');
        // Collapse multiple spaces
        text = WhitespaceRegex.Replace(text, " ");
        return text.Trim();
    }

    private static List<string> SplitWords(string text)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();

        foreach (char c in text)
        {
            if (c == ' ')
            {
                if (current.Length > 0)
                {
                    words.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            // CJK characters are treated as individual words
            if (IsCjk(c))
            {
                if (current.Length > 0)
                {
                    words.Add(current.ToString());
                    current.Clear();
                }
                words.Add(c.ToString());
            }
            else
            {
                // Punctuation splits words
                if (char.IsPunctuation(c) && current.Length > 0)
                {
                    words.Add(current.ToString());
                    current.Clear();
                }
                current.Append(c);
            }
        }

        if (current.Length > 0)
            words.Add(current.ToString());

        return words;
    }

    private List<string> WordPiece(string word, Dictionary<string, int> vocab)
    {
        if (vocab.ContainsKey(word))
            return [word];

        var pieces = new List<string>();
        var chars = word.ToCharArray();
        int start = 0;

        while (start < chars.Length)
        {
            int end = chars.Length;
            string? found = null;

            while (end > start)
            {
                var sub = start == 0
                    ? new string(chars[start..end])
                    : "##" + new string(chars[start..end]);

                if (vocab.ContainsKey(sub))
                {
                    found = sub;
                    break;
                }
                end--;
            }

            if (found != null)
            {
                pieces.Add(found);
                start += found.StartsWith("##") ? found.Length - 2 : found.Length;
            }
            else
            {
                // Unknown character — use [UNK]
                pieces.Add("[UNK]");
                start++;
            }
        }

        return pieces;
    }

    private static bool IsCjk(char c) =>
        (c >= 0x4E00 && c <= 0x9FFF) ||  // CJK Unified Ideographs
        (c >= 0x3400 && c <= 0x4DBF) ||  // CJK Extension A
        (c >= 0x2E80 && c <= 0x2EFF) ||  // CJK Radicals
        (c >= 0x3000 && c <= 0x303F) ||  // CJK Symbols
        (c >= 0xFF00 && c <= 0xFFEF);     // Fullwidth

    // ═══════════════════════════════════════════
    //  Pooling & Normalization
    // ═══════════════════════════════════════════

    private static float[] MeanPool(Tensor<float> embedding, Tensor<long> attentionMask)
    {
        int batchSize = embedding.Dimensions[0];   // 1
        int seqLen = embedding.Dimensions[1];      // 512
        int hiddenDim = embedding.Dimensions[2];   // e.g. 768 (BGE) or 384 (MiniLM)

        // Runtime dimension check: warn if model output differs from expected default
        if (hiddenDim != DefaultDimension)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[LocalEmbedder] WARNING: model outputs {hiddenDim}-dim but target is {DefaultDimension}; " +
                $"{(hiddenDim > DefaultDimension ? "truncating" : "padding")} to {DefaultDimension}.");
        }

        var result = new float[DefaultDimension];

        // Mean pool: average over sequence length for non-padding tokens
        float[] sum = new float[hiddenDim];
        int count = 0;

        for (int j = 0; j < seqLen; j++)
        {
            if (attentionMask[0, j] == 0) continue;
            count++;
            for (int k = 0; k < hiddenDim; k++)
                sum[k] += embedding[0, j, k];
        }

        if (count > 0)
        {
            for (int k = 0; k < hiddenDim; k++)
                sum[k] /= count;
        }

        // Take first N dimensions (target fixed size)
        Array.Copy(sum, result, Math.Min(hiddenDim, DefaultDimension));
        return result;
    }

    private static float[] L2Normalize(float[] vec)
    {
        float norm = 0;
        foreach (var v in vec) norm += v * v;
        norm = MathF.Sqrt(norm);
        if (norm < 1e-12f) return vec;
        for (int i = 0; i < vec.Length; i++)
            vec[i] /= norm;
        return vec;
    }

    // ═══════════════════════════════════════════
    //  Vocab loader
    // ═══════════════════════════════════════════

    private static Dictionary<string, int> LoadVocab(string path)
    {
        var vocab = new Dictionary<string, int>();
        int idx = 0;
        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
                vocab[trimmed] = idx++;
        }
        return vocab;
    }

    // ═══════════════════════════════════════════
    //  Model file discovery
    // ═══════════════════════════════════════════

    private static string? FindBaseModelsDirectory()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "models"),
            Path.Combine(Directory.GetCurrentDirectory(), "models"),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "models")),
        ];
        foreach (var dir in candidates)
            if (Directory.Exists(dir)) return Path.GetFullPath(dir);

        var fallback = Path.Combine(AppContext.BaseDirectory, "models");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    private static (string? name, string? modelPath, string? vocabPath) DetectCurrentModel()
    {
        var baseDir = BaseModelsDirectory;
        if (baseDir == null || !Directory.Exists(baseDir))
            return (null, null, null);

        foreach (var subDir in Directory.GetDirectories(baseDir))
        {
            var name = Path.GetFileName(subDir);
            // P13.1: if quantization is on and a quantized file exists, prefer it
            var (modelFile, vocabFile, usingQuant) = ResolveModelFiles(subDir, name);
            if (modelFile != null && vocabFile != null)
            {
                // Mark telemetry on first detection (subsequent SwitchModel may override)
                return (name, modelFile, vocabFile);
            }
        }
        return (null, null, null);
    }

    /// <summary>
    /// P13.1: pick the on-disk model file based on <see cref="EmbeddingOptions.Quantization"/>.
    /// Returns (modelPath, vocabPath, usingQuantized) — any may be null if not present.
    /// </summary>
    private static (string? modelPath, string? vocabPath, bool usingQuant) ResolveModelFiles(string subDir, string modelName)
    {
        var quantPref = (Options.Quantization ?? "auto").ToLowerInvariant();
        var vocabFile = Path.Combine(subDir, "vocab.txt");
        var fp32File = Path.Combine(subDir, "model.onnx");
        var quantFile = KnownModels.TryGetValue(modelName, out var info) && info.QuantizedFileName != null
            ? Path.Combine(subDir, info.QuantizedFileName)
            : null;

        var hasVocab = File.Exists(vocabFile);
        var hasFp32 = File.Exists(fp32File);
        var hasQuant = quantFile != null && File.Exists(quantFile);

        // "int8" hard requirement
        if (quantPref == "int8" && !hasQuant)
        {
            // Will be reported via the model list; don't fail detection
            // here — caller may still want to load FP32. The quantFile path
            // is null in that case.
        }

        var useQuant = (quantPref == "auto" || quantPref == "int8") && hasQuant;
        if (useQuant) return (quantFile!, vocabFile, true);
        if (hasFp32 && hasVocab) return (fp32File, vocabFile, false);
        return (null, null, false);
    }

    private (InferenceSession?, Dictionary<string, int>?) GetLoadedModel()
    {
        EnsureLoaded();
        lock (_loadLock) { return (_session, _vocab); }
    }

    // ═══════════════════════════════════════════
    //  Model management (for TUI slash commands)
    // ═══════════════════════════════════════════

    /// <summary>List all known models with download status.</summary>
    public static List<AvailableModelInfo> ListAvailableModels()
    {
        var result = new List<AvailableModelInfo>();
        foreach (var (id, info) in KnownModels)
        {
            var dir = BaseModelsDirectory != null ? Path.Combine(BaseModelsDirectory, id) : null;
            var downloaded = dir != null
                && File.Exists(Path.Combine(dir, "model.onnx"))
                && File.Exists(Path.Combine(dir, "vocab.txt"));
            var quantDownloaded = downloaded
                && info.QuantizedFileName != null
                && File.Exists(Path.Combine(dir!, info.QuantizedFileName));
            result.Add(new AvailableModelInfo(id, info.DisplayName, info.Description, info.Dimension, downloaded, quantDownloaded));
        }
        return result;
    }

    /// <summary>Switch the active embedding model. Returns true on success.</summary>
    public bool SwitchModel(string name)
    {
        var baseDir = BaseModelsDirectory;
        if (baseDir == null) return false;

        var modelDir = Path.Combine(baseDir, name);
        var (modelFile, vocabFile, usingQuant) = ResolveModelFiles(modelDir, name);
        if (modelFile == null || vocabFile == null) return false;

        lock (_loadLock)
        {
            _session?.Dispose();
            _session = null;
            _vocab = null;
            _loadAttempted = false;
            _currentModelName = name;
            _modelPath = modelFile;
            _vocabPath = vocabFile;
            _usingQuantizedModel = usingQuant;

            EnsureLoaded();
        }
        return _session != null;
    }

    /// <summary>Delete a downloaded model directory. Cannot delete the currently active model.</summary>
    public bool DeleteModel(string name)
    {
        var baseDir = BaseModelsDirectory;
        if (baseDir == null) return false;

        if (string.Equals(_currentModelName, name, StringComparison.OrdinalIgnoreCase))
            return false;

        var modelDir = Path.Combine(baseDir, name);
        if (!Directory.Exists(modelDir)) return false;

        Directory.Delete(modelDir, recursive: true);
        return true;
    }

    /// <summary>Download a known model from HuggingFace mirror.</summary>
    /// <remarks>
    /// P13.1: if the model has a <see cref="ModelInfo.QuantizedModelUrl"/>,
    /// also downloads the INT8/UINT8 variant as <c>model.int8.onnx</c>. A
    /// download failure of the quantized file is non-fatal (FP32 still loads).
    /// </remarks>
    public async Task<bool> DownloadModelAsync(string name, HttpClient? httpClient = null)
    {
        if (!KnownModels.TryGetValue(name, out var info)) return false;

        var baseDir = BaseModelsDirectory;
        if (baseDir == null) return false;

        var modelDir = Path.Combine(baseDir, name);
        Directory.CreateDirectory(modelDir);
        var modelFile = Path.Combine(modelDir, "model.onnx");
        var vocabFile = Path.Combine(modelDir, "vocab.txt");
        var quantFile = info.QuantizedFileName != null
            ? Path.Combine(modelDir, info.QuantizedFileName)
            : null;

        var http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var disposeHttp = httpClient == null;

        try
        {
            using (var resp = await http.GetAsync(info.ModelUrl).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                using var fs = new FileStream(modelFile, FileMode.Create, FileAccess.Write, FileShare.None);
                await resp.Content.CopyToAsync(fs).ConfigureAwait(false);
            }
            using (var resp = await http.GetAsync(info.VocabUrl).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                using var fs = new FileStream(vocabFile, FileMode.Create, FileAccess.Write, FileShare.None);
                await resp.Content.CopyToAsync(fs).ConfigureAwait(false);
            }

            // P13.1: best-effort quantized download; non-fatal on failure
            if (info.QuantizedModelUrl != null && quantFile != null)
            {
                try
                {
                    using var resp = await http.GetAsync(info.QuantizedModelUrl).ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode)
                    {
                        using var fs = new FileStream(quantFile, FileMode.Create, FileAccess.Write, FileShare.None);
                        await resp.Content.CopyToAsync(fs).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // Quantized variant not available upstream — FP32 still works
                }
            }
            return true;
        }
        catch
        {
            if (File.Exists(modelFile)) try { File.Delete(modelFile); } catch { }
            if (File.Exists(vocabFile)) try { File.Delete(vocabFile); } catch { }
            if (quantFile != null && File.Exists(quantFile)) try { File.Delete(quantFile); } catch { }
            return false;
        }
        finally
        {
            if (disposeHttp) http.Dispose();
        }
    }

    // ═══════════════════════════════════════════
    //  DTOs
    // ═══════════════════════════════════════════

    /// <summary>Metadata for a known downloadable model.</summary>
    /// <param name="DisplayName">Human-friendly name shown in TUI / DevUI.</param>
    /// <param name="Description">One-line description (e.g. "384维 英文通用").</param>
    /// <param name="ModelUrl">URL of the FP32 model file on HuggingFace mirror.</param>
    /// <param name="VocabUrl">URL of the vocab.txt tokenizer file.</param>
    /// <param name="QuantizedModelUrl">P13.1: URL of an INT8/UINT8 quantized
    ///   variant when available (e.g. <c>model_qint8_avx512_vnni.onnx</c>).
    ///   Null if the upstream model has no quantized export.</param>
    /// <param name="QuantizedFileName">P13.1: local filename to save the
    ///   quantized model as (e.g. <c>model.int8.onnx</c>). Null when no
    ///   quantized URL is available.</param>
    /// <param name="Dimension">Embedding dimension (e.g. 384).</param>
    public sealed record ModelInfo(
        string DisplayName,
        string Description,
        string ModelUrl,
        string VocabUrl,
        string? QuantizedModelUrl,
        string? QuantizedFileName,
        int Dimension);

    /// <summary>Information about an available (or downloadable) model.</summary>
    public sealed record AvailableModelInfo(string Id, string DisplayName, string Description, int Dimension, bool Downloaded, bool QuantizedDownloaded);

    private readonly record struct Token(long InputId, long AttentionMask);

    public void Dispose()
    {
        if (!_disposed)
        {
            lock (_loadLock) { _session?.Dispose(); }
            _disposed = true;
        }
    }
}
