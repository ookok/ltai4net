using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

// ==================== 领域苗圃配置 ====================

public record DomainDiscoveryConfig
{
    public int MinQueriesToDiscover { get; init; } = 10;  // 发现新领域所需的最小查询数
    public float SimilarityThreshold { get; init; } = 0.3f;  // 相似度阈值
    public TimeSpan DiscoveryInterval { get; init; } = TimeSpan.FromMinutes(30);
    public int MaxNurserySize { get; init; } = 1000;
}

public record NurseryEntry
{
    public string Query { get; init; } = "";
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public List<string> Keywords { get; init; } = new();
}

public record DiscoveredDomain
{
    public string Name { get; init; } = "";
    public List<string> SeedKeywords { get; init; } = new();
    public int SampleCount { get; init; }
}

// ==================== 领域发现服务 ====================

public sealed class DomainDiscoveryService : IDisposable
{
    private readonly DomainDiscoveryConfig _config;
    private readonly CellAIRegistry _cellRegistry;
    private readonly ILogger<DomainDiscoveryService> _logger;
    
    // 领域苗圃：存储未分类的查询
    private readonly ConcurrentQueue<NurseryEntry> _nursery = new();
    private readonly Timer? _discoveryTimer;
    private readonly object _lock = new();

    public DomainDiscoveryService(
        DomainDiscoveryConfig config,
        CellAIRegistry cellRegistry,
        ILogger<DomainDiscoveryService>? logger = null)
    {
        _config = config;
        _cellRegistry = cellRegistry;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DomainDiscoveryService>.Instance;

        _discoveryTimer = new Timer(
            RunDiscoveryCycle,
            null,
            _config.DiscoveryInterval,
            _config.DiscoveryInterval);

        _logger.LogInformation(
            "DomainDiscoveryService initialized: minQueries={Min} interval={Interval}",
            _config.MinQueriesToDiscover, _config.DiscoveryInterval);
    }

    /// <summary>
    /// 记录未分类查询到苗圃
    /// </summary>
    public void RecordUnclassified(string query, string detectedDomain = "general")
    {
        if (detectedDomain != "general") return;

        // 提取关键词
        var keywords = ExtractKeywords(query);
        if (keywords.Count == 0) return;

        var entry = new NurseryEntry
        {
            Query = query,
            Keywords = keywords
        };

        _nursery.Enqueue(entry);

        // 限制苗圃大小
        if (_nursery.Count > _config.MaxNurserySize)
        {
            _nursery.TryDequeue(out _);
        }
    }

    /// <summary>
    /// 获取苗圃状态
    /// </summary>
    public DomainNurseryStats GetNurseryStats()
    {
        return new DomainNurseryStats
        {
            TotalEntries = _nursery.Count,
            MaxSize = _config.MaxNurserySize
        };
    }

    // ==================== 内部方法 ====================

    private void RunDiscoveryCycle(object? state)
    {
        try
        {
            _logger.LogInformation("Starting domain discovery cycle...");

            var entries = _nursery.ToList();
            if (entries.Count < _config.MinQueriesToDiscover)
            {
                _logger.LogDebug("Insufficient nursery entries for discovery: {Count}", entries.Count);
                return;
            }

            // 1. 聚类分析：按关键词重叠度分组
            var clusters = ClusterEntries(entries);

            // 2. 检查是否有符合条件的簇
            foreach (var cluster in clusters)
            {
                if (cluster.Count >= _config.MinQueriesToDiscover)
                {
                    // 3. 发现新领域
                    var domainName = GenerateDomainName(cluster);
                    var seedKeywords = cluster.SelectMany(e => e.Keywords)
                        .GroupBy(k => k)
                        .OrderByDescending(g => g.Count())
                        .Take(5)
                        .Select(g => g.Key)
                        .ToList();

                    // 4. 注册新领域
                    RegisterNewDomain(domainName, seedKeywords);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Domain discovery cycle failed");
        }
    }

    private List<List<NurseryEntry>> ClusterEntries(List<NurseryEntry> entries)
    {
        var clusters = new List<List<NurseryEntry>>();
        var visited = new HashSet<int>();

        for (int i = 0; i < entries.Count; i++)
        {
            if (visited.Contains(i)) continue;

            var cluster = new List<NurseryEntry> { entries[i] };
            visited.Add(i);

            for (int j = i + 1; j < entries.Count; j++)
            {
                if (visited.Contains(j)) continue;

                var similarity = ComputeKeywordSimilarity(entries[i], entries[j]);
                if (similarity >= _config.SimilarityThreshold)
                {
                    cluster.Add(entries[j]);
                    visited.Add(j);
                }
            }

            clusters.Add(cluster);
        }

        return clusters.OrderByDescending(c => c.Count).ToList();
    }

    private static float ComputeKeywordSimilarity(NurseryEntry a, NurseryEntry b)
    {
        var setA = a.Keywords.ToHashSet();
        var setB = b.Keywords.ToHashSet();

        var intersection = setA.Intersect(setB).Count();
        var union = setA.Union(setB).Count();

        return union > 0 ? (float)intersection / union : 0f;
    }

    private static string GenerateDomainName(List<NurseryEntry> cluster)
    {
        // 使用最高频关键词作为领域名
        var topKeyword = cluster.SelectMany(e => e.Keywords)
            .GroupBy(k => k)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault()?.Key ?? "unknown";

        // 简单清洗：去除停用词，保持简短
        var cleanName = topKeyword.Replace(" ", "_").ToLowerInvariant();
        return $"auto_{cleanName}_{DateTime.UtcNow:MMdd}";
    }

    private void RegisterNewDomain(string domainName, List<string> seedKeywords)
    {
        _logger.LogInformation(
            "Discovered new domain: name={Name} keywords=[{Keywords}]",
            domainName, string.Join(", ", seedKeywords));

        // 注册到 CellAIRegistry (自动处理规则和种子引擎)
        _cellRegistry.RegisterDynamicDomain(domainName, seedKeywords.ToArray());

        _logger.LogInformation("New domain registered: {Domain}", domainName);
    }

    private static List<string> ExtractKeywords(string query)
    {
        // 简单关键词提取：分词 + 过滤停用词
        var stopWords = new HashSet<string> { "the", "is", "at", "which", "on", "a", "an", "and", "or", "but", "in", "with", "to", "for", "of", "it", "this", "that", "我", "是", "在", "有", "的", "了", "和", "或", "但" };
        
        return query.ToLowerInvariant()
            .Split(new[] { ' ', '\t', '\n', '\r', ',', '.', '!', '?', '，', '。', '！', '？' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2 && !stopWords.Contains(w))
            .Distinct()
            .Take(5)
            .ToList();
    }

    public void Dispose()
    {
        _discoveryTimer?.Dispose();
        _logger.LogInformation("DomainDiscoveryService disposed");
    }
}

public record DomainNurseryStats
{
    public int TotalEntries { get; init; }
    public int MaxSize { get; init; }
}
