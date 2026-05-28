using LTAI.Core.Governors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.Setup;

public sealed record L0BootstrapResult
{
    public string DeviceTier { get; init; } = "";
    public long AvailableMemoryMB { get; init; }
    public int TotalModels { get; init; }
    public int Downloaded { get; init; }
    public int Skipped { get; init; }
    public int Failed { get; init; }
    public long TotalDownloadMB { get; init; }
    public TimeSpan Elapsed { get; init; }
    public List<L0DownloadItem> Items { get; init; } = new();
    public string Summary { get; init; } = "";
}

public sealed record L0DownloadItem
{
    public string Version { get; init; } = "";
    public string Name { get; init; } = "";
    public long DiskSizeMB { get; init; }
    public long RecommendedMemoryMB { get; init; }
    public string Category { get; init; } = "";
    public L0DownloadStatus Status { get; set; }
    public string? LocalPath { get; set; }
    public string? Error { get; set; }
}

public enum L0DownloadStatus
{
    Recommended,
    Optional,
    TooBig,
    AlreadyInstalled,
    Downloading,
    Downloaded,
    Failed,
    Skipped
}

public sealed class L0BootstrapDownloader
{
    private readonly ModelDownloader _modelDownloader;
    private readonly ILogger<L0BootstrapDownloader> _logger;
    private readonly string _modelsDir;
    private readonly int _maxParallelDownloads;

    public L0BootstrapDownloader(
        ModelDownloader modelDownloader,
        string? modelsDir = null,
        int maxParallelDownloads = 3,
        ILogger<L0BootstrapDownloader>? logger = null)
    {
        _modelDownloader = modelDownloader;
        _logger = logger ?? NullLogger<L0BootstrapDownloader>.Instance;
        _modelsDir = modelsDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LTAI", "models");
        _maxParallelDownloads = maxParallelDownloads;

        Directory.CreateDirectory(_modelsDir);
    }

    public List<L0DownloadItem> Scan()
    {
        var profile = DeviceProfiler.Profile(_logger as ILogger);
        var allL0 = LocalModelRegistry.GetByLayer(ModelLayer.L0);
        var items = new List<L0DownloadItem>();

        foreach (var model in allL0)
        {
            var category = DetermineCategory(model.Version);
            var installed = _modelDownloader.IsModelInstalled(model.Version, _modelsDir);

            var status = installed ? L0DownloadStatus.AlreadyInstalled
                : model.RecommendedMemoryMB > profile.AvailableMemoryMB * 0.6
                    ? L0DownloadStatus.TooBig
                    : IsCoreModel(model.Version)
                        ? L0DownloadStatus.Recommended
                        : L0DownloadStatus.Optional;

            items.Add(new L0DownloadItem
            {
                Version = model.Version,
                Name = model.Name,
                DiskSizeMB = model.DiskSizeMB,
                RecommendedMemoryMB = model.RecommendedMemoryMB,
                Category = category,
                Status = status,
                LocalPath = installed ? Path.Combine(_modelsDir, model.Version, model.Name.Contains("ONNX") ? "model.onnx" : "") : null
            });
        }

        return items.OrderBy(i => i.Status == L0DownloadStatus.Recommended ? 0 : 1)
                    .ThenBy(i => i.Category)
                    .ThenBy(i => i.DiskSizeMB)
                    .ToList();
    }

    public async Task<L0BootstrapResult> BootstrapAsync(
        bool downloadOptional = false,
        IProgress<L0DownloadItem>? progress = null,
        IProgress<string>? logProgress = null,
        CancellationToken ct = default)
    {
        var sw = global::System.Diagnostics.Stopwatch.StartNew();
        var profile = DeviceProfiler.Profile(_logger as ILogger);
        var items = Scan();

        var toDownload = items
            .Where(i => i.Status == L0DownloadStatus.Recommended ||
                       (downloadOptional && i.Status == L0DownloadStatus.Optional))
            .ToList();

        logProgress?.Report($"设备级别: {profile.Tier}");
        logProgress?.Report($"可用内存: {profile.AvailableMemoryMB} MB");
        logProgress?.Report($"模型目录: {_modelsDir}");
        logProgress?.Report($"");
        logProgress?.Report($"L0 模型扫描结果:");

        foreach (var item in items)
        {
            var icon = item.Status switch
            {
                L0DownloadStatus.AlreadyInstalled => "[已安装]",
                L0DownloadStatus.Recommended => "[★推荐]",
                L0DownloadStatus.Optional => "[可选]",
                L0DownloadStatus.TooBig => "[内存不足]",
                _ => ""
            };
            logProgress?.Report($"  {icon} {item.Name} ({item.DiskSizeMB}MB)");
        }

        logProgress?.Report($"");
        logProgress?.Report($"待下载: {toDownload.Count} 个模型 ({toDownload.Sum(i => i.DiskSizeMB)} MB)");
        logProgress?.Report($"");

        var downloaded = 0;
        var skipped = 0;
        var failed = 0;
        var totalMB = 0L;

        using var semaphore = new SemaphoreSlim(_maxParallelDownloads);
        var tasks = toDownload.Select(async item =>
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var model = LocalModelRegistry.GetByVersion(item.Version);
                if (model == null)
                {
                    item.Status = L0DownloadStatus.Failed;
                    item.Error = "Model not found in registry";
                    Interlocked.Increment(ref failed);
                    return;
                }

                item.Status = L0DownloadStatus.Downloading;
                progress?.Report(item);
                logProgress?.Report($"📥 下载中: {item.Name} ({item.DiskSizeMB}MB)...");

                try
                {
                    var modelProgress = new Progress<ModelDownloadProgress>(p =>
                    {
                        if (p.Percent % 20 < 0.1 || p.Percent >= 99)
                            logProgress?.Report($"   {item.Name}: {p.Percent:F0}% ({p.DownloadedBytes / 1024.0 / 1024.0:F0}/{p.TotalBytes / 1024.0 / 1024.0:F0} MB)");
                    });

                    var path = await _modelDownloader.DownloadAsync(model, _modelsDir, modelProgress, ct)
                        .ConfigureAwait(false);

                    item.Status = L0DownloadStatus.Downloaded;
                    item.LocalPath = path;
                    Interlocked.Increment(ref downloaded);
                    Interlocked.Add(ref totalMB, item.DiskSizeMB);

                    progress?.Report(item);
                    logProgress?.Report($"   ✓ {item.Name} 下载完成");
                }
                catch (OperationCanceledException)
                {
                    item.Status = L0DownloadStatus.Failed;
                    item.Error = "Cancelled";
                    Interlocked.Increment(ref failed);
                    throw;
                }
                catch (Exception ex)
                {
                    item.Status = L0DownloadStatus.Failed;
                    item.Error = ex.Message;
                    Interlocked.Increment(ref failed);

                    progress?.Report(item);
                    logProgress?.Report($"   ✗ {item.Name} 下载失败: {ex.Message}");
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        skipped = items.Count(i => i.Status is L0DownloadStatus.AlreadyInstalled or L0DownloadStatus.TooBig);
        sw.Stop();

        var result = new L0BootstrapResult
        {
            DeviceTier = profile.Tier.ToString(),
            AvailableMemoryMB = profile.AvailableMemoryMB,
            TotalModels = items.Count,
            Downloaded = downloaded,
            Skipped = skipped,
            Failed = failed,
            TotalDownloadMB = totalMB,
            Elapsed = sw.Elapsed,
            Items = items,
            Summary = downloaded > 0
                ? $"✓ 下载完成: {downloaded} 个 L0 模型, {totalMB} MB, 耗时 {sw.Elapsed.TotalSeconds:F0}s"
                : failed > 0
                    ? $"⚠ {failed} 个失败, 0 个成功"
                    : "✓ 所有 L0 模型已就绪 (无需下载)"
        };

        _logger.LogInformation("L0 Bootstrap: {Summary}", result.Summary);
        return result;
    }

    public async Task<L0BootstrapResult> BootstrapRecommendedOnlyAsync(
        IProgress<L0DownloadItem>? progress = null,
        CancellationToken ct = default)
    {
        var logProgress = new Progress<string>(s => _logger.LogInformation("{Msg}", s));
        return await BootstrapAsync(downloadOptional: false, progress, logProgress, ct).ConfigureAwait(false);
    }

    public async Task<L0BootstrapResult> BootstrapAllCompatibleAsync(
        IProgress<L0DownloadItem>? progress = null,
        CancellationToken ct = default)
    {
        var logProgress = new Progress<string>(s => _logger.LogInformation("{Msg}", s));
        return await BootstrapAsync(downloadOptional: true, progress, logProgress, ct).ConfigureAwait(false);
    }

    private static string DetermineCategory(string version)
    {
        if (version.Contains("bge", StringComparison.OrdinalIgnoreCase) ||
            version.Contains("jina", StringComparison.OrdinalIgnoreCase))
            return "Embedding (嵌入模型)";

        if (version.Contains("ocr", StringComparison.OrdinalIgnoreCase))
            return "OCR (文字识别)";

        if (version.Contains("supertonic", StringComparison.OrdinalIgnoreCase))
            return "TTS (语音合成)";

        if (version.Contains("needle", StringComparison.OrdinalIgnoreCase))
            return "Router (工具路由)";

        return "Other";
    }

    private static bool IsCoreModel(string version)
    {
        if (version.Contains("bge-small", StringComparison.OrdinalIgnoreCase))
            return true;
        if (version.Contains("rapidocr", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}
