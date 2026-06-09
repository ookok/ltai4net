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
    ~LocalEmbedder() => Dispose(disposing: false);
    private const int DefaultDimension = 384;
    private static readonly TimeSpan ModelLoadTimeout = TimeSpan.FromSeconds(10);

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
    /// per-instance via the <see cref="LocalEmbedder(EmbeddingOptions)"/> constructor.
    /// When unset, defaults are <c>Gpu = auto</c> + <c>Quantization = auto</c>.
    /// </summary>
    public static EmbeddingOptions Options { get; set; } = new();

    /// <summary>P13.2: name of the active execution provider (after load). Null until first load.</summary>
    public string? ActiveExecutionProvider => _activeExecutionProvider;

    /// <summary>P13.1: true if the loaded model is the INT8/UINT8 quantized variant.</summary>
    public bool UsingQuantizedModel => _usingQuantizedModel;

    /// <summary>
    /// P14.8: Fired by <see cref="SwitchModel"/> after the new model is
    /// successfully loaded (synchronously, while still inside the load lock).
    /// Argument is the new model name. Subscribers should invalidate any caches.
    /// </summary>
    public event Action<string>? ModelSwitched;

    /// <summary>Known ONNX models with download URLs.</summary>
    public static readonly Dictionary<string, ModelInfo> KnownModels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["minilm-l6-v2"] = new(
            DisplayName: "all-MiniLM-L6-v2",
            Description: "384维 英文通用 (推荐)",
            ModelUrl: "https://hf-mirror.com/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx",
            VocabUrl: "https://hf-mirror.com/Xenova/all-MiniLM-L6-v2/resolve/main/vocab.txt",
            QuantizedModelUrl: "https://hf-mirror.com/Xenova/all-MiniLM-L6-v2/resolve/main/onnx/model_int8.onnx",
            QuantizedFileName: "model.int8.onnx",
            Dimension: 384
        ),
        ["bge-small-zh"] = new(
            DisplayName: "BAAI/bge-small-zh-v1.5",
            Description: "384维 中文通用",
            ModelUrl: "https://hf-mirror.com/BAAI/bge-small-zh-v1.5/resolve/main/onnx/model.onnx",
            VocabUrl: "https://hf-mirror.com/Xenova/bge-small-zh-v1.5/resolve/main/vocab.txt",
            QuantizedModelUrl: "https://hf-mirror.com/Xenova/bge-small-zh-v1.5/resolve/main/onnx/model_int8.onnx",
            QuantizedFileName: "model.int8.onnx",
            Dimension: 384
        ),
        ["bge-small-en"] = new(
            DisplayName: "BAAI/bge-small-en-v1.5",
            Description: "384维 英文通用",
            ModelUrl: "https://hf-mirror.com/BAAI/bge-small-en-v1.5/resolve/main/onnx/model.onnx",
            VocabUrl: "https://hf-mirror.com/Xenova/bge-small-en-v1.5/resolve/main/vocab.txt",
            QuantizedModelUrl: "https://hf-mirror.com/Xenova/bge-small-en-v1.5/resolve/main/onnx/model_int8.onnx",
            QuantizedFileName: "model.int8.onnx",
            Dimension: 384
        ),
    };

    /// <summary>
    /// Global disable flag. When true, the embedder ctor skips model detection.
    /// Available always returns false. Set when any remote embedding API key is present.
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

    /// <summary>Name of the currently active model.</summary>
    public string? CurrentModelName => _currentModelName;

    /// <summary>Base directory containing model subdirectories.</summary>
    public static string? BaseModelsDirectory { get; private set; }

    /// <summary>Base URL for model fallback downloads.</summary>
    public static string ModelBaseUrl { get; set; } = "http://mogoo.com.cn/";

    /// <summary>Initialize embedder. Auto-detects the models directory and current model.</summary>
    public LocalEmbedder() : this(null) { }

    /// <summary>Initialize with explicit options.</summary>
    public LocalEmbedder(EmbeddingOptions? options)
    {
        if (options != null) Options = options;
        if (DefaultDisabled) return;
        Activate();
    }

    /// <summary>Late-bind model paths. Idempotent.</summary>
    public void Activate()
    {
        if (_loadAttempted) return;
        BaseModelsDirectory ??= FindBaseModelsDirectory();
        var detected = DetectCurrentModelWithQuant();
        _currentModelName = detected.name;
        _modelPath = detected.modelPath;
        _vocabPath = detected.vocabPath;
        _usingQuantizedModel = detected.usingQuant;
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

    /// <summary>
    /// Load the ONNX model with a timeout to prevent hang.
    /// Uses Task.Run + Wait with timeout to unblock the UI thread.
    /// </summary>
    private void EnsureLoaded()
    {
        if (_loadAttempted) return;
        lock (_loadLock)
        {
            if (_loadAttempted) return;
            if (_modelPath == null || _vocabPath == null) { _loadAttempted = true; return; }

            try
            {
                // Run ONNX model loading on a background thread with timeout
                // to prevent any possibility of blocking the UI thread.
                var loaded = Task.Run(() =>
                {
                    try
                    {
                        var opts = new SessionOptions();
                        opts.ExecutionMode = ExecutionMode.ORT_PARALLEL;
                        opts.IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2);
                        opts.InterOpNumThreads = 2;

                        // Always start with CPU fallback — fastest and most reliable
                        opts.AppendExecutionProvider_CPU();

                        // Try GPU providers only if they're fast to initialize
                        var gpuPref = (Options.Gpu ?? "auto").ToLowerInvariant();
                        if (gpuPref is "dml" or "auto")
                        {
                            try
                            {
                                opts.AppendExecutionProvider_DML(Options.DeviceId);
                                _activeExecutionProvider = "DML";
                            }
                            catch { /* DML not available */ }
                        }
                        if (_activeExecutionProvider == null && (gpuPref is "cuda" or "auto"))
                        {
                            try
                            {
                                opts.AppendExecutionProvider_CUDA(Options.DeviceId);
                                _activeExecutionProvider = "CUDA";
                            }
                            catch { /* CUDA not available */ }
                        }
                        if (_activeExecutionProvider == null)
                            _activeExecutionProvider = "CPU";

                        if (Options.EnableGraphOptimization)
                            opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

                        _session = new InferenceSession(_modelPath, opts);

                        // Detect actual dimension from model metadata
                        try { _actualDimension = _session.InputMetadata["input_ids"].Dimensions[^1]; }
                        catch { _actualDimension = DefaultDimension; }

                        _vocab = LoadVocab(_vocabPath);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        _session = null;
                        _vocab = null;
                        _activeExecutionProvider = null;
                        _loadError = ex;
                        return false;
                    }
                });

                // Wait with timeout — if ONNX loading hangs, we don't block init
                if (!loaded.Wait(ModelLoadTimeout))
                {
                    // Timeout — mark as failed and continue
                    _session = null;
                    _vocab = null;
                    _activeExecutionProvider = null;
                    _loadError = new TimeoutException(
                        $"ONNX model loading timed out after {ModelLoadTimeout.TotalSeconds}s");
                }
            }
            catch (Exception ex)
            {
                _session = null;
                _vocab = null;
                _activeExecutionProvider = null;
                _loadError = ex;
            }
            _loadAttempted = true;
        }
    }

    private Exception? _loadError;
    /// <summary>Last load failure exception. For diagnostics only.</summary>
    public Exception? LastLoadError => _loadError;

    /// <summary>Generate embedding vector for the given text.</summary>
    public float[] Generate(string text)
    {
        var (session, vocab) = GetLoadedModel();
        if (session == null || vocab == null)
            throw new InvalidOperationException(
                "LocalEmbedder not available. Use /model download to download an embedding model.");

        var normalized = BertTokenizer.NormalizeText(text);
        var words = BertTokenizer.SplitWords(normalized);
        var allPieces = new List<string>();
        foreach (var word in words)
            allPieces.AddRange(BertTokenizer.WordPiece(word, vocab));

        int totalWithSpecials = allPieces.Count + 2;
        if (totalWithSpecials <= BertTokenizer.MaxLength)
        {
            var tokens = BertTokenizer.BuildTokens(allPieces, 0, allPieces.Count, vocab);
            return EmbeddingPool.L2Normalize(EmbedTokens(session, tokens));
        }

        const int window = BertTokenizer.MaxLength - 2;
        const int stride = 256;
        var chunkEmbs = new List<float[]>();
        for (int start = 0; start < allPieces.Count; start += stride)
        {
            int end = Math.Min(start + window, allPieces.Count);
            var tokens = BertTokenizer.BuildTokens(allPieces, start, end, vocab);
            var pooled = EmbedTokens(session, tokens);
            chunkEmbs.Add(pooled);
        }

        var result = new float[DefaultDimension];
        foreach (var emb in chunkEmbs)
            for (int i = 0; i < DefaultDimension; i++)
                result[i] += emb[i];
        for (int i = 0; i < DefaultDimension; i++)
            result[i] /= chunkEmbs.Count;
        return EmbeddingPool.L2Normalize(result);
    }

    /// <summary>Batched embedding — N texts in 1 session.Run. 5-10x throughput.</summary>
    public IReadOnlyList<float[]> GenerateBatch(IReadOnlyList<string> texts)
    {
        if (texts.Count == 0) return Array.Empty<float[]>();
        if (texts.Count == 1) return new[] { Generate(texts[0]) };

        var (session, vocab) = GetLoadedModel();
        if (session == null || vocab == null)
            throw new InvalidOperationException(
                "LocalEmbedder not available. Use /model download to download an embedding model.");

        var perTextTokens = new List<Token[]>(texts.Count);
        int actualMaxLen = 0;
        for (int i = 0; i < texts.Count; i++)
        {
            var t = texts[i];
            var toks = BertTokenizer.TokenizeToIds(t, vocab);
            int trueLen = toks.Length;
            while (trueLen > 0 && toks[trueLen - 1].InputId == BertTokenizer.PadTokenId) trueLen--;
            if (trueLen > actualMaxLen) actualMaxLen = trueLen;
            perTextTokens.Add(toks);
        }
        if (actualMaxLen == 0) actualMaxLen = 1;

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
                    inputIds[i, j] = BertTokenizer.PadTokenId;
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
        if (results.Count == 0)
            throw new InvalidOperationException("ONNX inference returned zero output tensors");
        var output = results[0].AsTensor<float>();
        int hiddenDim = output.Dimensions[2];

        var embeddings = new float[texts.Count][];
        var pool = System.Buffers.ArrayPool<float>.Shared;
        var pooledBuf = pool.Rent(hiddenDim);
        try
        {
            for (int i = 0; i < texts.Count; i++)
            {
                Array.Clear(pooledBuf, 0, hiddenDim);
                int validTokens = 0;
                for (int j = 0; j < actualMaxLen; j++)
                {
                    if (attentionMask[i, j] == 0) continue;
                    validTokens++;
                    for (int k = 0; k < hiddenDim; k++)
                        pooledBuf[k] += output[i, j, k];
                }
                if (validTokens > 0)
                {
                    for (int k = 0; k < hiddenDim; k++)
                        pooledBuf[k] /= validTokens;
                }
                var emb = EmbeddingPool.L2NormalizeInPlace(pooledBuf, hiddenDim);
                embeddings[i] = emb;
            }
        }
        finally { pool.Return(pooledBuf); }
        return embeddings;
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
        return EmbeddingPool.MeanPool(embedding, attentionMask, DefaultDimension);
    }

    //  Vocab loader

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

    //  Model file discovery

    private static string? FindBaseModelsDirectory()
    {
        var envDir = Environment.GetEnvironmentVariable("LTAI_EMBEDDING_MODELS_DIR");
        if (!string.IsNullOrEmpty(envDir) && Directory.Exists(envDir))
            return Path.GetFullPath(envDir);

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

    /// <summary>Pick the on-disk model file based on quantization preference.</summary>
    private static (string? modelPath, string? vocabPath, bool usingQuant) ResolveModelFiles(string subDir, string modelName)
    {
        var quantPref = Options.GetQuantizationFor(modelName);
        var vocabFile = Path.Combine(subDir, "vocab.txt");
        var fp32File = Path.Combine(subDir, "model.onnx");
        var quantFile = KnownModels.TryGetValue(modelName, out var info) && info.QuantizedFileName != null
            ? Path.Combine(subDir, info.QuantizedFileName)
            : null;

        var hasVocab = File.Exists(vocabFile);
        var hasFp32 = File.Exists(fp32File);
        var hasQuant = quantFile != null && File.Exists(quantFile);

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

    //  Model management (for TUI slash commands)

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

        Action<string>? toNotify = null;
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
            if (_session != null) toNotify = ModelSwitched;
        }
        toNotify?.Invoke(name);
        return _session != null;
    }

    /// <summary>Delete a downloaded model directory.</summary>
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
    public async Task<bool> DownloadModelAsync(string name, HttpClient? httpClient = null)
        => await DownloadModelStaticAsync(name, httpClient).ConfigureAwait(false);

    /// <summary>Static download method.</summary>
    public static async Task<bool> DownloadModelStaticAsync(string name, HttpClient? httpClient = null)
    {
        if (!KnownModels.TryGetValue(name, out var info)) return false;
        var baseDir = BaseModelsDirectory;
        if (baseDir == null) return false;

        var modelDir = Path.Combine(baseDir, name);
        Directory.CreateDirectory(modelDir);
        var vocabFile = Path.Combine(modelDir, "vocab.txt");

        var quantPref = Options.GetQuantizationFor(name);
        var wantQuant = (quantPref == "auto" || quantPref == "int8")
                        && info.QuantizedModelUrl != null
                        && info.QuantizedFileName != null;
        var quantFile = wantQuant ? Path.Combine(modelDir, info.QuantizedFileName!) : null;
        var fp32File = wantQuant ? null : Path.Combine(modelDir, "model.onnx");
        var activeFile = (string?)quantFile ?? fp32File;

        var http = httpClient ?? new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromMinutes(10) };
        var disposeHttp = httpClient == null;
        var triedFallback = false;

        try
        {
            try
            {
                await DownloadVocabAsync(http, info.VocabUrl, vocabFile).ConfigureAwait(false);
                var modelUrl = wantQuant ? info.QuantizedModelUrl! : info.ModelUrl;
                await DownloadModelFileAsync(http, modelUrl, activeFile!).ConfigureAwait(false);
            }
            catch when (!triedFallback)
            {
                triedFallback = true;
                if (File.Exists(vocabFile)) try { File.Delete(vocabFile); } catch { }
                if (activeFile != null && File.Exists(activeFile)) try { File.Delete(activeFile); } catch { }

                var fb = ModelBaseUrl.TrimEnd('/') + "/" + name;
                var modelFile = wantQuant ? info.QuantizedFileName ?? "model_int8.onnx" : "model.onnx";
                await DownloadVocabAsync(http, $"{fb}/vocab.txt", vocabFile).ConfigureAwait(false);
                await DownloadModelFileAsync(http, $"{fb}/{modelFile}", activeFile!).ConfigureAwait(false);
            }
            return true;
        }
        catch
        {
            if (File.Exists(vocabFile)) try { File.Delete(vocabFile); } catch { }
            if (activeFile != null && File.Exists(activeFile)) try { File.Delete(activeFile); } catch { }
            return false;
        }
        finally
        {
            if (disposeHttp) http.Dispose();
        }
    }

    private static async Task DownloadVocabAsync(HttpClient http, string url, string destPath)
    {
        using var resp = await http.GetAsync(url).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await resp.Content.CopyToAsync(fs).ConfigureAwait(false);
    }

    private static async Task DownloadModelFileAsync(HttpClient http, string url, string destPath)
    {
        using var resp = await http.GetAsync(url).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await resp.Content.CopyToAsync(fs).ConfigureAwait(false);
    }

    /// <summary>Clean up stale model variant when switching quantization mode.</summary>
    public int CleanupStaleVariant(string name, bool targetQuant)
    {
        if (!KnownModels.TryGetValue(name, out var info)) return 0;
        var baseDir = BaseModelsDirectory;
        if (baseDir == null) return 0;
        var modelDir = Path.Combine(baseDir, name);
        if (!Directory.Exists(modelDir)) return 0;

        var removed = 0;
        if (targetQuant)
        {
            var fp32 = Path.Combine(modelDir, "model.onnx");
            if (File.Exists(fp32)) { try { File.Delete(fp32); removed++; } catch { } }
        }
        else if (info.QuantizedFileName != null)
        {
            var q = Path.Combine(modelDir, info.QuantizedFileName);
            if (File.Exists(q)) { try { File.Delete(q); removed++; } catch { } }
        }
        return removed;
    }

    //  DTOs

    public sealed record ModelInfo(
        string DisplayName,
        string Description,
        string ModelUrl,
        string VocabUrl,
        string? QuantizedModelUrl,
        string? QuantizedFileName,
        int Dimension);

    public sealed record AvailableModelInfo(string Id, string DisplayName, string Description, int Dimension, bool Downloaded, bool QuantizedDownloaded);

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            lock (_loadLock) { _session?.Dispose(); _session = null; }
            _disposed = true;
        }
    }
}
