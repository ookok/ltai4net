using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using LTAI.Core.System;
using Microsoft.Extensions.Logging;

namespace LTAI.Infra.Multimodal;

public sealed class OCREngine : IDisposable
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tiff", ".tif", ".gif" };

    private static readonly byte[][] ImageMagicBytes =
    [
        [0x89, 0x50, 0x4E, 0x47], // PNG
        [0xFF, 0xD8, 0xFF],        // JPEG
        [0x52, 0x49, 0x46, 0x46],  // WEBP (RIFF)
        [0x42, 0x4D],              // BMP
        [0x49, 0x49],              // TIFF (little endian)
        [0x4D, 0x4D],              // TIFF (big endian)
        [0x47, 0x49, 0x46],        // GIF
    ];

    private readonly ILogger<OCREngine> _logger;
    private readonly string _tessDataPath;
    private readonly ConcurrentDictionary<string, Lazy<Tesseract.TesseractEngine>> _engines = new();

    public OCREngine(ILogger<OCREngine> logger)
    {
        _logger = logger;
        _tessDataPath = Environment.GetEnvironmentVariable("TESSDATA_PREFIX")
            ?? Path.Combine(AppContext.BaseDirectory, "tessdata");

        if (!Directory.Exists(_tessDataPath))
            _logger.LogWarning("TESSDATA directory not found: {Path}. OCR will fail until traineddata files are provided.", _tessDataPath);
    }

    private Tesseract.TesseractEngine GetEngine(string language)
    {
        return _engines.GetOrAdd(language, lang => new Lazy<Tesseract.TesseractEngine>(() =>
        {
            _logger.LogInformation("Initializing Tesseract engine with language: {Lang}", lang);
            return new Tesseract.TesseractEngine(_tessDataPath, lang, Tesseract.EngineMode.Default);
        })).Value;
    }

    public async Task<string> ExtractTextAsync(string imagePath, string language = "eng+chi_sim", CancellationToken ct = default)
    {
        if (!File.Exists(imagePath)) return "Error: File not found";

        var ext = Path.GetExtension(imagePath);
        if (!AllowedExtensions.Contains(ext))
            return $"Error: Unsupported image format '{ext}'. Allowed: {string.Join(", ", AllowedExtensions)}";

        try
        {
            var engine = GetEngine(language);
            using var img = Tesseract.Pix.LoadFromFile(imagePath);
            using var page = engine.Process(img);
            var text = page.GetText();
            var conf = page.GetMeanConfidence() * 100;
            _logger.LogInformation("OCR: {Path}, confidence={Conf:F1}%", Path.GetFileName(imagePath), conf);
            return await Task.FromResult($"Confidence: {conf:F1}%\n\n{text}");
        }
        catch (Exception ex) { _logger.LogError(ex, "OCR failed for {Path}", imagePath); return $"OCR error: {ex.Message}"; }
    }

    public async Task<string> ExtractTextFromBytesAsync(byte[] imageBytes, string language = "eng+chi_sim", CancellationToken ct = default)
    {
        if (imageBytes == null || imageBytes.Length == 0)
            return "Error: Empty image data";

        if (!IsLikelyImage(imageBytes))
            return "Error: Data does not appear to be a valid image";

        try
        {
            var engine = GetEngine(language);
            using var img = Tesseract.Pix.LoadFromMemory(imageBytes);
            using var page = engine.Process(img);
            var text = page.GetText();
            var conf = page.GetMeanConfidence() * 100;
            _logger.LogInformation("OCR from bytes, confidence={Conf:F1}%", conf);
            return await Task.FromResult($"Confidence: {conf:F1}%\n\n{text}");
        }
        catch (Exception ex) { _logger.LogError(ex, "OCR failed from bytes"); return $"OCR error: {ex.Message}"; }
    }

    private static bool IsLikelyImage(byte[] bytes)
    {
        if (bytes.Length < 4) return false;
        return ImageMagicBytes.Any(sig => bytes.Take(sig.Length).SequenceEqual(sig));
    }

    public void Dispose()
    {
        foreach (var kvp in _engines)
        {
            if (kvp.Value.IsValueCreated)
                kvp.Value.Value.Dispose();
        }
        _engines.Clear();
    }
}

public sealed class VisionAnalyzer
{
    private readonly ILogger<VisionAnalyzer> _logger;

    public VisionAnalyzer(ILogger<VisionAnalyzer> logger) { _logger = logger; }

    public async Task<string> DescribeImageAsync(string imagePath, string? task = null, CancellationToken ct = default)
    {
        if (!File.Exists(imagePath)) return "Error: File not found";
        try
        {
            var bytes = await File.ReadAllBytesAsync(imagePath, ct).ConfigureAwait(false);
            var base64 = Convert.ToBase64String(bytes);
            var ext = Path.GetExtension(imagePath).ToLowerInvariant();
            var mime = ext switch { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", _ => "image/png" };
            var info = new FileInfo(imagePath);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Image: {Path.GetFileName(imagePath)}");
            sb.AppendLine($"Format: {mime} | Size: {info.Length / 1024}KB | Base64: {base64.Length} chars");
            sb.AppendLine($"Task: {task ?? "Describe this image in detail"}");
            sb.AppendLine("\n[Send to vision LLM (GPT-4V/Claude 3) for full analysis]");
            return await Task.FromResult(sb.ToString()).ConfigureAwait(false);
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }
}

public sealed class SpeechEngine
{
    private readonly ILogger<SpeechEngine> _logger;

    public SpeechEngine(ILogger<SpeechEngine> logger) { _logger = logger; }

    public async Task<byte[]> SynthesizeAsync(string text, string? voice = null, CancellationToken ct = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new PlatformNotSupportedException("Speech synthesis requires Windows");
        try
        {
            var synth = new System.Speech.Synthesis.SpeechSynthesizer();
            if (voice != null) try { synth.SelectVoice(voice); } catch { /* non-fatal */ }
            using var ms = new MemoryStream();
            synth.SetOutputToWaveStream(ms);
            synth.Speak(text);
            return await Task.FromResult(ms.ToArray()).ConfigureAwait(false);
        }
        catch (Exception ex) { _logger.LogError(ex, "TTS failed"); throw; }
    }

    public async Task<string> SynthesizeToFileAsync(string text, string outputPath, string? voice = null, CancellationToken ct = default)
    {
        var bytes = await SynthesizeAsync(text, voice, ct).ConfigureAwait(false);
        await File.WriteAllBytesAsync(outputPath, bytes, ct).ConfigureAwait(false);
        return outputPath;
    }

    public async Task<string> RecognizeFromFileAsync(string audioPath, CancellationToken ct = default)
    {
        if (!File.Exists(audioPath)) return "Error: File not found";
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new PlatformNotSupportedException("Speech recognition requires Windows");
        try
        {
            var rec = new System.Speech.Recognition.SpeechRecognitionEngine();
            rec.SetInputToWaveFile(audioPath);
            rec.LoadGrammar(new System.Speech.Recognition.DictationGrammar());
            var result = rec.Recognize();
            return await Task.FromResult(result?.Text ?? "(No speech detected)");
        }
        catch (Exception ex) { return $"Recognition error: {ex.Message}"; }
    }

    public async Task<string[]> GetAvailableVoicesAsync(CancellationToken ct = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return await Task.FromResult(Array.Empty<string>()).ConfigureAwait(false);
#pragma warning disable CA1416
        var synth = new System.Speech.Synthesis.SpeechSynthesizer();
        return synth.GetInstalledVoices().Select(v => v.VoiceInfo.Name).ToArray();
#pragma warning restore CA1416
    }
}

public sealed class MultimodalOrchestrator
{
    private readonly ILogger<MultimodalOrchestrator> _logger;
    private readonly OCREngine _ocr;
    private readonly RapidOCREngine? _rapidOcr;
    private readonly VisionAnalyzer _vision;
    private readonly SpeechEngine _speech;
    private readonly LTAI.Core.Multimodal.WhisperSttEngine? _whisper;

    public MultimodalOrchestrator(ILogger<MultimodalOrchestrator> logger, OCREngine ocr,
        VisionAnalyzer vision, SpeechEngine speech, RapidOCREngine? rapidOcr = null,
        LTAI.Core.Multimodal.WhisperSttEngine? whisper = null)
    {
        _logger = logger; _ocr = ocr; _vision = vision; _speech = speech; _rapidOcr = rapidOcr;
        _whisper = whisper;
    }

    public async Task<string> ProcessFileAsync(string filePath, string? task = null, CancellationToken ct = default)
    {
        if (!File.Exists(filePath)) return "Error: File not found";
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var result = ext switch
        {
            ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif" =>
                (task?.Contains("ocr") == true || task?.Contains("text") == true)
                    ? (_rapidOcr?.IsReady == true
                        ? await _rapidOcr.ExtractTextAsync(filePath, ct: ct)
                        : await _ocr.ExtractTextAsync(filePath, ct: ct))
                    : await _vision.DescribeImageAsync(filePath, task, ct),
            ".wav" or ".mp3" or ".m4a" => await _speech.RecognizeFromFileAsync(filePath, ct),
            _ => $"Unsupported format: {ext}"
        };

        // Shield: check extracted multimodal content for prompt injection
        if (result.StartsWith("Error:") || result.StartsWith("Unsupported"))
            return result;

        var shieldResult = PromptShield.Instance.SanitizeInput(result);
        if (!shieldResult.Passed)
        {
            _logger.LogWarning(
                "Multimodal shield blocked: {Path} — violations: {Violations}",
                filePath, string.Join(", ", shieldResult.Violations));
            return $"[Blocked] The extracted content was flagged by safety shield ({string.Join(", ", shieldResult.Violations)})";
        }

        return result;
    }

    public async Task<string> ProcessSpeechAsync(string audioFilePath, CancellationToken ct = default)
    {
        string text;
        if (_whisper != null)
        {
            var audioData = await File.ReadAllBytesAsync(audioFilePath, ct).ConfigureAwait(false);
            var result = await _whisper.TranscribeAsync(audioData).ConfigureAwait(false);
            text = result?.Text ?? "";
        }
        else
        {
            text = await _speech.RecognizeFromFileAsync(audioFilePath, ct).ConfigureAwait(false);
        }

        // Shield: check recognized speech for prompt injection
        if (string.IsNullOrEmpty(text) || text.StartsWith("Error:") || text.StartsWith("Recognition error"))
            return text;

        var shieldResult = PromptShield.Instance.SanitizeInput(text);
        if (!shieldResult.Passed)
        {
            _logger.LogWarning(
                "Speech shield blocked: {Path} — violations: {Violations}",
                audioFilePath, string.Join(", ", shieldResult.Violations));
            return $"[Blocked] The recognized speech was flagged by safety shield ({string.Join(", ", shieldResult.Violations)})";
        }

        return text;
    }
}
