using System.Collections.Concurrent;
using LTAI.Knowledge.Core;
using LTAI.Knowledge.Core.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

// ==================== 领域图谱配置 ====================

public record DomainGraphConfig
{
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(30);
    public int MaxLoadedGraphs { get; init; } = 10;
    public bool EnableLazyLoading { get; init; } = true;
    public bool EnableAutoUnload { get; init; } = true;
    public string GraphsDirectory { get; init; } = "";
}

public record DomainGraphInfo
{
    public string Domain { get; init; } = "";
    public bool IsLoaded { get; init; }
    public bool IsLoading { get; init; }
    public DateTime? LoadedAt { get; init; }
    public DateTime? LastUsedAt { get; init; }
    public int EntityCount { get; init; }
    public int TripletCount { get; init; }
    public long MemoryBytes { get; init; }
    public string? Version { get; init; }
    public string? Source { get; init; }  // "local", "github", "generated"
    public string? Error { get; init; }
}

// ==================== 领域图谱注册表 ====================

public sealed class DomainGraphRegistry : IDisposable
{
    private readonly DomainGraphConfig _config;
    private readonly ILogger<DomainGraphRegistry> _logger;
    private readonly ConcurrentDictionary<string, KnowledgeGraph> _loadedGraphs = new();
    private readonly ConcurrentDictionary<string, DomainGraphInfo> _graphRegistry = new();
    private readonly Timer? _idleCheckTimer;
    private readonly SemaphoreSlim _loadSemaphore;
    private readonly object _lock = new();

    public DomainGraphRegistry(
        DomainGraphConfig? config = null,
        ILogger<DomainGraphRegistry>? logger = null)
    {
        _config = config ?? new DomainGraphConfig
        {
            GraphsDirectory = Path.Combine(AppContext.BaseDirectory, "synaptic", "graphs")
        };
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DomainGraphRegistry>.Instance;
        _loadSemaphore = new SemaphoreSlim(3);  // 最多 3 个并发加载

        Directory.CreateDirectory(_config.GraphsDirectory);

        if (_config.EnableAutoUnload)
        {
            _idleCheckTimer = new Timer(
                CheckIdleGraphs,
                null,
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(5));
        }

        // 扫描已存在的图谱
        ScanExistingGraphs();

        _logger.LogInformation(
            "DomainGraphRegistry initialized: directory={Dir} maxGraphs={Max} idleTimeout={Timeout}",
            _config.GraphsDirectory, _config.MaxLoadedGraphs, _config.IdleTimeout);
    }

    /// <summary>
    /// 获取或创建领域图谱 (核心方法)
    /// </summary>
    public KnowledgeGraph GetOrCreateGraph(string domain)
    {
        // 检查是否已加载
        if (_loadedGraphs.TryGetValue(domain, out var loadedGraph))
        {
            UpdateLastUsed(domain);
            return loadedGraph;
        }

        // 检查是否需要懒加载
        if (_config.EnableLazyLoading)
        {
            var registryInfo = _graphRegistry.GetValueOrDefault(domain);
            if (registryInfo != null && registryInfo.Source != null)
            {
                _logger.LogDebug("Triggering lazy load for domain: {Domain}", domain);
                _ = Task.Run(async () => await LoadGraphAsync(domain));
            }
        }

        // 创建空图谱
        var graphPath = GetGraphPath(domain);
        var newGraph = new LTAI.Knowledge.Core.KnowledgeGraph(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<KnowledgeGraph>.Instance,
            dbPath: graphPath);

        _loadedGraphs[domain] = newGraph;
        _graphRegistry[domain] = new DomainGraphInfo
        {
            Domain = domain,
            IsLoaded = true,
            IsLoading = false,
            LoadedAt = DateTime.UtcNow,
            LastUsedAt = DateTime.UtcNow,
            EntityCount = 0,
            TripletCount = 0,
            MemoryBytes = 0,
            Source = "generated"
        };

        _logger.LogInformation("Created new empty graph for domain: {Domain}", domain);
        return newGraph;
    }

    /// <summary>
    /// 异步加载领域图谱
    /// </summary>
    public async Task<DomainGraphInfo?> LoadGraphAsync(string domain, CancellationToken ct = default)
    {
        await _loadSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 检查是否已加载
            if (_loadedGraphs.ContainsKey(domain))
            {
                return _graphRegistry.GetValueOrDefault(domain);
            }

            // 更新状态为加载中
            _graphRegistry[domain] = new DomainGraphInfo
            {
                Domain = domain,
                IsLoaded = false,
                IsLoading = true,
                LastUsedAt = DateTime.UtcNow
            };

            // 尝试从磁盘加载
            var graphPath = GetGraphPath(domain);
            var graph = new LTAI.Knowledge.Core.KnowledgeGraph(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<KnowledgeGraph>.Instance,
                dbPath: graphPath);

            if (File.Exists(graphPath))
            {
                graph.LoadFromDisk(graphPath);
                _logger.LogInformation("Loaded graph from disk: domain={Domain} path={Path}", domain, graphPath);
            }
            else
            {
                _logger.LogWarning("Graph file not found, creating empty: domain={Domain}", domain);
            }

            // 注册到已加载
            _loadedGraphs[domain] = graph;

            var stats = graph.GetStats();
            var info = new DomainGraphInfo
            {
                Domain = domain,
                IsLoaded = true,
                IsLoading = false,
                LoadedAt = DateTime.UtcNow,
                LastUsedAt = DateTime.UtcNow,
                EntityCount = (int)stats["entity_count"],
                TripletCount = (int)stats["triplet_count"],
                MemoryBytes = EstimateMemoryBytes(graph),
                Source = File.Exists(graphPath) ? "local" : "generated"
            };

            _graphRegistry[domain] = info;

            // 检查是否需要卸载其他图谱
            if (_loadedGraphs.Count > _config.MaxLoadedGraphs)
            {
                await UnloadLeastUsedGraphAsync(ct).ConfigureAwait(false);
            }

            return info;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load graph: domain={Domain}", domain);
            _graphRegistry[domain] = new DomainGraphInfo
            {
                Domain = domain,
                IsLoaded = false,
                IsLoading = false,
                Error = ex.Message
            };
            return null;
        }
        finally
        {
            _loadSemaphore.Release();
        }
    }

    /// <summary>
    /// 保存领域图谱到磁盘
    /// </summary>
    public async Task<bool> SaveGraphAsync(string domain, CancellationToken ct = default)
    {
        if (!_loadedGraphs.TryGetValue(domain, out var graph))
        {
            _logger.LogWarning("Cannot save graph: not loaded domain={Domain}", domain);
            return false;
        }

        try
        {
            var graphPath = GetGraphPath(domain);
            await graph.SaveToDiskAsync(graphPath).ConfigureAwait(false);

            _logger.LogInformation("Graph saved: domain={Domain} path={Path}", domain, graphPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save graph: domain={Domain}", domain);
            return false;
        }
    }

    /// <summary>
    /// 卸载领域图谱
    /// </summary>
    public async Task<bool> UnloadGraphAsync(string domain, CancellationToken ct = default)
    {
        // 先保存
        await SaveGraphAsync(domain, ct).ConfigureAwait(false);

        if (_loadedGraphs.TryRemove(domain, out var graph))
        {
            _graphRegistry[domain] = new DomainGraphInfo
            {
                Domain = domain,
                IsLoaded = false,
                IsLoading = false,
                LastUsedAt = DateTime.UtcNow
            };

            _logger.LogInformation("Graph unloaded: domain={Domain}", domain);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 跨领域查询
    /// </summary>
    public List<Triplet> QueryAcrossDomains(string[] domains, string query)
    {
        var allTriplets = new List<Triplet>();

        foreach (var domain in domains)
        {
            if (_loadedGraphs.TryGetValue(domain, out var graph))
            {
                var triplets = graph.GetTriplets();
                var filtered = triplets
                    .Where(t => t.Subject.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                               t.Object.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                allTriplets.AddRange(filtered);
                UpdateLastUsed(domain);
            }
        }

        return allTriplets.OrderByDescending(t => t.Confidence).ToList();
    }

    /// <summary>
    /// 获取所有领域图谱信息
    /// </summary>
    public List<DomainGraphInfo> GetAllGraphs()
    {
        return _graphRegistry.Values
            .OrderByDescending(g => g.LastUsedAt)
            .ToList();
    }

    /// <summary>
    /// 获取特定领域图谱信息
    /// </summary>
    public DomainGraphInfo? GetGraphInfo(string domain)
    {
        return _graphRegistry.GetValueOrDefault(domain);
    }

    /// <summary>
    /// 获取已加载的图谱
    /// </summary>
    public KnowledgeGraph? GetLoadedGraph(string domain)
    {
        return _loadedGraphs.GetValueOrDefault(domain);
    }

    /// <summary>
    /// 获取统计信息
    /// </summary>
    public DomainGraphRegistryStats GetStats()
    {
        var loadedCount = _graphRegistry.Values.Count(g => g.IsLoaded);
        var totalEntities = _graphRegistry.Values.Sum(g => g.EntityCount);
        var totalTriplets = _graphRegistry.Values.Sum(g => g.TripletCount);
        var totalMemory = _graphRegistry.Values.Sum(g => g.MemoryBytes);

        return new DomainGraphRegistryStats
        {
            TotalGraphs = _graphRegistry.Count,
            LoadedGraphs = loadedCount,
            TotalEntities = totalEntities,
            TotalTriplets = totalTriplets,
            TotalMemoryBytes = totalMemory,
            MaxLoadedGraphs = _config.MaxLoadedGraphs
        };
    }

    // ==================== 内部方法 ====================

    private string GetGraphPath(string domain)
    {
        var safeDomain = domain.Replace(Path.DirectorySeparatorChar, '_')
            .Replace(Path.AltDirectorySeparatorChar, '_');
        return Path.Combine(_config.GraphsDirectory, $"{safeDomain}_graph.db");
    }

    private void UpdateLastUsed(string domain)
    {
        if (_graphRegistry.TryGetValue(domain, out var info))
        {
            _graphRegistry[domain] = info with { LastUsedAt = DateTime.UtcNow };
        }
    }

    private void ScanExistingGraphs()
    {
        foreach (var graphFile in Directory.GetFiles(_config.GraphsDirectory, "*_graph.db"))
        {
            var domain = Path.GetFileNameWithoutExtension(graphFile)
                .Replace("_graph", "")
                .Replace("_", " ");

            _graphRegistry[domain] = new DomainGraphInfo
            {
                Domain = domain,
                IsLoaded = false,
                IsLoading = false,
                Source = "local"
            };
        }

        _logger.LogInformation("Scanned {Count} existing graphs", _graphRegistry.Count);
    }

    private async Task UnloadLeastUsedGraphAsync(CancellationToken ct)
    {
        var leastUsed = _graphRegistry.Values
            .Where(g => g.IsLoaded)
            .OrderBy(g => g.LastUsedAt)
            .FirstOrDefault();

        if (leastUsed != null)
        {
            await UnloadGraphAsync(leastUsed.Domain, ct).ConfigureAwait(false);
            _logger.LogInformation("Unloaded least-used graph: domain={Domain}", leastUsed.Domain);
        }
    }

    private void CheckIdleGraphs(object? state)
    {
        if (!_config.EnableAutoUnload) return;

        var now = DateTime.UtcNow;
        var idleGraphs = _graphRegistry.Values
            .Where(g => g.IsLoaded &&
                       g.LastUsedAt.HasValue &&
                       (now - g.LastUsedAt.Value) > _config.IdleTimeout)
            .Select(g => g.Domain)
            .ToList();

        foreach (var domain in idleGraphs)
        {
            _ = UnloadGraphAsync(domain);
        }
    }

    private static long EstimateMemoryBytes(KnowledgeGraph graph)
    {
        var stats = graph.GetStats();
        var entities = (int)stats["entity_count"];
        var triplets = (int)stats["triplet_count"];

        // 粗略估算：每个实体 ~100 字节，每个三元组 ~200 字节
        return (entities * 100) + (triplets * 200);
    }

    public void Dispose()
    {
        _idleCheckTimer?.Dispose();
        _loadSemaphore.Dispose();
        _logger.LogInformation("DomainGraphRegistry disposed");
    }
}

public record DomainGraphRegistryStats
{
    public int TotalGraphs { get; init; }
    public int LoadedGraphs { get; init; }
    public int TotalEntities { get; init; }
    public int TotalTriplets { get; init; }
    public long TotalMemoryBytes { get; init; }
    public int MaxLoadedGraphs { get; init; }
}
