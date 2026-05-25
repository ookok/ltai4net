using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

// ==================== 细胞包格式定义 ====================

public enum CellPackageFormat { Onnx, Mlnet, Hybrid }
public enum CellCompression { None, Gzip, Brotli, Quantized }

public record CellDependency
{
    public string CellId { get; init; } = "";
    public string Domain { get; init; } = "";
    public string MinVersion { get; init; } = "";
    public bool IsRequired { get; init; } = true;
    public int LoadOrder { get; init; }
}

public record CellPackageManifest
{
    public string CellId { get; init; } = "";
    public string Domain { get; init; } = "";
    public string Version { get; init; } = "1.0.0";
    public string Author { get; init; } = "";
    public string Description { get; init; } = "";
    public CellPackageFormat Format { get; init; }
    public CellCompression Compression { get; init; }
    
    // 大小控制
    public long ModelSizeBytes { get; init; }
    public long TotalSizeBytes { get; init; }
    public int MaxSizeLimitMB { get; init; } = 50;
    public bool IsQuantized { get; init; }
    public int ShardCount { get; init; } = 1;
    
    // 性能指标
    public float Accuracy { get; init; }
    public float AvgLatencyMs { get; init; }
    public int TrainingSamples { get; init; }
    public string[] Labels { get; init; } = Array.Empty<string>();
    public int MaxSequenceLength { get; init; } = 128;
    
    // 依赖关系
    public List<CellDependency> Dependencies { get; init; } = new();
    public List<string> Tags { get; init; } = new();
    
    // 元数据
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; init; }
    public string ChecksumSHA256 { get; init; } = "";
    public string License { get; init; } = "MIT";
    
    // 级联加载配置
    public int CascadePriority { get; init; } = 0;  // 0=最高优先级
    public bool LazyLoad { get; init; } = true;
    public TimeSpan? AutoUnloadAfter { get; init; }
}

public record CellPackageInfo
{
    public string CellId { get; init; } = "";
    public string Domain { get; init; } = "";
    public string Version { get; init; } = "";
    public string LocalPath { get; init; } = "";
    public CellPackageManifest Manifest { get; init; } = null!;
    public bool IsLoaded { get; init; }
    public bool IsValid { get; init; }
    public DateTime DownloadedAt { get; init; }
    public string Source { get; init; } = "";  // "github", "local", "peer"
}

// ==================== 细胞包管理器 ====================

public sealed class CellPackageManager
{
    private readonly ILogger<CellPackageManager> _logger;
    private readonly string _packagesDirectory;
    private readonly Dictionary<string, CellPackageInfo> _installedPackages = new();
    private readonly object _lock = new();
    private readonly OnnxInt8Quantizer? _quantizer;

    public CellPackageManager(
        string packagesDirectory,
        ILogger<CellPackageManager>? logger = null,
        OnnxInt8Quantizer? quantizer = null)
    {
        _packagesDirectory = packagesDirectory;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CellPackageManager>.Instance;
        _quantizer = quantizer;
        Directory.CreateDirectory(_packagesDirectory);
        
        LoadInstalledPackages();
    }

    /// <summary>
    /// 打包细胞 AI 模型为可分发格式
    /// </summary>
    public async Task<string> PackageCellAsync(
        string domain,
        string modelPath,
        CellPackageManifest manifest,
        CancellationToken ct = default)
    {
        var packageDir = Path.Combine(_packagesDirectory, $"{manifest.CellId}_{manifest.Version}");
        Directory.CreateDirectory(packageDir);

        // 1. 验证大小限制
        var modelSize = new FileInfo(modelPath).Length;
        var maxBytes = manifest.MaxSizeLimitMB * 1024L * 1024L;
        if (modelSize > maxBytes)
        {
            throw new InvalidOperationException(
                $"Model size {modelSize / 1024.0 / 1024.0:F1}MB exceeds limit {manifest.MaxSizeLimitMB}MB");
        }

        // 2. 压缩/量化模型
        var compressedPath = await CompressModelAsync(modelPath, manifest.Compression, packageDir, ct).ConfigureAwait(false);
        
        // 3. 如果超过分片阈值，进行分片
        var shardPaths = await ShardIfNeededAsync(compressedPath, manifest, packageDir, ct).ConfigureAwait(false);

        // 4. 生成校验和
        var checksum = await ComputeChecksumAsync(compressedPath, ct).ConfigureAwait(false);
        manifest = manifest with
        {
            ModelSizeBytes = modelSize,
            TotalSizeBytes = shardPaths.Sum(p => new FileInfo(p).Length),
            ChecksumSHA256 = checksum,
            UpdatedAt = DateTime.UtcNow
        };

        // 5. 写入清单文件
        var manifestPath = Path.Combine(packageDir, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions), ct).ConfigureAwait(false);

        // 6. 创建 .cellpackage 归档
        var packagePath = Path.Combine(_packagesDirectory, $"{manifest.CellId}_{manifest.Version}.cellpackage");
        await CreatePackageArchiveAsync(packageDir, packagePath, ct).ConfigureAwait(false);

        // 7. 注册已安装的包
        var packageInfo = new CellPackageInfo
        {
            CellId = manifest.CellId,
            Domain = manifest.Domain,
            Version = manifest.Version,
            LocalPath = packagePath,
            Manifest = manifest,
            IsLoaded = false,
            IsValid = true,
            DownloadedAt = DateTime.UtcNow,
            Source = "local"
        };

        lock (_lock)
        {
            _installedPackages[manifest.CellId] = packageInfo;
        }

        _logger.LogInformation(
            "Cell packaged: id={Id} domain={Domain} version={Version} size={SizeKB:F1}KB shards={Shards}",
            manifest.CellId, manifest.Domain, manifest.Version,
            manifest.TotalSizeBytes / 1024.0, shardPaths.Count);

        return packagePath;
    }

    /// <summary>
    /// 解压并验证细胞包
    /// </summary>
    public async Task<CellPackageInfo?> InstallPackageAsync(
        string packagePath,
        CancellationToken ct = default)
    {
        if (!File.Exists(packagePath))
        {
            _logger.LogWarning("Package not found: {Path}", packagePath);
            return null;
        }

        var extractDir = Path.Combine(_packagesDirectory, "extracted", Path.GetFileNameWithoutExtension(packagePath));
        Directory.CreateDirectory(extractDir);

        try
        {
            // 1. 解压
            await ExtractPackageArchiveAsync(packagePath, extractDir, ct).ConfigureAwait(false);

            // 2. 读取清单
            var manifestPath = Path.Combine(extractDir, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                _logger.LogWarning("Manifest not found in package: {Path}", packagePath);
                return null;
            }

            var manifest = JsonSerializer.Deserialize<CellPackageManifest>(
                await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false), JsonOptions);
            if (manifest == null) return null;

            // 3. 验证校验和
            var modelPath = Path.Combine(extractDir, "model.onnx");
            if (File.Exists(modelPath))
            {
                var checksum = await ComputeChecksumAsync(modelPath, ct).ConfigureAwait(false);
                if (checksum != manifest.ChecksumSHA256)
                {
                    _logger.LogWarning("Checksum mismatch for package: {Id}", manifest.CellId);
                    return null;
                }
            }

            // 4. 解压缩（如果需要）
            var decompressedPath = await DecompressIfNeededAsync(modelPath, manifest.Compression, extractDir, ct).ConfigureAwait(false);

            // 5. 合并分片（如果需要）
            var finalModelPath = await MergeShardsIfNeededAsync(extractDir, manifest, ct).ConfigureAwait(false);

            var packageInfo = new CellPackageInfo
            {
                CellId = manifest.CellId,
                Domain = manifest.Domain,
                Version = manifest.Version,
                LocalPath = finalModelPath,
                Manifest = manifest,
                IsLoaded = false,
                IsValid = true,
                DownloadedAt = DateTime.UtcNow,
                Source = "github"
            };

            lock (_lock)
            {
                _installedPackages[manifest.CellId] = packageInfo;
            }

            _logger.LogInformation(
                "Package installed: id={Id} domain={Domain} version={Version}",
                manifest.CellId, manifest.Domain, manifest.Version);

            return packageInfo;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to install package: {Path}", packagePath);
            return null;
        }
    }

    /// <summary>
    /// 获取已安装的包
    /// </summary>
    public CellPackageInfo? GetPackage(string cellId)
    {
        lock (_lock)
        {
            return _installedPackages.GetValueOrDefault(cellId);
        }
    }

    /// <summary>
    /// 获取所有已安装的包
    /// </summary>
    public List<CellPackageInfo> GetInstalledPackages()
    {
        lock (_lock)
        {
            return _installedPackages.Values.ToList();
        }
    }

    /// <summary>
    /// 卸载包
    /// </summary>
    public bool UninstallPackage(string cellId)
    {
        lock (_lock)
        {
            if (_installedPackages.Remove(cellId, out var package))
            {
                try
                {
                    var extractDir = Path.GetDirectoryName(package.LocalPath);
                    if (Directory.Exists(extractDir))
                    {
                        Directory.Delete(extractDir, true);
                    }
                    if (File.Exists(package.LocalPath))
                    {
                        File.Delete(package.LocalPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to cleanup package files: {Id}", cellId);
                }

                _logger.LogInformation("Package uninstalled: {Id}", cellId);
                return true;
            }
            return false;
        }
    }

    // ==================== 内部方法 ====================

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private void LoadInstalledPackages()
    {
        foreach (var manifestFile in Directory.GetFiles(_packagesDirectory, "manifest.json", SearchOption.AllDirectories))
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<CellPackageManifest>(
                    File.ReadAllText(manifestFile), JsonOptions);
                if (manifest != null)
                {
                    _installedPackages[manifest.CellId] = new CellPackageInfo
                    {
                        CellId = manifest.CellId,
                        Domain = manifest.Domain,
                        Version = manifest.Version,
                        LocalPath = Path.GetDirectoryName(manifestFile)!,
                        Manifest = manifest,
                        IsLoaded = false,
                        IsValid = true,
                        DownloadedAt = DateTime.UtcNow,
                        Source = "local"
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load manifest: {File}", manifestFile);
            }
        }
    }

    private async Task<string> CompressModelAsync(
        string modelPath,
        CellCompression compression,
        string outputDir,
        CancellationToken ct)
    {
        if (compression == CellCompression.None)
        {
            var destPath = Path.Combine(outputDir, "model.onnx");
            File.Copy(modelPath, destPath, true);
            return destPath;
        }

        var compressedPath = compression switch
        {
            CellCompression.Gzip => Path.Combine(outputDir, "model.onnx.gz"),
            CellCompression.Brotli => Path.Combine(outputDir, "model.onnx.br"),
            CellCompression.Quantized => Path.Combine(outputDir, "model_quantized.onnx"),
            _ => throw new ArgumentException("Unknown compression type")
        };

        if (compression == CellCompression.Quantized)
        {
            if (_quantizer is not null)
            {
                try
                {
                    var result = await _quantizer.QuantizeAsync(modelPath, compressedPath, ct).ConfigureAwait(false);
                    _logger.LogInformation(
                        "Model quantized via ONNX Runtime: {Path} (ratio={Ratio:F1}%, orig={Orig}MB, quantized={Quant}MB)",
                        compressedPath, result.CompressionRatio * 100, result.OriginalSizeMB, result.QuantizedSizeMB);
                    return compressedPath;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ONNX quantization failed, falling back to copy: {Path}", modelPath);
                }
            }

            File.Copy(modelPath, compressedPath, true);
            _logger.LogInformation("Model quantized (copy fallback): {Path}", compressedPath);
            return compressedPath;
        }

        await using var sourceStream = File.OpenRead(modelPath);
        await using var destStream = File.Create(compressedPath);

        if (compression == CellCompression.Gzip)
        {
            await using var gzipStream = new GZipStream(destStream, CompressionLevel.Optimal);
            await sourceStream.CopyToAsync(gzipStream, ct).ConfigureAwait(false);
        }
        else if (compression == CellCompression.Brotli)
        {
            await using var brotliStream = new BrotliStream(destStream, CompressionLevel.Optimal);
            await sourceStream.CopyToAsync(brotliStream, ct).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Model compressed: {Original} -> {Compressed} ({Ratio:F1}%)",
            modelPath, compressedPath,
            new FileInfo(compressedPath).Length / (double)new FileInfo(modelPath).Length * 100);

        return compressedPath;
    }

    private async Task<List<string>> ShardIfNeededAsync(
        string modelPath,
        CellPackageManifest manifest,
        string outputDir,
        CancellationToken ct)
    {
        var shardSizeMB = 10;  // 每片 10MB
        var fileSize = new FileInfo(modelPath).Length;
        var shardSizeBytes = shardSizeMB * 1024L * 1024L;

        if (fileSize <= shardSizeBytes)
        {
            return new List<string> { modelPath };
        }

        var shardPaths = new List<string>();
        var shardIndex = 0;

        await using var sourceStream = File.OpenRead(modelPath);
        var buffer = new byte[81920];  // 80KB 缓冲区

        while (sourceStream.Position < sourceStream.Length)
        {
            var shardPath = Path.Combine(outputDir, $"model.shard.{shardIndex:D3}");
            await using var shardStream = File.Create(shardPath);
            long bytesWritten = 0;

            while (bytesWritten < shardSizeBytes && sourceStream.Position < sourceStream.Length)
            {
                var bytesToRead = (int)Math.Min(buffer.Length, shardSizeBytes - bytesWritten);
                var bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, bytesToRead), ct).ConfigureAwait(false);
                if (bytesRead == 0) break;

                await shardStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                bytesWritten += bytesRead;
            }

            shardPaths.Add(shardPath);
            shardIndex++;
        }

        manifest = manifest with { ShardCount = shardPaths.Count };

        _logger.LogInformation(
            "Model sharded: {Count} shards of {SizeMB}MB each",
            shardPaths.Count, shardSizeMB);

        return shardPaths;
    }

    private async Task<string> ComputeChecksumAsync(string filePath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task CreatePackageArchiveAsync(string sourceDir, string packagePath, CancellationToken ct)
    {
        if (File.Exists(packagePath))
        {
            File.Delete(packagePath);
        }

        await Task.Run(() => ZipFile.CreateFromDirectory(sourceDir, packagePath), ct).ConfigureAwait(false);
    }

    private static async Task ExtractPackageArchiveAsync(string packagePath, string extractDir, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            if (Directory.Exists(extractDir))
            {
                Directory.Delete(extractDir, true);
            }
            ZipFile.ExtractToDirectory(packagePath, extractDir);
        }, ct);
    }

    private async Task<string> DecompressIfNeededAsync(
        string modelPath,
        CellCompression compression,
        string outputDir,
        CancellationToken ct)
    {
        if (compression == CellCompression.None || !File.Exists(modelPath))
        {
            return modelPath;
        }

        var decompressedPath = Path.Combine(outputDir, "model_decompressed.onnx");

        if (compression == CellCompression.Gzip)
        {
            await using var sourceStream = File.OpenRead(modelPath);
            await using var gzipStream = new GZipStream(sourceStream, CompressionMode.Decompress);
            await using var destStream = File.Create(decompressedPath);
            await gzipStream.CopyToAsync(destStream, ct).ConfigureAwait(false);
        }
        else if (compression == CellCompression.Brotli)
        {
            await using var sourceStream = File.OpenRead(modelPath);
            await using var brotliStream = new BrotliStream(sourceStream, CompressionMode.Decompress);
            await using var destStream = File.Create(decompressedPath);
            await brotliStream.CopyToAsync(destStream, ct).ConfigureAwait(false);
        }
        else
        {
            return modelPath;
        }

        _logger.LogInformation("Model decompressed: {Path}", decompressedPath);
        return decompressedPath;
    }

    private async Task<string> MergeShardsIfNeededAsync(
        string extractDir,
        CellPackageManifest manifest,
        CancellationToken ct)
    {
        if (manifest.ShardCount <= 1)
        {
            var singlePath = Path.Combine(extractDir, "model.onnx");
            if (File.Exists(singlePath)) return singlePath;
            return Path.Combine(extractDir, "model_decompressed.onnx");
        }

        var mergedPath = Path.Combine(extractDir, "model_merged.onnx");
        var shards = Directory.GetFiles(extractDir, "model.shard.*").OrderBy(f => f).ToList();

        await using var destStream = File.Create(mergedPath);
        var buffer = new byte[81920];

        foreach (var shard in shards)
        {
            await using var sourceStream = File.OpenRead(shard);
            await sourceStream.CopyToAsync(destStream, buffer.Length, ct).ConfigureAwait(false);
        }

        _logger.LogInformation("Shards merged: {Count} -> {Path}", shards.Count, mergedPath);
        return mergedPath;
    }
}
