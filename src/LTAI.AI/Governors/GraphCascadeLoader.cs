using System.Collections.Concurrent;
using LTAI.Knowledge.Core;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

// ==================== 图谱级联加载配置 ====================

public record GraphCascadeConfig
{
    public int MaxConcurrentDownloads { get; init; } = 2;
    public int MaxMemoryMB { get; init; } = 300;
    public bool EnableLazyLoading { get; init; } = true;
    public TimeSpan LazyLoadTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public bool EnableAutoUnload { get; init; } = true;
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(30);
    public int MaxLoadedGraphs { get; init; } = 10;
    public bool EnableDependencyPrefetch { get; init; } = true;
}

public enum GraphLoadState { NotLoaded, Loading, Loaded, Failed, Unloaded }

public record GraphLoadStatus
{
    public string GraphId { get; init; } = "";
    public string Domain { get; init; } = "";
    public GraphLoadState State { get; init; }
    public int CascadePriority { get; init; }
    public DateTime? LoadedAt { get; init; }
    public DateTime? LastUsedAt { get; init; }
    public long MemoryBytes { get; init; }
    public int EntityCount { get; init; }
    public int TripletCount { get; init; }
    public List<string> LoadedDependencies { get; init; } = new();
    public string? Error { get; init; }
}

// ==================== 图谱级联加载器 ====================

public sealed class GraphCascadeLoader : IDisposable
{
    private readonly GraphCascadeConfig _config;
    private readonly DomainGraphRegistry _graphRegistry;
    private readonly GitHubGraphRegistry _githubRegistry;
    private readonly GraphPackageManager _packageManager;
    private readonly ILogger<GraphCascadeLoader> _logger;
    
    private readonly ConcurrentDictionary<string, GraphLoadStatus> _loadStatus = new();
    private readonly SemaphoreSlim _downloadSemaphore;
    private readonly Timer? _idleCheckTimer;
    private readonly object _lock = new();
    
    private int _totalLoads;
    private int _totalUnloads;
    private long _currentMemoryBytes;

    public GraphCascadeLoader(
        GraphCascadeConfig config,
        DomainGraphRegistry graphRegistry,
        GitHubGraphRegistry githubRegistry,
        GraphPackageManager packageManager,
        ILogger<GraphCascadeLoader>? logger = null)
    {
        _config = config;
        _graphRegistry = graphRegistry;
        _githubRegistry = githubRegistry;
        _packageManager = packageManager;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<GraphCascadeLoader>.Instance;
        _downloadSemaphore = new SemaphoreSlim(_config.MaxConcurrentDownloads);

        if (_config.EnableAutoUnload)
        {
            _idleCheckTimer = new Timer(
                CheckIdleGraphs,
                null,
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(5));
        }

        _logger.LogInformation(
            "GraphCascadeLoader initialized: maxMemory={MemoryMB}MB maxGraphs={MaxGraphs} lazy={Lazy}",
            _config.MaxMemoryMB, _config.MaxLoadedGraphs, _config.EnableLazyLoading);
    }

    /// <summary>
    /// 级联加载图谱及其依赖
    /// </summary>
    public async Task<GraphLoadStatus?> LoadGraphCascadeAsync(
        string graphId,
        string domain,
        int priority = 0,
        CancellationToken ct = default)
    {
        // 检查是否已加载
        if (_loadStatus.TryGetValue(graphId, out var existing) && existing.State == GraphLoadState.Loaded)
        {
            _logger.LogDebug("Graph already loaded: {Id}", graphId);
            return existing;
        }

        // 检查内存限制
        if (!CanFitInMemory(graphId))
        {
            await UnloadLeastUsedGraphsAsync(ct).ConfigureAwait(false);
        }

        var status = new GraphLoadStatus
        {
            GraphId = graphId,
            Domain = domain,
            State = GraphLoadState.Loading,
            CascadePriority = priority
        };

        _loadStatus[graphId] = status;

        try
        {
            await _downloadSemaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // 1. 获取包信息
                var package = _packageManager.GetPackage(graphId);
                if (package == null)
                {
                    // 尝试从 GitHub 下载
                    _logger.LogInformation("Graph not found locally, downloading from GitHub: {Id}", graphId);
                    package = await _githubRegistry.DownloadGraphAsync(graphId, ct: ct).ConfigureAwait(false);
                    if (package == null)
                    {
                        status = status with { State = GraphLoadState.Failed, Error = "Package not found" };
                        _loadStatus[graphId] = status;
                        return status;
                    }
                }

                // 2. 加载依赖 (级联)
                var dependencies = package.Manifest.Dependencies;
                if (_config.EnableDependencyPrefetch && dependencies.Count > 0)
                {
                    await LoadDependenciesAsync(dependencies, ct).ConfigureAwait(false);
                }

                // 3. 加载图谱到 DomainGraphRegistry
                var graph = await _packageManager.LoadGraphFromPackageAsync(graphId, ct: ct).ConfigureAwait(false);
                if (graph == null)
                {
                    status = status with { State = GraphLoadState.Failed, Error = "Failed to load graph from package" };
                    _loadStatus[graphId] = status;
                    return status;
                }

                // 4. 注册到 DomainGraphRegistry
                _graphRegistry.GetOrCreateGraph(domain);  // 确保领域存在
                await _graphRegistry.SaveGraphAsync(domain, ct);  // 保存到磁盘

                var stats = graph.GetStats();
                status = status with
                {
                    State = GraphLoadState.Loaded,
                    LoadedAt = DateTime.UtcNow,
                    LastUsedAt = DateTime.UtcNow,
                    MemoryBytes = package.Manifest.TotalSizeBytes,
                    EntityCount = (int)stats["entity_count"],
                    TripletCount = (int)stats["triplet_count"],
                    LoadedDependencies = dependencies.Select(d => d.GraphId).ToList()
                };

                _loadStatus[graphId] = status;
                _totalLoads++;
                _currentMemoryBytes += package.Manifest.TotalSizeBytes;

                _logger.LogInformation(
                    "Graph cascade loaded: id={Id} domain={Domain} entities={Entities} triplets={Triplets} deps={Deps} memory={MemoryKB:F1}KB",
                    graphId, domain, status.EntityCount, status.TripletCount,
                    dependencies.Count, package.Manifest.TotalSizeBytes / 1024.0);

                return status;
            }
            finally
            {
                _downloadSemaphore.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load graph cascade: {Id}", graphId);
            status = status with { State = GraphLoadState.Failed, Error = ex.Message };
            _loadStatus[graphId] = status;
            return status;
        }
    }

    /// <summary>
    /// 懒加载触发器 (按需加载)
    /// </summary>
    public async Task<GraphLoadStatus?> TriggerLazyLoadAsync(
        string graphId,
        string domain,
        CancellationToken ct = default)
    {
        if (!_config.EnableLazyLoading)
        {
            return await LoadGraphCascadeAsync(graphId, domain, ct: ct).ConfigureAwait(false);
        }

        // 如果未加载，标记为待加载并返回占位符
        if (!_loadStatus.ContainsKey(graphId))
        {
            _logger.LogDebug("Lazy load triggered for graph: {Id}", graphId);
            
            _ = Task.Run(async () =>
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(_config.LazyLoadTimeout);
                await LoadGraphCascadeAsync(graphId, domain, ct: cts.Token).ConfigureAwait(false);
            }, ct);

            return new GraphLoadStatus
            {
                GraphId = graphId,
                Domain = domain,
                State = GraphLoadState.Loading,
                Error = "Loading in background..."
            };
        }

        return _loadStatus.GetValueOrDefault(graphId);
    }

    /// <summary>
    /// 卸载图谱
    /// </summary>
    public async Task<bool> UnloadGraphAsync(string graphId, CancellationToken ct = default)
    {
        if (!_loadStatus.TryGetValue(graphId, out var status) || status.State != GraphLoadState.Loaded)
        {
            return false;
        }

        try
        {
            // 从 DomainGraphRegistry 卸载
            await _graphRegistry.UnloadGraphAsync(status.Domain, ct).ConfigureAwait(false);

            status = status with
            {
                State = GraphLoadState.Unloaded,
                LoadedAt = null
            };

            _loadStatus[graphId] = status;
            _totalUnloads++;
            _currentMemoryBytes -= status.MemoryBytes;

            _logger.LogInformation("Graph unloaded: id={Id} memory={MemoryKB:F1}KB", graphId, status.MemoryBytes / 1024.0);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to unload graph: {Id}", graphId);
            return false;
        }
    }

    /// <summary>
    /// 获取所有加载状态
    /// </summary>
    public List<GraphLoadStatus> GetLoadStatuses()
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
    public GraphCascadeStats GetStats()
    {
        return new GraphCascadeStats
        {
            TotalLoads = _totalLoads,
            TotalUnloads = _totalUnloads,
            CurrentlyLoaded = _loadStatus.Count(s => s.Value.State == GraphLoadState.Loaded),
            CurrentMemoryBytes = _currentMemoryBytes,
            MaxMemoryBytes = _config.MaxMemoryMB * 1024L * 1024L,
            MaxLoadedGraphs = _config.MaxLoadedGraphs
        };
    }

    // ==================== 内部方法 ====================

    private async Task LoadDependenciesAsync(List<GraphDependency> dependencies, CancellationToken ct)
    {
        var sortedDeps = dependencies.OrderBy(d => d.LoadOrder).ToList();

        foreach (var dep in sortedDeps)
        {
            if (ct.IsCancellationRequested) break;

            // 检查是否已加载
            if (_loadStatus.TryGetValue(dep.GraphId, out var depStatus) &&
                depStatus.State == GraphLoadState.Loaded)
            {
                continue;
            }

            // 加载依赖
            await LoadGraphCascadeAsync(dep.GraphId, dep.Domain, dep.LoadOrder, ct).ConfigureAwait(false);
        }
    }

    private bool CanFitInMemory(string graphId)
    {
        var package = _packageManager.GetPackage(graphId);
        if (package == null) return true;  // 未知大小，允许尝试

        var projectedMemory = _currentMemoryBytes + package.Manifest.TotalSizeBytes;
        var maxBytes = _config.MaxMemoryMB * 1024L * 1024L;

        return projectedMemory <= maxBytes;
    }

    private async Task UnloadLeastUsedGraphsAsync(CancellationToken ct)
    {
        var loadedGraphs = _loadStatus.Values
            .Where(s => s.State == GraphLoadState.Loaded)
            .OrderBy(s => s.LastUsedAt)
            .ToList();

        var unloaded = 0;
        foreach (var graph in loadedGraphs)
        {
            if (_currentMemoryBytes <= _config.MaxMemoryMB * 1024L * 1024L * 0.8)
            {
                break;  // 内存使用降至 80% 以下
            }

            await UnloadGraphAsync(graph.GraphId, ct).ConfigureAwait(false);
            unloaded++;
        }

        if (unloaded > 0)
        {
            _logger.LogInformation("Unloaded {Count} least-used graphs to free memory", unloaded);
        }
    }

    private void CheckIdleGraphs(object? state)
    {
        if (!_config.EnableAutoUnload) return;

        var now = DateTime.UtcNow;
        var toUnload = _loadStatus.Values
            .Where(s => s.State == GraphLoadState.Loaded &&
                       s.LastUsedAt.HasValue &&
                       (now - s.LastUsedAt.Value) > _config.IdleTimeout)
            .Select(s => s.GraphId)
            .ToList();

        foreach (var graphId in toUnload)
        {
            _ = UnloadGraphAsync(graphId);
        }
    }

    public void Dispose()
    {
        _idleCheckTimer?.Dispose();
        _downloadSemaphore.Dispose();
        _logger.LogInformation("GraphCascadeLoader disposed");
    }
}

public record GraphCascadeStats
{
    public int TotalLoads { get; init; }
    public int TotalUnloads { get; init; }
    public int CurrentlyLoaded { get; init; }
    public long CurrentMemoryBytes { get; init; }
    public long MaxMemoryBytes { get; init; }
    public int MaxLoadedGraphs { get; init; }
}
