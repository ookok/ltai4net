using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

// ============================================================================
// ASI-Evolve inspired: Cognition Seeder
// Injects human domain priors (papers, heuristics, known patterns) into
// existing memory stores BEFORE the AI starts exploring.
// ============================================================================

public sealed record CognitionItem
{
    public string Title { get; init; } = "";
    public string Content { get; init; } = "";
    public string Domain { get; init; } = "general";
    public List<string> Tags { get; init; } = new();
    public float Priority { get; init; } = 0.5f;
    public DateTime AddedAt { get; init; } = DateTime.UtcNow;
}

public sealed class CognitionSeeder
{
    private readonly SynapticMemory _synapticMemory;
    private readonly DualMemoryStore _dualMemory;
    private readonly WeightSubspaceAnalyzer? _subspaceAnalyzer;
    private readonly ILogger<CognitionSeeder> _logger;
    private readonly ConcurrentDictionary<string, CognitionItem> _items = new();
    private int _totalSeeded;

    public CognitionSeeder(
        SynapticMemory synapticMemory,
        DualMemoryStore dualMemory,
        WeightSubspaceAnalyzer? subspaceAnalyzer = null,
        ILogger<CognitionSeeder>? logger = null)
    {
        _synapticMemory = synapticMemory;
        _dualMemory = dualMemory;
        _subspaceAnalyzer = subspaceAnalyzer;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CognitionSeeder>.Instance;
    }

    // ========================================================================
    // 1. Seed domain knowledge into memory systems
    // ========================================================================

    public void Seed(IEnumerable<CognitionItem> items)
    {
        foreach (var item in items)
        {
            SeedItem(item);
        }

        _logger.LogInformation("CognitionSeeder: {Count} items seeded, total={Total}",
            items.Count(), _totalSeeded);
    }

    public void SeedItem(CognitionItem item)
    {
        _items[item.Title] = item;

        _synapticMemory.Store(new SynapticExperience
        {
            Query = $"[PRIOR] {item.Title}",
            Response = item.Content,
            Label = "prior_knowledge",
            Confidence = item.Priority,
            Reward = item.Priority,
            Metadata = $"cognition_seed,domain={item.Domain},tags={string.Join(",", item.Tags)}",
            Type = SynapseType.Teaching
        });

        _dualMemory.StoreEpisode(new RawEpisode
        {
            Query = $"[PRIOR] {item.Title}",
            FullTrajectory = item.Content,
            FinalAnswer = item.Content,
            Domain = item.Domain,
            WasSuccessful = true,
            Confidence = item.Priority,
            Reward = item.Priority,
            ImportanceScore = item.Priority * 10,
            Metadata = $"cognition_seed,tags={string.Join(",", item.Tags)}",
            IsImmutable = true
        });

        if (_subspaceAnalyzer != null)
        {
            var embedding = EncodePriorKnowledge(item.Content, 128);
            _subspaceAnalyzer.Analyze(new[] { embedding }, $"prior_{item.Domain}_{item.Title.GetHashCode():X}");
        }

        Interlocked.Increment(ref _totalSeeded);
    }

    // ========================================================================
    // 2. Load priors from a JSON file (e.g., domain_papers.json)
    // ========================================================================

    public void LoadFromFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            _logger.LogWarning("CognitionSeeder: file not found {Path}", filePath);
            return;
        }

        try
        {
            var json = File.ReadAllText(filePath);
            var items = JsonSerializer.Deserialize<List<CognitionItem>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (items != null && items.Count > 0)
                Seed(items);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CognitionSeeder: failed to load {Path}", filePath);
        }
    }

    // ========================================================================
    // 3. Create experiment-specific init_cognition (like ASI-Evolve)
    // ========================================================================

    public void SeedForExperiment(
        string experimentName,
        string domain,
        List<(string title, string content, float priority)> priors)
    {
        var items = priors.Select(p => new CognitionItem
        {
            Title = p.title,
            Content = p.content,
            Domain = domain,
            Priority = p.priority,
            Tags = new List<string> { experimentName, domain }
        });

        Seed(items);
    }

    // ========================================================================
    // 4. Cross-domain seed: transfer knowledge between domains
    // ========================================================================

    public void TransferKnowledge(string sourceDomain, string targetDomain, int topK = 5)
    {
        var sourceItems = _items.Values
            .Where(c => c.Domain == sourceDomain)
            .OrderByDescending(c => c.Priority)
            .Take(topK)
            .ToList();

        foreach (var item in sourceItems)
        {
            SeedItem(new CognitionItem
            {
                Title = $"[From {sourceDomain}] {item.Title}",
                Content = item.Content,
                Domain = targetDomain,
                Tags = item.Tags.Append($"transferred_from_{sourceDomain}").ToList(),
                Priority = item.Priority * 0.7f
            });
        }

        _logger.LogInformation("Transferred {Count} items from {Source} → {Target}",
            sourceItems.Count, sourceDomain, targetDomain);
    }

    // ========================================================================
    // 5. Stats
    // ========================================================================

    public Dictionary<string, object> GetStats() => new()
    {
        ["total_seeded"] = _totalSeeded,
        ["unique_items"] = _items.Count,
        ["domains"] = _items.Values.Select(c => c.Domain).Distinct().ToList()
    };

    public List<CognitionItem> GetItemsByDomain(string domain) =>
        _items.Values.Where(c => c.Domain == domain).ToList();

    private static float[] EncodePriorKnowledge(string content, int dim)
    {
        var encoded = new float[dim];
        for (int i = 0; i < Math.Min(content.Length, dim); i++)
            encoded[i] = ((float)(content[i] % 128) / 64f) - 1f;

        var norm = 0f;
        for (int i = 0; i < dim; i++) norm += encoded[i] * encoded[i];
        if (norm > 0)
        {
            norm = MathF.Sqrt(norm);
            for (int i = 0; i < dim; i++) encoded[i] /= norm;
        }

        return encoded;
    }
}
