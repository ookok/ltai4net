// Copyright (c) LTAI. All rights reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace LTAI.AI;

/// <summary>
/// Local ONNX-based sentence embedding using all-MiniLM-L6-v2.
/// 384-dim, official ONNX export from sentence-transformers, 90MB.
/// Processes in-process without any external API call.
/// Falls back gracefully if the model file is not found.
/// </summary>
public sealed class LocalEmbedder : IDisposable
{
    private const int MaxLength = 512;
    private const int Dimension = 384;

    private readonly InferenceSession? _session;
    private readonly Dictionary<string, int>? _vocab;
    private readonly string? _modelPath;
    private bool _disposed;

    /// <summary>Whether the ONNX model is loaded and ready.</summary>
    public bool Available => _session != null;

    /// <summary>Embedding dimension (384 for all-MiniLM-L6-v2).</summary>
    public int Dim => Dimension;

    // Special tokens for BERT
    private const int ClsTokenId = 101;
    private const int SepTokenId = 102;
    private const int PadTokenId = 0;
    private const int UnkTokenId = 100;

    /// <summary>
    /// Initializes a new instance. Searches for the model in standard locations.
    /// </summary>
    public LocalEmbedder()
    {
        _modelPath = FindModelFile("model.onnx");
        var vocabPath = FindModelFile("vocab.txt");

        if (_modelPath == null || vocabPath == null)
        {
            return; // Available = false
        }

        try
        {
            var opts = new SessionOptions();
            // Optimize for CPU inference
            opts.EnableMemoryPattern = true;
            opts.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

            _session = new InferenceSession(_modelPath, opts);
            _vocab = LoadVocab(vocabPath);
        }
        catch (Exception)
        {
            // Available = false
        }
    }

    /// <summary>
    /// Generate embedding vector for the given text.
    /// </summary>
    public float[] Generate(string text)
    {
        if (_session == null || _vocab == null)
            throw new InvalidOperationException(
                "LocalEmbedder not available. Run 'dotnet build' to download the embedding model.");

        var tokens = Tokenize(text);

        // Create input tensors
        var inputIds = new DenseTensor<long>(new[] { 1, tokens.Count });
        var attentionMask = new DenseTensor<long>(new[] { 1, tokens.Count });
        var tokenTypeIds = new DenseTensor<long>(new[] { 1, tokens.Count });

        for (int i = 0; i < tokens.Count; i++)
        {
            inputIds[0, i] = tokens[i].InputId;
            attentionMask[0, i] = tokens[i].AttentionMask;
            tokenTypeIds[0, i] = 0;
        }

        // Run ONNX inference
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
            NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds),
        };

        using var results = _session.Run(inputs);

        // all-MiniLM-L6-v2 output: last_hidden_state (batch, seq_len, 384)
        var embedding = results.First().AsTensor<float>();

        // Mean pooling (take average over all non-padding tokens)
        var pooled = MeanPool(embedding, attentionMask);

        // L2 normalize (BGE requires normalized embeddings)
        return L2Normalize(pooled);
    }

    // ═══════════════════════════════════════════
    //  BERT WordPiece Tokenizer
    // ═══════════════════════════════════════════

    private List<Token> Tokenize(string text)
    {
        // Normalize: lowercase for CJK mixed text, collapse whitespace
        var normalized = NormalizeText(text);
        var words = SplitWords(normalized);
        var pieces = new List<string>();

        pieces.Add("[CLS]");

        foreach (var word in words)
        {
            var wordPieces = WordPiece(word);
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
            var id = _vocab!.GetValueOrDefault(piece, UnkTokenId);
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
        while (text.Contains("  ")) text = text.Replace("  ", " ");
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

    private List<string> WordPiece(string word)
    {
        if (_vocab!.ContainsKey(word))
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

                if (_vocab!.ContainsKey(sub))
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
        int hiddenDim = embedding.Dimensions[2];   // 768 (BGE hidden size)

        var result = new float[Dimension];  // 384 — MiniLM outputs 384

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

        // Take first 512 dimensions (BGE-small-zh target dim)
        Array.Copy(sum, result, Math.Min(hiddenDim, Dimension));
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
    //  Model file search
    // ═══════════════════════════════════════════

    private static string? FindModelFile(string fileName)
    {
        // Search order: AppContext.BaseDirectory > CWD/models/ > project root
        string[] searchDirs =
        [
            Path.Combine(AppContext.BaseDirectory, "models", "minilm-l6-v2"),
            Path.Combine(Directory.GetCurrentDirectory(), "models", "minilm-l6-v2"),
            Path.Combine(Directory.GetCurrentDirectory(), "..", "models", "minilm-l6-v2"),
            AppContext.BaseDirectory,
        ];

        foreach (var dir in searchDirs)
        {
            var full = Path.Combine(dir, fileName);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    // ═══════════════════════════════════════════
    //  DTO
    // ═══════════════════════════════════════════

    private readonly record struct Token(long InputId, long AttentionMask);

    public void Dispose()
    {
        if (!_disposed)
        {
            _session?.Dispose();
            _disposed = true;
        }
    }
}
