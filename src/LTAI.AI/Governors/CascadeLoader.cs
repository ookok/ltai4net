using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

// ==================== 级联加载配置 ====================

public record CascadeLoaderConfig
{
    public int MaxConcurrentDownloads { get; init; } = 3;
    public int MaxMemoryMB { get; init; } = 200;  // 最大内存占用
    public bool EnableLazyLoading { get; init; } = true;  // 启用懒加载
    public TimeSpan LazyLoadTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public bool EnableAutoUnload { get; init; } = true;  // 启用自动卸载
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(30);
    public int MaxCachedCells { get; init; } = 20;  // 最大缓存细胞数
    public bool EnableDependencyPrefetch { get; init; } = true;  // 启用依赖预取
}

public enum CellLoadState { NotLoaded, Loading, Loaded, Failed, Unloaded }

public record CellLoadStatus
{
    public string CellId { get; init; } = "";
    public string Domain { get; init; } = "";
    public CellLoadState State { get; init; }
    public int CascadePriority { get; init; }
    public DateTime? LoadedAt { get; init; }
    public DateTime? LastUsedAt { get; init; }
    public long MemoryBytes { get; init; }
    public List<string> LoadedDependencies { get; init; } = new();
    public string? Error { get; init; }
}

// ==================== 级联加载器 ====================

public sealed class CascadeLoader : IDisposable
{
    private readonly CascadeLoaderConfig _config;
    private readonly CellAIRegistry _cellRegistry;
    private readonly GitHubCellRegistry _githubRegistry;
    private readonly CellPackageManager _packageManager;
    private readonly ILogger<CascadeLoader> _logger;
    
    private readonly ConcurrentDictionary<string, CellLoadStatus> _loadStatus = new();
    private readonly SemaphoreSlim _downloadSemaphore;
    private readonly Timer? _idleCheckTimer;
    private readonly object _lock = new();
    
    private int _totalLoads;
    private int _totalUnloads;
    private long _currentMemoryBytes;

    public CascadeLoader(
        CascadeLoaderConfig config,
        CellAIRegistry cellRegistry,
        GitHubCellRegistry githubRegistry,
        CellPackageManager packageManager,
        ILogger<CascadeLoader>? logger = null)
    {
        _config = config;
        _cellRegistry = cellRegistry;
        _githubRegistry = githubRegistry;
        _packageManager = packageManager;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CascadeLoader>.Instance;
        _downloadSemaphore = new SemaphoreSlim(_config.MaxConcurrentDownloads);

        if (_config.EnableAutoUnload)
        {
            _idleCheckTimer = new Timer(
                CheckIdleCells,
                null,
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(5));
        }

        _logger.LogInformation(
            "CascadeLoader initialized: maxMemory={MemoryMB}MB maxCells={MaxCells} lazy={Lazy}",
            _config.MaxMemoryMB, _config.MaxCachedCells, _config.EnableLazyLoading);
    }

    /// <summary>
    /// 级联加载细胞及其依赖
    /// </summary>
    public async Task<CellLoadStatus?> LoadCellCascadeAsync(
        string cellId,
        string domain,
        int priority = 0,
        CancellationToken ct = default)
    {
        // 检查是否已加载
        if (_loadStatus.TryGetValue(cellId, out var existing) && existing.State == CellLoadState.Loaded)
        {
            _logger.LogDebug("Cell already loaded: {Id}", cellId);
            return existing;
        }

        // 检查内存限制
        if (!CanFitInMemory(cellId))
        {
            await UnloadLeastUsedCellsAsync(ct).ConfigureAwait(false);
        }

        var status = new CellLoadStatus
        {
            CellId = cellId,
            Domain = domain,
            State = CellLoadState.Loading,
            CascadePriority = priority
        };

        _loadStatus[cellId] = status;

        try
        {
            // 1. 获取包信息
            var package = _packageManager.GetPackage(cellId);
            if (package == null)
            {
                // 尝试从 GitHub 下载
                _logger.LogInformation("Cell not found locally, downloading from GitHub: {Id}", cellId);
                package = await _githubRegistry.DownloadCellAsync(cellId, ct: ct).ConfigureAwait(false);
                if (package == null)
                {
                    status = status with { State = CellLoadState.Failed, Error = "Package not found" };
                    _loadStatus[cellId] = status;
                    return status;
                }
            }

            // 2. 加载依赖 (级联)
            var dependencies = package.Manifest.Dependencies;
            if (_config.EnableDependencyPrefetch && dependencies.Count > 0)
            {
                await LoadDependenciesAsync(dependencies, ct).ConfigureAwait(false);
            }

            // 3. 加载细胞模型
            await LoadCellModelAsync(package, ct).ConfigureAwait(false);

            status = status with
            {
                State = CellLoadState.Loaded,
                LoadedAt = DateTime.UtcNow,
                LastUsedAt = DateTime.UtcNow,
                MemoryBytes = package.Manifest.TotalSizeBytes,
                LoadedDependencies = dependencies.Select(d => d.CellId).ToList()
            };

            _loadStatus[cellId] = status;
            _totalLoads++;
            _currentMemoryBytes += package.Manifest.TotalSizeBytes;

            _logger.LogInformation(
                "Cell cascade loaded: id={Id} domain={Domain} deps={Deps} memory={MemoryKB:F1}KB",
                cellId, domain, dependencies.Count, package.Manifest.TotalSizeBytes / 1024.0);

            return status;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load cell cascade: {Id}", cellId);
            status = status with { State = CellLoadState.Failed, Error = ex.Message };
            _loadStatus[cellId] = status;
            return status;
        }
    }

    /// <summary>
    /// 懒加载触发器 (按需加载)
    /// </summary>
    public async Task<CellLoadStatus?> TriggerLazyLoadAsync(
        string cellId,
        string domain,
        CancellationToken ct = default)
    {
        if (!_config.EnableLazyLoading)
        {
            return await LoadCellCascadeAsync(cellId, domain, ct: ct).ConfigureAwait(false);
        }

        // 如果未加载，标记为待加载并返回占位符
        if (!_loadStatus.ContainsKey(cellId))
        {
            _logger.LogDebug("Lazy load triggered for cell: {Id}", cellId);
            
            _ = Task.Run(async () =>
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(_config.LazyLoadTimeout);
                await LoadCellCascadeAsync(cellId, domain, ct: cts.Token).ConfigureAwait(false);
            }, ct);

            return new CellLoadStatus
            {
                CellId = cellId,
                Domain = domain,
                State = CellLoadState.Loading,
                Error = "Loading in background..."
            };
        }

        return _loadStatus.GetValueOrDefault(cellId);
    }

    /// <summary>
    /// 卸载细胞
    /// </summary>
    public async Task<bool> UnloadCellAsync(string cellId, CancellationToken ct = default)
    {
        if (!_loadStatus.TryGetValue(cellId, out var status) || status.State != CellLoadState.Loaded)
        {
            return false;
        }

        try
        {
            // 从 CellAIRegistry 卸载
            await _cellRegistry.UnloadIdleCellsAsync(ct).ConfigureAwait(false);

            status = status with
            {
                State = CellLoadState.Unloaded,
                LoadedAt = null
            };

            _loadStatus[cellId] = status;
            _totalUnloads++;
            _currentMemoryBytes -= status.MemoryBytes;

            _logger.LogInformation("Cell unloaded: id={Id} memory={MemoryKB:F1}KB", cellId, status.MemoryBytes / 1024.0);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to unload cell: {Id}", cellId);
            return false;
        }
    }

    /// <summary>
    /// 获取所有加载状态
    /// </summary>
    public List<CellLoadStatus> GetLoadStatuses()
    {
        return _loadStatus.Values
            .OrderBy(s => s.CascadePriority)
            .ThenByDescending(s => s.LoadedAt)
            .ToList();
    }

    /// <summary>
    /// 获取当前内存使用
    /// </summary>
    public long GetCurrentMemoryBytes()
    {
        return _currentMemoryBytes;
    }

    /// <summary>
    /// 获取加载统计
    /// </summary>
    public CascadeStats GetStats()
    {
        return new CascadeStats
        {
            TotalLoads = _totalLoads,
            TotalUnloads = _totalUnloads,
            CurrentlyLoaded = _loadStatus.Count(s => s.Value.State == CellLoadState.Loaded),
            CurrentMemoryBytes = _currentMemoryBytes,
            MaxMemoryBytes = _config.MaxMemoryMB * 1024L * 1024L,
            MaxCachedCells = _config.MaxCachedCells
        };
    }

    // ==================== 内部方法 ====================

    private async Task LoadDependenciesAsync(List<CellDependency> dependencies, CancellationToken ct)
    {
        var sortedDeps = dependencies.OrderBy(d => d.LoadOrder).ToList();

        foreach (var dep in sortedDeps)
        {
            if (ct.IsCancellationRequested) break;

            // 检查是否已加载
            if (_loadStatus.TryGetValue(dep.CellId, out var depStatus) &&
                depStatus.State == CellLoadState.Loaded)
            {
                continue;
            }

            // 加载依赖
            await LoadCellCascadeAsync(dep.CellId, dep.Domain, dep.LoadOrder, ct).ConfigureAwait(false);
        }
    }

    private async Task LoadCellModelAsync(CellPackageInfo package, CancellationToken ct)
    {
        // 根据包格式加载到 CellAIRegistry
        var config = new OnnxModelConfig
        {
            Domain = package.Manifest.Domain,
            ModelPath = package.LocalPath,
            Labels = package.Manifest.Labels,
            MaxSequenceLength = package.Manifest.MaxSequenceLength,
            MinConfidence = 0.5f,
            IsQuantized = package.Manifest.IsQuantized,
            SizeBytes = package.Manifest.ModelSizeBytes,
            Source = "github",
            Description = package.Manifest.Description
        };

        await _cellRegistry.InitializePretrainedModelsAsync(
            new Dictionary<string, OnnxModelConfig> { [package.Manifest.Domain] = config },
            autoDownload: false,
            ct: ct).ConfigureAwait(false);
    }

    private bool CanFitInMemory(string cellId)
    {
        var package = _packageManager.GetPackage(cellId);
        if (package == null) return true;  // 未知大小，允许尝试

        var projectedMemory = _currentMemoryBytes + package.Manifest.TotalSizeBytes;
        var maxBytes = _config.MaxMemoryMB * 1024L * 1024L;

        return projectedMemory <= maxBytes;
    }

    private async Task UnloadLeastUsedCellsAsync(CancellationToken ct)
    {
        var loadedCells = _loadStatus.Values
            .Where(s => s.State == CellLoadState.Loaded)
            .OrderBy(s => s.LastUsedAt)
            .ToList();

        var unloaded = 0;
        foreach (var cell in loadedCells)
        {
            if (_currentMemoryBytes <= _config.MaxMemoryMB * 1024L * 1024L * 0.8)
            {
                break;  // 内存使用降至 80% 以下
            }

            await UnloadCellAsync(cell.CellId, ct).ConfigureAwait(false);
            unloaded++;
        }

        if (unloaded > 0)
        {
            _logger.LogInformation("Unloaded {Count} least-used cells to free memory", unloaded);
        }
    }

    private void CheckIdleCells(object? state)
    {
        if (!_config.EnableAutoUnload) return;

        var now = DateTime.UtcNow;
        var toUnload = _loadStatus.Values
            .Where(s => s.State == CellLoadState.Loaded &&
                       s.LastUsedAt.HasValue &&
                       (now - s.LastUsedAt.Value) > _config.IdleTimeout)
            .Select(s => s.CellId)
            .ToList();

        foreach (var cellId in toUnload)
        {
            _ = UnloadCellAsync(cellId);
        }
    }

    public void Dispose()
    {
        _idleCheckTimer?.Dispose();
        _downloadSemaphore.Dispose();
        _logger.LogInformation("CascadeLoader disposed");
    }
}

public record CascadeStats
{
    public int TotalLoads { get; init; }
    public int TotalUnloads { get; init; }
    public int CurrentlyLoaded { get; init; }
    public long CurrentMemoryBytes { get; init; }
    public long MaxMemoryBytes { get; init; }
    public int MaxCachedCells { get; init; }
}
