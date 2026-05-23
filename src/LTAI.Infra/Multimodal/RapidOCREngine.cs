using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace LTAI.Infra.Multimodal;

/// RapidOCR: ONNX-based lightweight OCR engine (detection + recognition).
/// Models: PP-OCRv4 (det ~4.5MB + rec ~8.5MB), pure ONNX Runtime, zero Python dependency.
/// Chinese accuracy >> Tesseract at the same ~15MB model size.
public sealed class RapidOCREngine : IDisposable
{
    private readonly ILogger<RapidOCREngine> _logger;
    private readonly string _modelDir;
    private InferenceSession? _detSession;
    private InferenceSession? _recSession;
    private string[]? _vocab;
    private bool _isReady;

    private static readonly HashSet<string> AllowedExt = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".tiff", ".tif", ".webp", ".gif" };

    public bool IsReady => _isReady;

    public RapidOCREngine(string modelDir, ILogger<RapidOCREngine>? logger = null)
    {
        _modelDir = modelDir;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RapidOCREngine>.Instance;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var detPath = global::System.IO.Path.Combine(_modelDir, "ch_PP-OCRv4_det_infer.onnx");
        var recPath = global::System.IO.Path.Combine(_modelDir, "ch_PP-OCRv4_rec_infer.onnx");
        var vocabPath = global::System.IO.Path.Combine(_modelDir, "ppocr_keys_v1.txt");

        if (!global::System.IO.File.Exists(detPath) || !global::System.IO.File.Exists(recPath))
        {
            _logger.LogWarning("RapidOCR models not found in {Dir}. Run ModelAutoDownloader first.", _modelDir);
            return;
        }

        await Task.Run(() =>
        {
            _detSession = new InferenceSession(detPath, new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                IntraOpNumThreads = 1
            });
            _recSession = new InferenceSession(recPath, new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                IntraOpNumThreads = 1
            });
        }, ct);

        if (global::System.IO.File.Exists(vocabPath))
        {
            var vocabText = await global::System.IO.File.ReadAllTextAsync(vocabPath, ct);
            _vocab = vocabText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }

        _isReady = true;
        _logger.LogInformation("RapidOCR initialized: det={Det}MB rec={Rec}MB vocab={Vocab}",
            new global::System.IO.FileInfo(detPath).Length / 1048576,
            new global::System.IO.FileInfo(recPath).Length / 1048576,
            _vocab?.Length ?? 0);
    }

    public async Task<string> ExtractTextAsync(string imagePath, string language = "", CancellationToken ct = default)
    {
        if (!_isReady) return "";
        if (!global::System.IO.File.Exists(imagePath)) return "";

        var bytes = await global::System.IO.File.ReadAllBytesAsync(imagePath, ct);
        return await RecognizeBytesAsync(bytes, ct);
    }

    public async Task<string> ExtractTextFromBytesAsync(byte[] imageBytes, string language = "", CancellationToken ct = default)
    {
        if (!_isReady) return "";
        if (imageBytes.Length < 100) return "";
        return await RecognizeBytesAsync(imageBytes, ct);
    }

    private async Task<string> RecognizeBytesAsync(byte[] imageBytes, CancellationToken ct)
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Load image and preprocess for detection
            var (imgData, width, height) = await Task.Run(() => LoadImageRgb(imageBytes), ct);
            if (imgData is null) return "";

            // Scale short side to 960 for detection
            float ratio;
            float[] detInput;
            int detW, detH;
            (detInput, detW, detH, ratio) = PreprocessForDetection(imgData, width, height);

            // Run detection
            var boxes = RunDetection(detInput, detW, detH, ratio, width, height);

            if (boxes.Count == 0)
            {
                _logger.LogDebug("RapidOCR: no text detected ({Time}ms)", sw.ElapsedMilliseconds);
                return "";
            }

            // Recognize each box
            var results = new List<string>();
            foreach (var box in boxes)
            {
                ct.ThrowIfCancellationRequested();
                var text = RecognizeBox(imgData, width, height, box);
                if (!string.IsNullOrEmpty(text)) results.Add(text);
            }

            _logger.LogInformation("RapidOCR: {Boxes} boxes, {Time}ms", boxes.Count, sw.ElapsedMilliseconds);
            return string.Join("\n", results);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RapidOCR recognition failed");
            return "";
        }
    }

    // ---- Detection (DBNet) ----

    private (float[] data, int w, int h, float ratio) PreprocessForDetection(
        float[] img, int srcW, int srcH)
    {
        const int limitSide = 960;
        float ratio;
        int detW, detH;

        if (global::System.Math.Max(srcW, srcH) > limitSide)
        {
            ratio = (float)limitSide / global::System.Math.Max(srcW, srcH);
            detW = (int)(srcW * ratio);
            detH = (int)(srcH * ratio);
        }
        else
        {
            ratio = 1f;
            detW = srcW;
            detH = srcH;
        }

        // Pad to multiples of 32
        detW = ((detW + 31) / 32) * 32;
        detH = ((detH + 31) / 32) * 32;

        var input = new float[3 * detH * detW];
        for (int y = 0; y < detH; y++)
        for (int x = 0; x < detW; x++)
        {
            int srcY = (int)(y / ratio);
            int srcX = (int)(x / ratio);
            srcY = global::System.Math.Min(srcY, srcH - 1);
            srcX = global::System.Math.Min(srcX, srcW - 1);

            var offset = (srcY * srcW + srcX) * 3;
            var r = offset < img.Length - 2 ? img[offset] : 0;
            var g = offset + 1 < img.Length - 1 ? img[offset + 1] : 0;
            var b = offset + 2 < img.Length ? img[offset + 2] : 0;

            // Normalize: (value / 255 - mean) / std
            var baseIdx = y * detW + x;
            input[baseIdx] = (r / 255f - 0.485f) / 0.229f;
            input[detH * detW + baseIdx] = (g / 255f - 0.456f) / 0.224f;
            input[2 * detH * detW + baseIdx] = (b / 255f - 0.406f) / 0.225f;
        }

        return (input, detW, detH, ratio);
    }

    private List<(float x1, float y1, float x2, float y2)> RunDetection(
        float[] input, int detW, int detH, float ratio, int srcW, int srcH)
    {
        var tensor = new DenseTensor<float>(input, [1, 3, detH, detW]);
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("x", tensor) };
        using var results = _detSession!.Run(inputs);

        // DBNet output: probability map (1,1,H,W)
        var probMap = results.First().AsTensor<float>();
        var h = (int)probMap.Dimensions[2];
        var w = (int)probMap.Dimensions[3];

        // Threshold probability map
        var binary = new byte[h * w];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            binary[y * w + x] = probMap[0, 0, y, x] > 0.3f ? (byte)1 : (byte)0;

        // Find connected components (simple BFS)
        var boxes = FindContourBoxes(binary, w, h);

        // Scale back to original image coordinates
        var result = new List<(float, float, float, float)>();
        foreach (var (bx1, by1, bx2, by2) in boxes)
        {
            result.Add((
                bx1 / ratio, by1 / ratio,
                bx2 / ratio, by2 / ratio
            ));
        }

        return result;
    }

    private static List<(int x1, int y1, int x2, int y2)> FindContourBoxes(byte[] binary, int w, int h)
    {
        var visited = new bool[h * w];
        var boxes = new List<(int, int, int, int)>();
        var queue = new Queue<int>();

        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            var idx = y * w + x;
            if (binary[idx] == 0 || visited[idx]) continue;

            int minX = x, maxX = x, minY = y, maxY = y;
            queue.Clear();
            queue.Enqueue(idx);
            visited[idx] = true;

            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                var cx = cur % w;
                var cy = cur / w;
                minX = global::System.Math.Min(minX, cx);
                maxX = global::System.Math.Max(maxX, cx);
                minY = global::System.Math.Min(minY, cy);
                maxY = global::System.Math.Max(maxY, cy);

                foreach (var (dx, dy) in new[] { (1,0),(-1,0),(0,1),(0,-1),(1,1),(1,-1),(-1,1),(-1,-1) })
                {
                    var nx = cx + dx; var ny = cy + dy;
                    if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                    var nIdx = ny * w + nx;
                    if (binary[nIdx] == 0 || visited[nIdx]) continue;
                    visited[nIdx] = true;
                    queue.Enqueue(nIdx);
                }
            }

            var bw = maxX - minX + 1;
            var bh = maxY - minY + 1;
            // Filter tiny boxes (noise)
            if (bw >= 8 && bh >= 8 && bw * bh >= 64)
                boxes.Add((minX, minY, maxX, maxY));
        }

        // Sort by reading order: top-to-bottom, left-to-right
        boxes.Sort((a, b) =>
        {
            var yDiff = a.Item2 - b.Item2;
            if (global::System.Math.Abs(yDiff) < 10) return a.Item1 - b.Item1;
            return yDiff;
        });

        return boxes;
    }

    // ---- Recognition (CRNN) ----

    private string RecognizeBox(float[] img, int imgW, int imgH,
        (float x1, float y1, float x2, float y2) box)
    {
        try
        {
            var bx1 = (int)global::System.Math.Max(0, box.x1);
            var by1 = (int)global::System.Math.Max(0, box.y1);
            var bx2 = (int)global::System.Math.Min(imgW - 1, (int)box.x2);
            var by2 = (int)global::System.Math.Min(imgH - 1, (int)box.y2);
            if (bx2 <= bx1 || by2 <= by1) return "";

            // Crop and resize to 32px height, dynamic width
            var cropW = bx2 - bx1;
            var cropH = by2 - by1;
            var ratio = 32f / cropH;
            var resizedW = (int)(cropW * ratio);
            resizedW = global::System.Math.Max(resizedW, 4);

            var recInput = new float[3 * 32 * resizedW];
            for (int y = 0; y < 32; y++)
            for (int x = 0; x < resizedW; x++)
            {
                var srcX = bx1 + (int)(x / ratio);
                var srcY = by1 + (int)(y / ratio);
                srcX = global::System.Math.Clamp(srcX, 0, imgW - 1);
                srcY = global::System.Math.Clamp(srcY, 0, imgH - 1);

                var offset = (srcY * imgW + srcX) * 3;
                var r = offset < img.Length - 2 ? img[offset] : 0;
                var g = offset + 1 < img.Length - 1 ? img[offset + 1] : 0;
                var b = offset + 2 < img.Length ? img[offset + 2] : 0;

                var baseIdx = y * resizedW + x;
                recInput[baseIdx] = (r / 255f - 0.5f) / 0.5f;
                recInput[32 * resizedW + baseIdx] = (g / 255f - 0.5f) / 0.5f;
                recInput[2 * 32 * resizedW + baseIdx] = (b / 255f - 0.5f) / 0.5f;
            }

            var tensor = new DenseTensor<float>(recInput, [1, 3, 32, resizedW]);
            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("x", tensor) };
            using var results = _recSession!.Run(inputs);
            var logits = results.First().AsTensor<float>();

            // CTC decode
            var vocabSize = _vocab?.Length ?? 6623;
            var timeSteps = (int)logits.Dimensions[1];
            var result = new List<int>();

            for (int t = 0; t < timeSteps; t++)
            {
                var maxIdx = 0;
                var maxVal = float.MinValue;
                for (int v = 0; v < vocabSize; v++)
                {
                    var val = logits[0, t, v];
                    if (val > maxVal) { maxVal = val; maxIdx = v; }
                }

                // CTC merge: skip blanks and repeated chars
                if (maxIdx > 0 && (result.Count == 0 || maxIdx != result[^1]))
                    result.Add(maxIdx);
            }

            // Map indices to characters
            var chars = new global::System.Text.StringBuilder();
            foreach (var idx in result)
            {
                if (_vocab is not null && idx < _vocab.Length)
                    chars.Append(_vocab[idx]);
                else if (idx < 6623)
                    chars.Append(MapIdxToChar(idx));
            }

            return chars.ToString().Trim();
        }
        catch
        {
            return "";
        }
    }

    private static char MapIdxToChar(int idx)
    {
        // Approximate PP-OCR key mapping for common ranges
        return idx switch
        {
            < 97 => (char)('!' + idx),  // punctuation
            < 123 => (char)idx,          // ASCII letters
            _ => (char)(0x4E00 + idx - 123) // CJK ideographs
        };
    }

    // ---- Image Loading ----

    private static (float[]? data, int width, int height) LoadImageRgb(byte[] bytes)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(bytes);
            if (bitmap is null) return (null, 0, 0);

            var w = bitmap.Width;
            var h = bitmap.Height;
            var data = new float[w * h * 3];

            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                var idx = (y * w + x) * 3;
                data[idx] = pixel.Red;
                data[idx + 1] = pixel.Green;
                data[idx + 2] = pixel.Blue;
            }

            return (data, w, h);
        }
        catch
        {
            return (null, 0, 0);
        }
    }

    public void Dispose()
    {
        _detSession?.Dispose();
        _recSession?.Dispose();
        _isReady = false;
    }
}
