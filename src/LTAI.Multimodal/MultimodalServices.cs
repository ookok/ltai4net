using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace LTAI.Multimodal;

public sealed class OCREngine
{
    private readonly ILogger<OCREngine> _logger;
    private readonly string _tessDataPath;

    public OCREngine(ILogger<OCREngine> logger)
    {
        _logger = logger;
        _tessDataPath = Environment.GetEnvironmentVariable("TESSDATA_PREFIX")
            ?? Path.Combine(AppContext.BaseDirectory, "tessdata");
    }

    public async Task<string> ExtractTextAsync(string imagePath, string language = "eng+chi_sim", CancellationToken ct = default)
    {
        if (!File.Exists(imagePath)) return "Error: File not found";
        try
        {
            using var engine = new Tesseract.TesseractEngine(_tessDataPath, language, Tesseract.EngineMode.Default);
            using var img = Tesseract.Pix.LoadFromFile(imagePath);
            using var page = engine.Process(img);
            var text = page.GetText();
            var conf = page.GetMeanConfidence();
            _logger.LogInformation("OCR: {Path}, conf={Conf:F2}", Path.GetFileName(imagePath), conf);
            return await Task.FromResult($"Confidence: {conf:F1}%\n\n{text}");
        }
        catch (Exception ex) { _logger.LogError(ex, "OCR failed"); return $"OCR error: {ex.Message}"; }
    }

    public async Task<string> ExtractTextFromBytesAsync(byte[] imageBytes, string language = "eng+chi_sim", CancellationToken ct = default)
    {
        var tmp = Path.GetTempFileName() + ".png";
        try { await File.WriteAllBytesAsync(tmp, imageBytes, ct); return await ExtractTextAsync(tmp, language, ct); }
        finally { try { File.Delete(tmp); } catch { /* non-fatal */ } }
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
            var bytes = await File.ReadAllBytesAsync(imagePath, ct);
            var base64 = Convert.ToBase64String(bytes);
            var ext = Path.GetExtension(imagePath).ToLowerInvariant();
            var mime = ext switch { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", _ => "image/png" };
            var info = new FileInfo(imagePath);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Image: {Path.GetFileName(imagePath)}");
            sb.AppendLine($"Format: {mime} | Size: {info.Length / 1024}KB | Base64: {base64.Length} chars");
            sb.AppendLine($"Task: {task ?? "Describe this image in detail"}");
            sb.AppendLine("\n[Send to vision LLM (GPT-4V/Claude 3) for full analysis]");
            return await Task.FromResult(sb.ToString());
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
            return await Task.FromResult(ms.ToArray());
        }
        catch (Exception ex) { _logger.LogError(ex, "TTS failed"); throw; }
    }

    public async Task<string> SynthesizeToFileAsync(string text, string outputPath, string? voice = null, CancellationToken ct = default)
    {
        var bytes = await SynthesizeAsync(text, voice, ct);
        await File.WriteAllBytesAsync(outputPath, bytes, ct);
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
            return await Task.FromResult(Array.Empty<string>());
        var synth = new System.Speech.Synthesis.SpeechSynthesizer();
        return synth.GetInstalledVoices().Select(v => v.VoiceInfo.Name).ToArray();
    }
}

public sealed class MultimodalOrchestrator
{
    private readonly ILogger<MultimodalOrchestrator> _logger;
    private readonly OCREngine _ocr;
    private readonly VisionAnalyzer _vision;
    private readonly SpeechEngine _speech;

    public MultimodalOrchestrator(ILogger<MultimodalOrchestrator> logger, OCREngine ocr, VisionAnalyzer vision, SpeechEngine speech)
    {
        _logger = logger; _ocr = ocr; _vision = vision; _speech = speech;
    }

    public async Task<string> ProcessFileAsync(string filePath, string? task = null, CancellationToken ct = default)
    {
        if (!File.Exists(filePath)) return "Error: File not found";
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif" =>
                (task?.Contains("ocr") == true || task?.Contains("text") == true)
                    ? await _ocr.ExtractTextAsync(filePath, ct: ct)
                    : await _vision.DescribeImageAsync(filePath, task, ct),
            ".wav" or ".mp3" or ".m4a" => await _speech.RecognizeFromFileAsync(filePath, ct),
            _ => $"Unsupported format: {ext}"
        };
    }
}
