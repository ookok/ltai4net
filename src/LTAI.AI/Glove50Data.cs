// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════
//  Glove50Data — compact binary vocabulary loader for
//  GloVe-50d word vectors.
//
//  Format: 4-byte count N, then N entries:
//    [2-byte word_byte_length][UTF8 word bytes][50×4-byte float vector]
//  File extension: .gv50
//
//  Download via `scripts/generate-glove50.ps1` which fetches
//  the real GloVe-50d from Stanford NLP / HuggingFace and
//  converts to .gv50 compact format (~2MB for 400K words).
// ═══════════════════════════════════════════════════════

using System.IO;
using System.IO.Compression;

namespace LTAI.AI;

public static class Glove50Data
{
    public const int Dim = 50;
    public const string FileName = "glove50d.gv50";

    /// <summary>
    /// Download URLs for pre-converted .gv50 files (fallback).
    /// Primary source: generate via `scripts/generate-glove50.ps1`.
    /// </summary>
    public static readonly string[] MirrorUrls =
    [
        "https://mogoo.com.cn/glove/glove50d.gv50",
        "https://hf-mirror.com/ltai/glove50d/resolve/main/glove50d.gv50",
    ];

    /// <summary>
    /// Raw GloVe-50d source URLs (used by generator script to create .gv50).
    /// English: Stanford NLP (822MB).
    /// </summary>
    public static readonly string[] SourceUrls =
    [
        "https://huggingface.co/stanfordnlp/glove/resolve/main/glove.6B.50d.txt",
        "https://mogoo.com.cn/glove/glove.6B.50d.txt",
    ];

    /// <summary>
    /// Load vocabulary from a .gv50 binary file.
    /// Returns null on failure.
    /// </summary>
    public static Dictionary<string, float[]>? Load(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(fs);

            var count = reader.ReadInt32();
            if (count <= 0 || count > 500_000) return null;

            var vocab = new Dictionary<string, float[]>(count, StringComparer.OrdinalIgnoreCase);
            var wordBuf = new byte[256];
            var vecBuf = new byte[Dim * 4];

            for (int i = 0; i < count; i++)
            {
                var wordLen = reader.ReadUInt16();
                if (wordLen > 255) continue;
                var bytesRead = reader.Read(wordBuf, 0, wordLen);
                if (bytesRead != wordLen) break;
                var word = System.Text.Encoding.UTF8.GetString(wordBuf, 0, wordLen);

                var vecRead = reader.Read(vecBuf, 0, Dim * 4);
                if (vecRead != Dim * 4) break;

                var vec = new float[Dim];
                Buffer.BlockCopy(vecBuf, 0, vec, 0, Dim * 4);
                vocab[word] = vec;
            }

            return vocab;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Load from default search paths.</summary>
    public static Dictionary<string, float[]>? LoadFromDefaultPaths()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, FileName),
            Path.Combine(Directory.GetCurrentDirectory(), FileName),
            Path.Combine(Directory.GetCurrentDirectory(), "models", FileName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LTAI", "glove", FileName),
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                var result = Load(path);
                if (result != null) return result;
            }
        }
        return null;
    }

    /// <summary>
    /// Download the .gv50 file from mirrors.
    /// Returns the file path on success, null on failure.
    /// </summary>
    public static async Task<string?> DownloadAsync(string? destDir = null, CancellationToken ct = default)
    {
        destDir ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LTAI", "glove");
        Directory.CreateDirectory(destDir);
        var destPath = Path.Combine(destDir, FileName);

        using var http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        })
        {
            Timeout = TimeSpan.FromMinutes(2),
        };

        foreach (var url in MirrorUrls)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
                return destPath;
            }
            catch (HttpRequestException) when (url != MirrorUrls[^1])
            {
                // try next mirror
            }
        }

        return null;
    }

    /// <summary>
    /// Convert raw GloVe text format (word v0 v1 ... v49) to .gv50 binary.
    /// Each line: one word followed by 50 floats.
    /// Returns number of words written, or -1 on failure.
    /// </summary>
    public static int ConvertTxtToGv50(string txtPath, string gv50Path, int maxWords = 400_000)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(gv50Path)!);
            using var writer = new BinaryWriter(new FileStream(gv50Path, FileMode.Create, FileAccess.Write));
            using var reader = new StreamReader(txtPath);

            // First pass: count words
            var wordBuffer = new List<(string Word, float[] Vec)>();
            string? line;
            while ((line = reader.ReadLine()) != null && wordBuffer.Count < maxWords)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 51) continue;

                var word = parts[0];
                var vec = new float[50];
                bool valid = true;
                for (int i = 0; i < 50; i++)
                {
                    if (!float.TryParse(parts[i + 1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out vec[i]))
                    { valid = false; break; }
                }
                if (valid) wordBuffer.Add((word, vec));
            }

            // Write binary
            writer.Write(wordBuffer.Count);
            var buf = new byte[200];
            foreach (var (word, vec) in wordBuffer)
            {
                var wb = System.Text.Encoding.UTF8.GetBytes(word);
                if (wb.Length > 255) continue;
                writer.Write((ushort)wb.Length);
                writer.Write(wb);
                Buffer.BlockCopy(vec, 0, buf, 0, 200);
                writer.Write(buf);
            }

            return wordBuffer.Count;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>Check if the .gv50 file exists on any default path.</summary>
    public static bool Exists => LoadFromDefaultPaths() != null;
}
