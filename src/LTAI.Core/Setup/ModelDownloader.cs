using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LTAI.Core.Governors;
using Microsoft.Extensions.Logging;

namespace LTAI.Core.Setup;

public sealed record ModelDownloadProgress(
    long TotalBytes,
    long DownloadedBytes,
    double Percent,
    double SpeedMBps,
    string ModelName);

public sealed class ModelDownloader
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ModelDownloader>? _logger;

    public ModelDownloader(HttpClient? httpClient = null, ILogger<ModelDownloader>? logger = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.Timeout = TimeSpan.FromMinutes(30);
        _logger = logger;
    }

    public async Task<string> DownloadAsync(
        LocalModelInfo model,
        string modelsDir,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var layerDir = model.Layer.ToString().ToLowerInvariant();
        var dir = Path.Combine(modelsDir, layerDir);
        Directory.CreateDirectory(dir);

        var extension = model.EngineType.ToLowerInvariant();
        var fileName = model.Layer == ModelLayer.L0 && extension == "onnx"
            ? "model.onnx"
            : $"{model.Version}.{extension}";

        var filePath = Path.Combine(dir, fileName);

        if (File.Exists(filePath))
        {
            _logger?.LogInformation("✅ Model {Version} already exists at {Path}", model.Version, filePath);
            return filePath;
        }

        _logger?.LogInformation("📥 Downloading {ModelName} ({SizeMB} MB)...", model.Name, model.DiskSizeMB);

        // Try mirror first (hf-mirror.com for China), then direct URL
        var urlsToTry = new string[] { model.MirrorUrl, model.Url };
        Exception? lastError = null;

        foreach (var url in urlsToTry)
        {
            if (string.IsNullOrEmpty(url)) continue;

            try
            {
                _logger?.LogDebug("Trying URL: {Url}", url);
                await DownloadFileAsync(url, filePath, model, progress, ct).ConfigureAwait(false);
                _logger?.LogInformation("Downloaded successfully");

                if (model.Layer == ModelLayer.L0)
                {
                    await DownloadTokenizerAsync(url, dir, progress, ct).ConfigureAwait(false);
                }
                return filePath;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _logger?.LogWarning("Download failed from {Url}: {Error}", url, ex.Message);
            }
        }

        _logger?.LogError("❌ All download attempts failed for {ModelName}", model.Name);
        if (File.Exists(filePath)) File.Delete(filePath);
        throw new InvalidOperationException($"Failed to download {model.Name}. All sources failed.", lastError);
    }

    public bool IsModelInstalled(string version, string modelsDir)
    {
        var model = LocalModelRegistry.GetByVersion(version);
        if (model == null) return false;

        var layerDir = model.Layer.ToString().ToLowerInvariant();
        var dir = Path.Combine(modelsDir, layerDir);

        if (model.Layer == ModelLayer.L0 && model.EngineType == "onnx")
            return File.Exists(Path.Combine(dir, "model.onnx")) && File.Exists(Path.Combine(dir, "tokenizer.json"));

        var extension = model.EngineType.ToLowerInvariant();
        return File.Exists(Path.Combine(dir, $"{model.Version}.{extension}"));
    }

    public void RemoveModel(string version, string modelsDir)
    {
        var model = LocalModelRegistry.GetByVersion(version);
        if (model == null) throw new ArgumentException($"Unknown model version: {version}");

        var layerDir = model.Layer.ToString().ToLowerInvariant();
        var dir = Path.Combine(modelsDir, layerDir);

        if (model.Layer == ModelLayer.L0 && model.EngineType == "onnx")
        {
            var filePath = Path.Combine(dir, "model.onnx");
            if (File.Exists(filePath)) File.Delete(filePath);
            var tokPath = Path.Combine(dir, "tokenizer.json");
            if (File.Exists(tokPath)) File.Delete(tokPath);
        }
        else
        {
            var extension = model.EngineType.ToLowerInvariant();
            var filePath = Path.Combine(dir, $"{model.Version}.{extension}");
            if (File.Exists(filePath)) File.Delete(filePath);
        }

        _logger?.LogInformation("🗑️ Removed model {Version} ({Name})", version, model.Name);
    }

    private async Task DownloadTokenizerAsync(
        string modelUrl,
        string outputDir,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken ct)
    {
        var tokenizerPath = Path.Combine(outputDir, "tokenizer.json");
        if (File.Exists(tokenizerPath)) return;

        var tokenizerUrl = modelUrl.Replace("model.onnx", "tokenizer.json");
        if (tokenizerUrl == modelUrl)
        {
            tokenizerUrl = modelUrl[..modelUrl.LastIndexOf('/')] + "/tokenizer.json";
        }

        try
        {
            _logger?.LogInformation("Downloading tokenizer.json...");
            using var response = await _httpClient.GetAsync(tokenizerUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var fileStream = new FileStream(tokenizerPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            await stream.CopyToAsync(fileStream, ct).ConfigureAwait(false);
            _logger?.LogInformation("Tokenizer downloaded successfully");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Tokenizer download from {Url} failed, embedding may use hash fallback", tokenizerUrl);
        }
    }

    private async Task DownloadFileAsync(
        string url,
        string path,
        LocalModelInfo model,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken ct)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

        var buffer = new byte[81920];
        var totalRead = 0L;
        int bytesRead;
        var lastReportTime = DateTime.UtcNow;
        var lastReportBytes = 0L;

        while ((bytesRead = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
            totalRead += bytesRead;

            var now = DateTime.UtcNow;
            if ((now - lastReportTime).TotalSeconds >= 2)
            {
                var elapsed = (now - lastReportTime).TotalSeconds;
                var deltaBytes = totalRead - lastReportBytes;
                var speedMBps = deltaBytes / elapsed / 1024.0 / 1024.0;
                var percent = totalBytes > 0 ? (double)totalRead / totalBytes * 100 : 0;

                progress?.Report(new ModelDownloadProgress(
                    totalBytes, totalRead, percent, speedMBps, model.Name));

                _logger?.LogDebug("📥 {Name}: {Pct:F1}% ({Mb:F1}/{TotalMb:F1} MB, {Speed:F1} MB/s)",
                    model.Name, percent, totalRead / 1024.0 / 1024.0,
                    totalBytes > 0 ? totalBytes / 1024.0 / 1024.0 : 0, speedMBps);

                lastReportTime = now;
                lastReportBytes = totalRead;
            }
        }

        _logger?.LogInformation("✅ {Name} downloaded ({Size:F1} MB)", model.Name, totalRead / 1024.0 / 1024.0);
    }
}
