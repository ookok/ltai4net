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

    /// <summary>Known ONNX models with download URLs.</summary>
    public static readonly Dictionary<string, ModelInfo> KnownModels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["minilm-l6-v2"] = new(
            DisplayName: "all-MiniLM-L6-v2",
            Description: "384维 英文通用 (推荐)",
            ModelUrl: "https://hf-mirror.com/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx",
            VocabUrl: "https://hf-mirror.com/sentence-transformers/all-MiniLM-L6-v2/resolve/main/vocab.txt",
            Dimension: 384
        ),
        ["bge-small-zh"] = new(
            DisplayName: "BAAI/bge-small-zh-v1.5",
            Description: "384维 中文通用",
            ModelUrl: "https://hf-mirror.com/BAAI/bge-small-zh-v1.5/resolve/main/onnx/model.onnx",
            VocabUrl: "https://hf-mirror.com/BAAI/bge-small-zh-v1.5/resolve/main/vocab.txt",
            Dimension: 384
        ),
        ["bge-small-en"] = new(
            DisplayName: "BAAI/bge-small-en-v1.5",
            Description: "384维 英文通用",
            ModelUrl: "https://hf-mirror.com/BAAI/bge-small-en-v1.5/resolve/main/onnx/model.onnx",
            VocabUrl: "https://hf-mirror.com/BAAI/bge-small-en-v1.5/resolve/main/vocab.txt",
            Dimension: 384
        ),
    };

    /// <summary>Whether a model is loaded and available. Triggers lazy load on first check.</summary>
    public bool Available
    {
        get
        {
            if (!_loadAttempted) EnsureLoaded();
            return _session != null;
        }
    }

    /// <summary>Eagerly load the model on a background thread. Safe to call multiple times.</summary>
    public async Task PreWarmAsync()
    {
        if (_loadAttempted) return;
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
    /// Model is loaded lazily on first use.
    /// </summary>
    public LocalEmbedder()
    {
        BaseModelsDirectory ??= FindBaseModelsDirectory();
        (_currentModelName, _modelPath, _vocabPath) = DetectCurrentModel();
        // Eager pre-warm on background thread to avoid blocking first use
        _ = PreWarmAsync();
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

                // Try DirectML (Windows GPU, no NVIDIA required)
                try { opts.AppendExecutionProvider_DML(); } catch { }

                // Try CUDA if available
                try { opts.AppendExecutionProvider_CUDA(0); } catch { }

                // CPU fallback is the default
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
            }
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
            var modelFile = Path.Combine(subDir, "model.onnx");
            var vocabFile = Path.Combine(subDir, "vocab.txt");
            if (File.Exists(modelFile) && File.Exists(vocabFile))
                return (Path.GetFileName(subDir), modelFile, vocabFile);
        }
        return (null, null, null);
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
            result.Add(new AvailableModelInfo(id, info.DisplayName, info.Description, info.Dimension, downloaded));
        }
        return result;
    }

    /// <summary>Switch the active embedding model. Returns true on success.</summary>
    public bool SwitchModel(string name)
    {
        var baseDir = BaseModelsDirectory;
        if (baseDir == null) return false;

        var modelDir = Path.Combine(baseDir, name);
        var modelFile = Path.Combine(modelDir, "model.onnx");
        var vocabFile = Path.Combine(modelDir, "vocab.txt");
        if (!File.Exists(modelFile) || !File.Exists(vocabFile)) return false;

        lock (_loadLock)
        {
            _session?.Dispose();
            _session = null;
            _vocab = null;
            _loadAttempted = false;
            _currentModelName = name;
            _modelPath = modelFile;
            _vocabPath = vocabFile;

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
    public async Task<bool> DownloadModelAsync(string name, HttpClient? httpClient = null)
    {
        if (!KnownModels.TryGetValue(name, out var info)) return false;

        var baseDir = BaseModelsDirectory;
        if (baseDir == null) return false;

        var modelDir = Path.Combine(baseDir, name);
        Directory.CreateDirectory(modelDir);
        var modelFile = Path.Combine(modelDir, "model.onnx");
        var vocabFile = Path.Combine(modelDir, "vocab.txt");

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
            return true;
        }
        catch
        {
            if (File.Exists(modelFile)) try { File.Delete(modelFile); } catch { }
            if (File.Exists(vocabFile)) try { File.Delete(vocabFile); } catch { }
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
    public sealed record ModelInfo(string DisplayName, string Description, string ModelUrl, string VocabUrl, int Dimension);

    /// <summary>Information about an available (or downloadable) model.</summary>
    public sealed record AvailableModelInfo(string Id, string DisplayName, string Description, int Dimension, bool Downloaded);

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
