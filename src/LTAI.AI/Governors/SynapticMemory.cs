using System.Collections.Concurrent;
using LiteDB;
using LTAI.Core.System;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public enum SynapseType
{
    Teaching,
    Feedback,
    Interaction,
    Correction
}

public record SynapticExperience
{
    [BsonId]
    public ObjectId Id { get; init; }
    public SynapseType Type { get; init; }
    public string Query { get; init; } = "";
    public string Response { get; init; } = "";
    public string Label { get; init; } = "";
    public float Confidence { get; init; }
    public float Reward { get; init; }
    public string Metadata { get; init; } = "";
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public bool UsedForTraining { get; set; }
}

public record TrainingSample
{
    public string Text { get; init; } = "";
    public string Label { get; init; } = "";
    public float Weight { get; init; } = 1.0f;
}

public sealed class SynapticMemory : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<SynapticExperience> _experiences;
    private readonly ILogger<SynapticMemory> _logger;
    private readonly object _lock = new();

    public SynapticMemory(string dbPath, ILogger<SynapticMemory>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SynapticMemory>.Instance;
        var connectionString = $"Filename={dbPath};Connection=Shared";
        _db = new LiteDatabase(connectionString);
        _experiences = _db.GetCollection<SynapticExperience>("experiences");
        _experiences.EnsureIndex(x => x.Type);
        _experiences.EnsureIndex(x => x.Label);
        _experiences.EnsureIndex(x => x.UsedForTraining);
        _experiences.EnsureIndex(x => x.CreatedAt);

        _logger.LogInformation("SynapticMemory initialized: {Path}", dbPath);
    }

    public void Store(SynapticExperience experience)
    {
        lock (_lock)
        {
            _experiences.Insert(experience);
        }
    }

    public void StoreBatch(IEnumerable<SynapticExperience> experiences)
    {
        lock (_lock)
        {
            _experiences.InsertBulk(experiences);
        }
    }

    public List<TrainingSample> GetTrainingSamples(string? label = null, int maxCount = 1000)
    {
        var experiences = string.IsNullOrEmpty(label)
            ? _experiences.Query()
                .Where(x => !x.UsedForTraining && x.Reward > 0.5f)
                .OrderByDescending(x => x.Reward)
                .Limit(maxCount)
                .ToList()
            : _experiences.Query()
                .Where(x => !x.UsedForTraining && x.Reward > 0.5f && x.Label == label)
                .OrderByDescending(x => x.Reward)
                .Limit(maxCount)
                .ToList();

        return experiences
            .Select(exp => new TrainingSample
            {
                Text = exp.Query,
                Label = exp.Label,
                Weight = exp.Reward
            })
            .ToList();
    }

    public List<SynapticExperience> GetExperiencesByType(SynapseType type, int limit = 100)
    {
        return _experiences.Query()
            .Where(x => x.Type == type)
            .OrderByDescending(x => x.CreatedAt)
            .Limit(limit)
            .ToList();
    }

    public List<SynapticExperience> GetRecentUntrained(int limit = 500)
    {
        return _experiences.Query()
            .Where(x => !x.UsedForTraining)
            .OrderByDescending(x => x.CreatedAt)
            .Limit(limit)
            .ToList();
    }

    public void MarkAsTrained(IEnumerable<ObjectId> ids)
    {
        foreach (var id in ids)
        {
            var exp = _experiences.FindById(id);
            if (exp != null)
            {
                exp.UsedForTraining = true;
                _experiences.Update(exp);
            }
        }
    }

    public void DeleteExperience(ObjectId id)
    {
        lock (_lock)
        {
            _experiences.Delete(id);
        }
    }

    public void DeleteExperiences(IEnumerable<ObjectId> ids)
    {
        lock (_lock)
        {
            foreach (var id in ids)
                _experiences.Delete(id);
        }
    }

    public int ExperienceCount => _experiences.Count();
    public int UntrainedCount => _experiences.Query().Where(x => !x.UsedForTraining).Count();
    public int PendingCount => 0;

    public List<TrainingSample> GetSamplesByDomain(string domain, int maxCount = 500)
    {
        var domainKeywords = GetDomainKeywords(domain);
        var experiences = _experiences.Query()
            .Where(x => !x.UsedForTraining && x.Reward > 0.3f)
            .OrderByDescending(x => x.Reward)
            .Limit(maxCount * 3)
            .ToList();

        var scored = experiences.Select(exp => new
        {
            Sample = new TrainingSample
            {
                Text = exp.Query,
                Label = exp.Label,
                Weight = exp.Reward
            },
            Score = ComputeDomainScore(exp.Query, domainKeywords)
        })
        .Where(x => x.Score > 0)
        .OrderByDescending(x => x.Score)
        .ThenByDescending(x => x.Sample.Weight)
        .Take(maxCount)
        .Select(x => x.Sample)
        .ToList();

        return scored;
    }

    private static float ComputeDomainScore(string query, string[] keywords)
    {
        if (keywords.Length == 0) return 0f;

        var lower = query.ToLowerInvariant();
        var matchCount = keywords.Count(kw => lower.Contains(kw));
        var keywordRatio = (float)matchCount / keywords.Length;

        var totalWords = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var wordDensity = totalWords > 0 ? (float)matchCount / totalWords : 0f;

        var exactMatchBonus = keywords.Any(kw => lower == kw) ? 0.3f : 0f;

        return keywordRatio * 0.5f + wordDensity * 0.3f + exactMatchBonus;
    }

    private static string[] GetDomainKeywords(string domain)
    {
        return DomainKeywords.GetKeywords(domain);
    }

    public void FlushPending()
    {
        // No-op: pending buffer removed, all experiences stored immediately
    }

    public void Dispose()
    {
        try
        {
            _db.Dispose();
            _logger.LogInformation("SynapticMemory disposed, database closed");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disposing SynapticMemory");
        }
    }
}
