using LTAI.Core.Governors;
using Microsoft.Extensions.Logging;

namespace LTAI.Core.Network;

public record DownloadProgress
{
    public string ModelVersion { get; init; } = "";
    public long TotalBytes { get; set; }
    public long DownloadedBytes { get; set; }
    public double Percent => TotalBytes > 0 ? (double)DownloadedBytes / TotalBytes * 100 : 0;
    public string Status { get; set; } = "pending"; // pending, downloading, verifying, complete, failed
    public string? Error { get; set; }
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
}

public record DownloadResult
{
    public bool Success { get; init; }
    public string LocalPath { get; init; } = "";
    public long FileSizeBytes { get; init; }
    public string? Sha256 { get; init; }
    public TimeSpan Duration { get; init; }
    public string? Error { get; init; }
}

public sealed class ModelAutoDownloader
{
    private readonly HttpClient _http;
    private readonly string _modelsRoot;
    private readonly ILogger<ModelAutoDownloader> _logger;
    private readonly int _maxRetries;

    public event Action<DownloadProgress>? OnProgress;
    public event Action<string, bool>? OnComplete;

    private static readonly HashSet<string> DownloadedVersions = new();

    public ModelAutoDownloader(
        string? modelsRoot = null,
        ILogger<ModelAutoDownloader>? logger = null,
        int maxRetries = 3)
    {
        _modelsRoot = modelsRoot ?? global::System.IO.Path.Combine(AppContext.BaseDirectory, "models");
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ModelAutoDownloader>.Instance;
        _maxRetries = maxRetries;
        _http = new HttpClient { Timeout = TimeSpan.FromHours(2) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("LTAI-CellularAI/5.5");

        global::System.IO.Directory.CreateDirectory(_modelsRoot);
    }

    public async Task<DownloadResult> DownloadAsync(
        LocalModelInfo model,
        CancellationToken ct = default)
    {
        var modelDir = global::System.IO.Path.Combine(_modelsRoot, model.Version);
        global::System.IO.Directory.CreateDirectory(modelDir);

        var fileName = GetFileName(model.Url);
        var localPath = global::System.IO.Path.Combine(modelDir, fileName);

        // Already downloaded?
        if (global::System.IO.File.Exists(localPath))
        {
            var existingSize = new global::System.IO.FileInfo(localPath).Length;
            _logger.LogInformation("Model already downloaded: {Version} ({Size}MB)",
                model.Version, existingSize / 1024 / 1024);
            return new DownloadResult { Success = true, LocalPath = localPath, FileSizeBytes = existingSize };
        }

        return await DownloadWithRetryAsync(model, localPath, ct).ConfigureAwait(false);
    }

    private async Task<DownloadResult> DownloadWithRetryAsync(
        LocalModelInfo model, string localPath, CancellationToken ct)
    {
        var urls = new[] { model.Url, model.MirrorUrl }
            .Where(u => !string.IsNullOrEmpty(u)).Distinct().ToArray();

        var progress = new DownloadProgress
        {
            ModelVersion = model.Version,
            Status = "downloading"
        };

        for (int retry = 0; retry < _maxRetries; retry++)
        {
            ct.ThrowIfCancellationRequested();

            for (int urlIdx = 0; urlIdx < urls.Length; urlIdx++)
            {
                try
                {
                    var url = urls[urlIdx];
                    _logger.LogInformation("Downloading {Version} from {Url} (attempt {Attempt})",
                        model.Version, url[..Math.Min(url.Length, 80)], retry + 1);

                    using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();

                    progress.TotalBytes = response.Content.Headers.ContentLength ?? 0;
                    progress.DownloadedBytes = 0;
                    OnProgress?.Invoke(progress);

                    var tempPath = localPath + ".download";
                    await using var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using var fileStream = global::System.IO.File.Create(tempPath);

                    var buffer = new byte[8192];
                    int bytesRead;
                    while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                        progress.DownloadedBytes += bytesRead;
                        OnProgress?.Invoke(progress);
                    }

                    // Verify
                    progress.Status = "verifying";
                    OnProgress?.Invoke(progress);

                    var finalSize = new global::System.IO.FileInfo(tempPath).Length;
                    if (finalSize < 1024)
                    {
                        global::System.IO.File.Delete(tempPath);
                        throw new Exception($"Downloaded file too small: {finalSize} bytes");
                    }

                    // SHA256 optional verify
                    var sha256 = model.Sha256 != "auto_verify"
                        ? await VerifySha256Async(tempPath, model.Sha256, ct)
                        : "auto";

                    global::System.IO.File.Move(tempPath, localPath);

                    progress.Status = "complete";
                    OnProgress?.Invoke(progress);
                    OnComplete?.Invoke(model.Version, true);

                    _logger.LogInformation("Model downloaded: {Version} ({Size}MB) at {Path}",
                        model.Version, finalSize / 1024 / 1024, localPath);

                    return new DownloadResult
                    {
                        Success = true, LocalPath = localPath,
                        FileSizeBytes = finalSize, Sha256 = sha256,
                        Duration = DateTime.UtcNow - progress.StartedAt
                    };
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Download failed for {Version} (url {Idx}, retry {Retry})",
                        model.Version, urlIdx, retry);
                    if (urlIdx == urls.Length - 1 && retry == _maxRetries - 1)
                    {
                        progress.Status = "failed";
                        progress.Error = ex.Message;
                        OnProgress?.Invoke(progress);
                        OnComplete?.Invoke(model.Version, false);

                        return new DownloadResult
                        {
                            Error = ex.Message, Duration = DateTime.UtcNow - progress.StartedAt
                        };
                    }
                    await Task.Delay(TimeSpan.FromSeconds(retry * 5), ct).ConfigureAwait(false);
                }
            }
        }

        return new DownloadResult { Error = "All download attempts failed" };
    }

    public async Task<List<DownloadResult>> DownloadRecommendationAsync(
        ModelRecommendation recommendation,
        CancellationToken ct = default)
    {
        var results = new List<DownloadResult>();

        _logger.LogInformation("Auto-downloading recommended models: L0={L0}, L1={L1}, L2={L2}",
            recommendation.L0Embedding.Version, recommendation.L1Fast.Version,
            recommendation.L2Deep?.Version ?? "none");

        results.Add(await DownloadAsync(recommendation.L0Embedding, ct).ConfigureAwait(false));
        results.Add(await DownloadAsync(recommendation.L1Fast, ct).ConfigureAwait(false));
        if (recommendation.L2Deep is not null)
            results.Add(await DownloadAsync(recommendation.L2Deep, ct).ConfigureAwait(false));

        return results;
    }

    public static string GetModelPath(string version, string? modelsRoot = null)
    {
        var root = modelsRoot ?? global::System.IO.Path.Combine(AppContext.BaseDirectory, "models");
        var modelDir = global::System.IO.Path.Combine(root, version);
        if (!global::System.IO.Directory.Exists(modelDir)) return "";

        return global::System.IO.Directory.GetFiles(modelDir).FirstOrDefault() ?? "";
    }

    public bool IsDownloaded(string version)
    {
        var modelDir = global::System.IO.Path.Combine(_modelsRoot, version);
        return global::System.IO.Directory.Exists(modelDir)
            && global::System.IO.Directory.GetFiles(modelDir).Length > 0;
    }

    public long GetLocalModelSize(string version)
    {
        var modelDir = global::System.IO.Path.Combine(_modelsRoot, version);
        if (!global::System.IO.Directory.Exists(modelDir)) return 0;

        return global::System.IO.Directory.GetFiles(modelDir).Sum(f =>
            new global::System.IO.FileInfo(f).Length);
    }

    private static string GetFileName(string url)
    {
        var uri = new Uri(url);
        var segments = uri.AbsolutePath.Split('/');
        var fileName = segments.LastOrDefault(s => s.Contains('.')) ?? "model.bin";
        return fileName;
    }

    private static async Task<string> VerifySha256Async(string path, string expected, CancellationToken ct)
    {
        await using var stream = global::System.IO.File.OpenRead(path);
        var hash = await global::System.Security.Cryptography.SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        var actual = Convert.ToHexStringLower(hash);
        if (!string.Equals(expected, "auto_verify", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new Exception($"SHA256 mismatch: expected {expected}, got {actual}");
        }
        return actual;
    }
}
