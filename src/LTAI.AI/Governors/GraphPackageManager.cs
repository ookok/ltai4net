using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using LTAI.Knowledge.Core;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

// ==================== 图谱包格式定义 ====================

public enum GraphCompression { None, Gzip, Brotli }

public record GraphDependency
{
    public string GraphId { get; init; } = "";
    public string Domain { get; init; } = "";
    public string MinVersion { get; init; } = "";
    public bool IsRequired { get; init; } = true;
    public int LoadOrder { get; init; }
}

public record GraphPackageManifest
{
    public string GraphId { get; init; } = "";
    public string Domain { get; init; } = "";
    public string Version { get; init; } = "1.0.0";
    public string Author { get; init; } = "";
    public string Description { get; init; } = "";
    
    // 图谱统计
    public int EntityCount { get; init; }
    public int TripletCount { get; init; }
    public List<string> RelationTypes { get; init; } = new();
    
    // 大小控制
    public long TotalSizeBytes { get; init; }
    public long MaxSizeLimitMB { get; init; } = 100;
    public GraphCompression Compression { get; init; }
    public int ShardCount { get; init; } = 1;
    
    // 依赖关系
    public List<GraphDependency> Dependencies { get; init; } = new();
    public List<string> Tags { get; init; } = new();
    
    // 元数据
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; init; }
    public string ChecksumSHA256 { get; init; } = "";
    public string License { get; init; } = "CC-BY-4.0";
    
    // 级联加载配置
    public int CascadePriority { get; init; } = 0;
    public bool LazyLoad { get; init; } = true;
}

public record GraphPackageInfo
{
    public string GraphId { get; init; } = "";
    public string Domain { get; init; } = "";
    public string Version { get; init; } = "";
    public string LocalPath { get; init; } = "";
    public GraphPackageManifest Manifest { get; init; } = null!;
    public bool IsLoaded { get; init; }
    public bool IsValid { get; init; }
    public DateTime DownloadedAt { get; init; }
    public string Source { get; init; } = "";  // "github", "local", "generated"
}

// ==================== 图谱包管理器 ====================

public sealed class GraphPackageManager
{
    private readonly ILogger<GraphPackageManager> _logger;
    private readonly string _packagesDirectory;
    private readonly Dictionary<string, GraphPackageInfo> _installedPackages = new();
    private readonly object _lock = new();

    public GraphPackageManager(
        string packagesDirectory,
        ILogger<GraphPackageManager>? logger = null)
    {
        _packagesDirectory = packagesDirectory;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<GraphPackageManager>.Instance;
        Directory.CreateDirectory(_packagesDirectory);
        
        LoadInstalledPackages();
    }

    /// <summary>
    /// 打包知识图谱为可分发格式
    /// </summary>
    public async Task<string> PackageGraphAsync(
        KnowledgeGraph graph,
        GraphPackageManifest manifest,
        CancellationToken ct = default)
    {
        var packageDir = Path.Combine(_packagesDirectory, $"{manifest.GraphId}_{manifest.Version}");
        Directory.CreateDirectory(packageDir);

        try
        {
            // 1. 保存图谱数据
            var graphDataPath = Path.Combine(packageDir, "graph_data.json");
            await graph.SaveToDiskAsync(graphDataPath).ConfigureAwait(false);

            // 2. 验证大小限制
            var graphSize = new FileInfo(graphDataPath).Length;
            var maxBytes = manifest.MaxSizeLimitMB * 1024L * 1024L;
            if (graphSize > maxBytes)
            {
                _logger.LogWarning(
                    "Graph size {SizeMB:F1}MB exceeds limit {LimitMB}MB, compressing...",
                    graphSize / 1024.0 / 1024.0, manifest.MaxSizeLimitMB);
            }

            // 3. 压缩 (如果需要)
            var compressedPath = await CompressGraphAsync(graphDataPath, manifest.Compression, packageDir, ct).ConfigureAwait(false);

            // 4. 分片 (如果需要)
            var shardPaths = await ShardIfNeededAsync(compressedPath, manifest, packageDir, ct).ConfigureAwait(false);

            // 5. 生成清单和校验和
            var stats = graph.GetStats();
            var checksum = await ComputeChecksumAsync(compressedPath, ct).ConfigureAwait(false);
            
            manifest = manifest with
            {
                EntityCount = (int)stats["entity_count"],
                TripletCount = (int)stats["triplet_count"],
                RelationTypes = ((Dictionary<string, int>)stats["by_relation_type"]).Keys.ToList(),
                TotalSizeBytes = shardPaths.Sum(p => new FileInfo(p).Length),
                ChecksumSHA256 = checksum,
                UpdatedAt = DateTime.UtcNow
            };

            // 6. 写入清单文件
            var manifestPath = Path.Combine(packageDir, "manifest.json");
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions), ct).ConfigureAwait(false);

            // 7. 创建 .graphpackage 归档
            var packagePath = Path.Combine(_packagesDirectory, $"{manifest.GraphId}_{manifest.Version}.graphpackage");
            await CreatePackageArchiveAsync(packageDir, packagePath, ct).ConfigureAwait(false);

            // 8. 注册已安装的包
            var packageInfo = new GraphPackageInfo
            {
                GraphId = manifest.GraphId,
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
                _installedPackages[manifest.GraphId] = packageInfo;
            }

            _logger.LogInformation(
                "Graph packaged: id={Id} domain={Domain} version={Version} entities={Entities} triplets={Triplets} size={SizeKB:F1}KB",
                manifest.GraphId, manifest.Domain, manifest.Version,
                manifest.EntityCount, manifest.TripletCount, manifest.TotalSizeBytes / 1024.0);

            return packagePath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to package graph: {Id}", manifest.GraphId);
            throw;
        }
    }

    /// <summary>
    /// 解压并验证图谱包
    /// </summary>
    public async Task<GraphPackageInfo?> InstallPackageAsync(
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

            var manifest = JsonSerializer.Deserialize<GraphPackageManifest>(
                await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false), JsonOptions);
            if (manifest == null) return null;

            // 3. 验证校验和
            var graphPath = Path.Combine(extractDir, "graph_data.json");
            if (File.Exists(graphPath))
            {
                var checksum = await ComputeChecksumAsync(graphPath, ct).ConfigureAwait(false);
                if (checksum != manifest.ChecksumSHA256)
                {
                    _logger.LogWarning("Checksum mismatch for package: {Id}", manifest.GraphId);
                    return null;
                }
            }

            // 4. 解压缩 (如果需要)
            var decompressedPath = await DecompressIfNeededAsync(graphPath, manifest.Compression, extractDir, ct).ConfigureAwait(false);

            // 5. 合并分片 (如果需要)
            var finalGraphPath = await MergeShardsIfNeededAsync(extractDir, manifest, ct).ConfigureAwait(false);

            var packageInfo = new GraphPackageInfo
            {
                GraphId = manifest.GraphId,
                Domain = manifest.Domain,
                Version = manifest.Version,
                LocalPath = finalGraphPath,
                Manifest = manifest,
                IsLoaded = false,
                IsValid = true,
                DownloadedAt = DateTime.UtcNow,
                Source = "github"
            };

            lock (_lock)
            {
                _installedPackages[manifest.GraphId] = packageInfo;
            }

            _logger.LogInformation(
                "Graph package installed: id={Id} domain={Domain} version={Version}",
                manifest.GraphId, manifest.Domain, manifest.Version);

            return packageInfo;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to install graph package: {Path}", packagePath);
            return null;
        }
    }

    /// <summary>
    /// 从包加载知识图谱
    /// </summary>
    public async Task<KnowledgeGraph?> LoadGraphFromPackageAsync(
        string graphId,
        ILogger<KnowledgeGraph>? graphLogger = null,
        CancellationToken ct = default)
    {
        GraphPackageInfo? packageInfo;
        lock (_lock)
        {
            _installedPackages.TryGetValue(graphId, out packageInfo);
        }

        if (packageInfo == null)
        {
            _logger.LogWarning("Package not found: {Id}", graphId);
            return null;
        }

        try
        {
            var graph = new LTAI.Knowledge.Core.KnowledgeGraph(graphLogger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<KnowledgeGraph>.Instance);
            graph.LoadFromDisk(packageInfo.LocalPath);

            _logger.LogInformation(
                "Graph loaded from package: id={Id} domain={Domain}",
                graphId, packageInfo.Domain);

            return graph;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load graph from package: {Id}", graphId);
            return null;
        }
    }

    /// <summary>
    /// 获取已安装的包
    /// </summary>
    public GraphPackageInfo? GetPackage(string graphId)
    {
        lock (_lock)
        {
            return _installedPackages.GetValueOrDefault(graphId);
        }
    }

    /// <summary>
    /// 获取所有已安装的包
    /// </summary>
    public List<GraphPackageInfo> GetInstalledPackages()
    {
        lock (_lock)
        {
            return _installedPackages.Values.ToList();
        }
    }

    /// <summary>
    /// 卸载包
    /// </summary>
    public bool UninstallPackage(string graphId)
    {
        lock (_lock)
        {
            if (_installedPackages.Remove(graphId, out var package))
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
                    _logger.LogWarning(ex, "Failed to cleanup package files: {Id}", graphId);
                }

                _logger.LogInformation("Graph package uninstalled: {Id}", graphId);
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
                var manifest = JsonSerializer.Deserialize<GraphPackageManifest>(
                    File.ReadAllText(manifestFile), JsonOptions);
                if (manifest != null)
                {
                    _installedPackages[manifest.GraphId] = new GraphPackageInfo
                    {
                        GraphId = manifest.GraphId,
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

    private async Task<string> CompressGraphAsync(
        string graphPath,
        GraphCompression compression,
        string outputDir,
        CancellationToken ct)
    {
        if (compression == GraphCompression.None)
        {
            var destPath = Path.Combine(outputDir, "graph_data.json");
            File.Copy(graphPath, destPath, true);
            return destPath;
        }

        var compressedPath = compression switch
        {
            GraphCompression.Gzip => Path.Combine(outputDir, "graph_data.json.gz"),
            GraphCompression.Brotli => Path.Combine(outputDir, "graph_data.json.br"),
            _ => throw new ArgumentException("Unknown compression type")
        };

        await using var sourceStream = File.OpenRead(graphPath);
        await using var destStream = File.Create(compressedPath);

        if (compression == GraphCompression.Gzip)
        {
            await using var gzipStream = new GZipStream(destStream, CompressionLevel.Optimal);
            await sourceStream.CopyToAsync(gzipStream, ct).ConfigureAwait(false);
        }
        else if (compression == GraphCompression.Brotli)
        {
            await using var brotliStream = new BrotliStream(destStream, CompressionLevel.Optimal);
            await sourceStream.CopyToAsync(brotliStream, ct).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Graph compressed: {Original} -> {Compressed} ({Ratio:F1}%)",
            graphPath, compressedPath,
            new FileInfo(compressedPath).Length / (double)Math.Max(1, new FileInfo(graphPath).Length) * 100);

        return compressedPath;
    }

    private async Task<List<string>> ShardIfNeededAsync(
        string graphPath,
        GraphPackageManifest manifest,
        string outputDir,
        CancellationToken ct)
    {
        var shardSizeMB = 10;
        var fileSize = new FileInfo(graphPath).Length;
        var shardSizeBytes = shardSizeMB * 1024L * 1024L;

        if (fileSize <= shardSizeBytes)
        {
            return new List<string> { graphPath };
        }

        var shardPaths = new List<string>();
        var shardIndex = 0;

        await using var sourceStream = File.OpenRead(graphPath);
        var buffer = new byte[81920];

        while (sourceStream.Position < sourceStream.Length)
        {
            var shardPath = Path.Combine(outputDir, $"graph.shard.{shardIndex:D3}");
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
            "Graph sharded: {Count} shards of {SizeMB}MB each",
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
        string graphPath,
        GraphCompression compression,
        string outputDir,
        CancellationToken ct)
    {
        if (compression == GraphCompression.None || !File.Exists(graphPath))
        {
            return graphPath;
        }

        var decompressedPath = Path.Combine(outputDir, "graph_data_decompressed.json");

        if (compression == GraphCompression.Gzip)
        {
            await using var sourceStream = File.OpenRead(graphPath);
            await using var gzipStream = new GZipStream(sourceStream, CompressionMode.Decompress);
            await using var destStream = File.Create(decompressedPath);
            await gzipStream.CopyToAsync(destStream, ct).ConfigureAwait(false);
        }
        else if (compression == GraphCompression.Brotli)
        {
            await using var sourceStream = File.OpenRead(graphPath);
            await using var brotliStream = new BrotliStream(sourceStream, CompressionMode.Decompress);
            await using var destStream = File.Create(decompressedPath);
            await brotliStream.CopyToAsync(destStream, ct).ConfigureAwait(false);
        }
        else
        {
            return graphPath;
        }

        _logger.LogInformation("Graph decompressed: {Path}", decompressedPath);
        return decompressedPath;
    }

    private async Task<string> MergeShardsIfNeededAsync(
        string extractDir,
        GraphPackageManifest manifest,
        CancellationToken ct)
    {
        if (manifest.ShardCount <= 1)
        {
            var singlePath = Path.Combine(extractDir, "graph_data.json");
            if (File.Exists(singlePath)) return singlePath;
            return Path.Combine(extractDir, "graph_data_decompressed.json");
        }

        var mergedPath = Path.Combine(extractDir, "graph_data_merged.json");
        var shards = Directory.GetFiles(extractDir, "graph.shard.*").OrderBy(f => f).ToList();

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
