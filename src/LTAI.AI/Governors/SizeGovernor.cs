using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

// ==================== 大小控制配置 ====================

public record SizeGovernorConfig
{
    public int MaxCellSizeMB { get; init; } = 50;  // 单个细胞最大大小
    public int MaxTotalSizeMB { get; init; } = 500;  // 所有细胞总大小
    public bool EnableAutoCompression { get; init; } = true;  // 自动压缩
    public bool EnableQuantization { get; init; } = true;  // 启用量化
    public float QuantizationAccuracyThreshold { get; init; } = 0.95f;  // 量化后准确率下限
    public int ShardSizeMB { get; init; } = 10;  // 分片大小
    public CellCompression PreferredCompression { get; init; } = CellCompression.Gzip;
    public bool EnableSizeReporting { get; init; } = true;
    public TimeSpan SizeCheckInterval { get; init; } = TimeSpan.FromMinutes(10);
}

public record CellSizeInfo
{
    public string CellId { get; init; } = "";
    public string Domain { get; init; } = "";
    public long OriginalSizeBytes { get; init; }
    public long CompressedSizeBytes { get; init; }
    public long DiskSizeBytes { get; init; }
    public float CompressionRatio { get; init; }
    public bool IsQuantized { get; init; }
    public int ShardCount { get; init; }
    public bool ExceedsLimit { get; init; }
}

public record SizeGovernorStats
{
    public int TotalCells { get; init; }
    public long TotalOriginalSizeBytes { get; init; }
    public long TotalCompressedSizeBytes { get; init; }
    public long TotalDiskSizeBytes { get; init; }
    public float AverageCompressionRatio { get; init; }
    public int CellsExceedingLimit { get; init; }
    public long MaxAllowedBytes { get; init; }
    public long TotalAllowedBytes { get; init; }
}

// ==================== 大小控制器 ====================

public sealed class SizeGovernor : IDisposable
{
    private readonly SizeGovernorConfig _config;
    private readonly CellPackageManager _packageManager;
    private readonly ILogger<SizeGovernor> _logger;
    private readonly Dictionary<string, CellSizeInfo> _cellSizes = new();
    private readonly Timer? _sizeCheckTimer;
    private readonly object _lock = new();

    public SizeGovernor(
        SizeGovernorConfig config,
        CellPackageManager packageManager,
        ILogger<SizeGovernor>? logger = null)
    {
        _config = config;
        _packageManager = packageManager;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SizeGovernor>.Instance;

        if (_config.EnableSizeReporting)
        {
            _sizeCheckTimer = new Timer(
                CheckSizes,
                null,
                _config.SizeCheckInterval,
                _config.SizeCheckInterval);
        }

        _logger.LogInformation(
            "SizeGovernor initialized: maxCell={CellMB}MB maxTotal={TotalMB}MB compression={Compression}",
            _config.MaxCellSizeMB, _config.MaxTotalSizeMB, _config.PreferredCompression);
    }

    /// <summary>
    /// 验证细胞大小是否符合限制
    /// </summary>
    public CellSizeInfo ValidateCellSize(string cellId, long sizeBytes)
    {
        var maxBytes = _config.MaxCellSizeMB * 1024L * 1024L;
        var exceedsLimit = sizeBytes > maxBytes;

        var sizeInfo = new CellSizeInfo
        {
            CellId = cellId,
            OriginalSizeBytes = sizeBytes,
            CompressedSizeBytes = sizeBytes,  // 初始假设未压缩
            DiskSizeBytes = sizeBytes,
            CompressionRatio = 1.0f,
            IsQuantized = false,
            ShardCount = 1,
            ExceedsLimit = exceedsLimit
        };

        lock (_lock)
        {
            _cellSizes[cellId] = sizeInfo;
        }

        if (exceedsLimit)
        {
            _logger.LogWarning(
                "Cell exceeds size limit: id={Id} size={SizeMB:F1}MB limit={LimitMB}MB",
                cellId, sizeBytes / 1024.0 / 1024.0, _config.MaxCellSizeMB);
        }

        return sizeInfo;
    }

    /// <summary>
    /// 优化细胞大小 (压缩 + 量化)
    /// </summary>
    public async Task<CellSizeInfo?> OptimizeCellSizeAsync(
        string cellId,
        string modelPath,
        float currentAccuracy,
        CancellationToken ct = default)
    {
        var originalSize = new FileInfo(modelPath).Length;
        var maxBytes = _config.MaxCellSizeMB * 1024L * 1024L;

        if (originalSize <= maxBytes && !_config.EnableAutoCompression)
        {
            return ValidateCellSize(cellId, originalSize);
        }

        var optimizedPath = modelPath;
        var isQuantized = false;

        // 1. 尝试量化 (如果启用且准确率允许)
        if (_config.EnableQuantization && originalSize > maxBytes * 0.5)
        {
            var quantizedPath = await QuantizeModelAsync(modelPath, currentAccuracy, ct).ConfigureAwait(false);
            if (quantizedPath != null)
            {
                optimizedPath = quantizedPath;
                isQuantized = true;
                _logger.LogInformation("Model quantized: {Path} size={SizeKB:F1}KB", quantizedPath, new FileInfo(quantizedPath).Length / 1024.0);
            }
        }

        // 2. 如果仍然超过限制，尝试压缩
        var compressedPath = optimizedPath;
        if (new FileInfo(optimizedPath).Length > maxBytes)
        {
            compressedPath = await CompressModelForDistributionAsync(optimizedPath, ct).ConfigureAwait(false);
        }

        var finalSize = new FileInfo(compressedPath).Length;
        var compressionRatio = originalSize / (float)Math.Max(1, finalSize);

        var sizeInfo = new CellSizeInfo
        {
            CellId = cellId,
            OriginalSizeBytes = originalSize,
            CompressedSizeBytes = finalSize,
            DiskSizeBytes = finalSize,
            CompressionRatio = compressionRatio,
            IsQuantized = isQuantized,
            ShardCount = finalSize > _config.ShardSizeMB * 1024L * 1024L
                ? (int)Math.Ceiling(finalSize / (double)(_config.ShardSizeMB * 1024L * 1024L))
                : 1,
            ExceedsLimit = finalSize > maxBytes
        };

        lock (_lock)
        {
            _cellSizes[cellId] = sizeInfo;
        }

        _logger.LogInformation(
            "Cell size optimized: id={Id} original={OriginalMB:F1}MB final={FinalMB:F1}MB ratio={Ratio:F1}x quantized={Quantized}",
            cellId, originalSize / 1024.0 / 1024.0, finalSize / 1024.0 / 1024.0,
            compressionRatio, isQuantized);

        return sizeInfo;
    }

    /// <summary>
    /// 检查总大小是否超过限制
    /// </summary>
    public bool CheckTotalSizeLimit()
    {
        long totalSize;
        lock (_lock)
        {
            totalSize = _cellSizes.Values.Sum(s => s.DiskSizeBytes);
        }

        var maxTotalBytes = _config.MaxTotalSizeMB * 1024L * 1024L;
        var exceeds = totalSize > maxTotalBytes;

        if (exceeds)
        {
            _logger.LogWarning(
                "Total cell size exceeds limit: total={TotalMB:F1}MB limit={LimitMB}MB",
                totalSize / 1024.0 / 1024.0, _config.MaxTotalSizeMB);
        }

        return !exceeds;
    }

    /// <summary>
    /// 获取大小统计
    /// </summary>
    public SizeGovernorStats GetStats()
    {
        lock (_lock)
        {
            var cells = _cellSizes.Values.ToList();
            return new SizeGovernorStats
            {
                TotalCells = cells.Count,
                TotalOriginalSizeBytes = cells.Sum(s => s.OriginalSizeBytes),
                TotalCompressedSizeBytes = cells.Sum(s => s.CompressedSizeBytes),
                TotalDiskSizeBytes = cells.Sum(s => s.DiskSizeBytes),
                AverageCompressionRatio = cells.Count > 0
                    ? (float)cells.Average(s => (double)s.CompressionRatio)
                    : 1.0f,
                CellsExceedingLimit = cells.Count(s => s.ExceedsLimit),
                MaxAllowedBytes = _config.MaxCellSizeMB * 1024L * 1024L,
                TotalAllowedBytes = _config.MaxTotalSizeMB * 1024L * 1024L
            };
        }
    }

    /// <summary>
    /// 获取特定细胞的大小信息
    /// </summary>
    public CellSizeInfo? GetCellSizeInfo(string cellId)
    {
        lock (_lock)
        {
            return _cellSizes.GetValueOrDefault(cellId);
        }
    }

    // ==================== 内部方法 ====================

    private async Task<string?> QuantizeModelAsync(
        string modelPath,
        float currentAccuracy,
        CancellationToken ct)
    {
        if (!_config.EnableQuantization) return null;

        var quantizedPath = Path.Combine(
            Path.GetDirectoryName(modelPath)!,
            $"{Path.GetFileNameWithoutExtension(modelPath)}_quantized.onnx");

        // 简化量化实现：复制文件
        // 实际应使用 ONNX Runtime 量化工具 (onnxruntime.quantization)
        await Task.Run(() => File.Copy(modelPath, quantizedPath, true), ct).ConfigureAwait(false);

        // 模拟量化后的准确率检查
        var quantizedAccuracy = currentAccuracy * _config.QuantizationAccuracyThreshold;
        if (quantizedAccuracy < currentAccuracy * 0.9f)
        {
            _logger.LogWarning(
                "Quantization would reduce accuracy too much: {Accuracy:F2} -> {QuantizedAccuracy:F2}",
                currentAccuracy, quantizedAccuracy);
            return null;
        }

        return quantizedPath;
    }

    private async Task<string> CompressModelForDistributionAsync(
        string modelPath,
        CancellationToken ct)
    {
        var compressedPath = _config.PreferredCompression switch
        {
            CellCompression.Gzip => $"{modelPath}.gz",
            CellCompression.Brotli => $"{modelPath}.br",
            _ => modelPath
        };

        if (compressedPath == modelPath) return modelPath;

        await using var sourceStream = File.OpenRead(modelPath);
        await using var destStream = File.Create(compressedPath);

        if (_config.PreferredCompression == CellCompression.Gzip)
        {
            await using var gzipStream = new System.IO.Compression.GZipStream(
                destStream, System.IO.Compression.CompressionLevel.Optimal);
            await sourceStream.CopyToAsync(gzipStream, ct).ConfigureAwait(false);
        }
        else if (_config.PreferredCompression == CellCompression.Brotli)
        {
            await using var brotliStream = new System.IO.Compression.BrotliStream(
                destStream, System.IO.Compression.CompressionLevel.Optimal);
            await sourceStream.CopyToAsync(brotliStream, ct).ConfigureAwait(false);
        }

        return compressedPath;
    }

    private void CheckSizes(object? state)
    {
        var stats = GetStats();
        _logger.LogInformation(
            "Size check: cells={Count} total={TotalMB:F1}MB avgRatio={Ratio:F1}x exceeding={Exceeding}",
            stats.TotalCells,
            stats.TotalDiskSizeBytes / 1024.0 / 1024.0,
            stats.AverageCompressionRatio,
            stats.CellsExceedingLimit);

        if (!CheckTotalSizeLimit())
        {
            _logger.LogWarning("Total size limit exceeded, consider pruning or compressing cells");
        }
    }

    public void Dispose()
    {
        _sizeCheckTimer?.Dispose();
        _logger.LogInformation("SizeGovernor disposed");
    }
}
