using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed record UserContext
{
    public string UserId { get; init; } = "default";
    public int TotalInteractions { get; init; }
    public DateTime FirstSeen { get; init; }
    public DateTime LastInteraction { get; init; }
    public Dictionary<string, int> DomainCounts { get; init; } = new();
    public Dictionary<string, float> DomainSuccessRates { get; init; } = new();
    public float AverageTrustScore { get; init; } = 0.5f;
    public List<string> RecentQueries { get; init; } = new();
    public string PreferredModel { get; init; } = "";
    public float ComplexityTolerance { get; init; } = 0.5f;
}

public sealed record UserContextUpdate
{
    public string Query { get; init; } = "";
    public string Response { get; init; } = "";
    public string Domain { get; init; } = "";
    public bool Success { get; init; }
    public float Reward { get; init; }
}

public sealed class UserContextTracker
{
    private readonly ConcurrentDictionary<string, UserContext> _contexts = new();
    private readonly ILogger<UserContextTracker> _logger;
    private readonly int _maxRecentQueries = 20;

    public UserContextTracker(ILogger<UserContextTracker>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<UserContextTracker>.Instance;
    }

    public UserContext GetOrCreateContext(string userId = "default")
    {
        return _contexts.GetOrAdd(userId, id => new UserContext
        {
            UserId = id,
            FirstSeen = DateTime.UtcNow,
            LastInteraction = DateTime.UtcNow
        });
    }

    public void UpdateContext(string userId, UserContextUpdate update)
    {
        var ctx = GetOrCreateContext(userId);

        var updatedDomains = new Dictionary<string, int>(ctx.DomainCounts);
        updatedDomains[update.Domain] = updatedDomains.GetValueOrDefault(update.Domain, 0) + 1;

        var updatedSuccessRates = new Dictionary<string, float>(ctx.DomainSuccessRates);
        var currentRate = updatedSuccessRates.GetValueOrDefault(update.Domain, 0.5f);
        var alpha = 0.1f;
        updatedSuccessRates[update.Domain] = currentRate * (1 - alpha) + (update.Success ? 1.0f : 0.0f) * alpha;

        var recentQueries = new List<string>(ctx.RecentQueries) { update.Query };
        if (recentQueries.Count > _maxRecentQueries)
            recentQueries = recentQueries.TakeLast(_maxRecentQueries).ToList();

        var trustDelta = update.Reward - 0.5f;
        var newTrust = Math.Clamp(ctx.AverageTrustScore + trustDelta * 0.05f, 0.0f, 1.0f);

        _contexts[userId] = ctx with
        {
            TotalInteractions = ctx.TotalInteractions + 1,
            LastInteraction = DateTime.UtcNow,
            DomainCounts = updatedDomains,
            DomainSuccessRates = updatedSuccessRates,
            AverageTrustScore = newTrust,
            RecentQueries = recentQueries,
            ComplexityTolerance = ComputeComplexityTolerance(updatedSuccessRates, ctx.TotalInteractions + 1)
        };
    }

    public float GetDomainConfidence(string userId, string domain)
    {
        var ctx = GetOrCreateContext(userId);
        var successRate = ctx.DomainSuccessRates.GetValueOrDefault(domain, 0.5f);
        var interactionCount = ctx.DomainCounts.GetValueOrDefault(domain, 0);
        var experienceBonus = Math.Min(interactionCount / 10.0f, 0.3f);

        return Math.Clamp(successRate * 0.7f + ctx.AverageTrustScore * 0.2f + experienceBonus, 0.0f, 1.0f);
    }

    public float GetOverallTrustScore(string userId)
    {
        var ctx = GetOrCreateContext(userId);
        return ctx.AverageTrustScore;
    }

    public string GetPreferredModel(string userId, string defaultModel)
    {
        var ctx = GetOrCreateContext(userId);
        return string.IsNullOrEmpty(ctx.PreferredModel) ? defaultModel : ctx.PreferredModel;
    }

    public Dictionary<string, object> GetUserStats(string userId)
    {
        var ctx = GetOrCreateContext(userId);
        return new Dictionary<string, object>
        {
            ["total_interactions"] = ctx.TotalInteractions,
            ["trust_score"] = ctx.AverageTrustScore,
            ["complexity_tolerance"] = ctx.ComplexityTolerance,
            ["domain_counts"] = ctx.DomainCounts,
            ["domain_success_rates"] = ctx.DomainSuccessRates,
            ["first_seen"] = ctx.FirstSeen,
            ["last_interaction"] = ctx.LastInteraction
        };
    }

    private static float ComputeComplexityTolerance(Dictionary<string, float> successRates, int totalInteractions)
    {
        if (successRates.Count == 0) return 0.5f;

        var avgSuccess = successRates.Values.Average();
        var experienceBonus = Math.Min(totalInteractions / 50.0, 0.3);

        return (float)Math.Clamp(avgSuccess * 0.7 + 0.2 + experienceBonus, 0.2, 0.9);
    }
}
