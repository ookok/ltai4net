using System.Collections.Concurrent;
using System.Text.Json;
using LTAI.Agent.Pipeline.Steps;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Pipeline;

public sealed record StoredPlan(
    string Query,
    string Normalized,
    CompositionPlan Plan,
    int SuccessCount,
    int FailureCount,
    DateTime LastUsed);

public sealed class PlanLearningStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _storePath;
    private readonly ILogger<PlanLearningStore> _logger;
    private List<StoredPlan> _plans = [];
    private readonly object _lock = new();
    private bool _loaded;

    public PlanLearningStore(ILogger<PlanLearningStore>? logger = null)
    {
        _logger = logger ?? NullLogger<PlanLearningStore>.Instance;
        _storePath = Path.Combine(Directory.GetCurrentDirectory(), ".livingtree", "plans", "plans.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
    }

    public async Task<CompositionPlan?> FindSimilarAsync(string query, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);

        var norm = Normalize(query);
        var tokens = Tokenize(norm);

        lock (_lock)
        {
            StoredPlan? best = null;
            double bestScore = 0;

            foreach (var p in _plans)
            {
                if (p.FailureCount >= p.SuccessCount + 2) continue; // low-quality plan

                var pTokens = Tokenize(p.Normalized);
                var overlap = tokens.Count(t => pTokens.Contains(t));
                var score = (double)overlap / Math.Max(tokens.Count, pTokens.Count);

                if (score > bestScore)
                {
                    bestScore = score;
                    best = p;
                }
            }

            if (best != null && bestScore >= 0.5)
                return best.Plan;
        }

        return null;
    }

    public async Task StoreAsync(string query, CompositionPlan plan, bool success, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);

        var norm = Normalize(query);

        lock (_lock)
        {
            var existing = _plans.Find(p => p.Normalized == norm);
            if (existing != null)
            {
                _plans.Remove(existing);
                _plans.Add(existing with
                {
                    SuccessCount = existing.SuccessCount + (success ? 1 : 0),
                    FailureCount = existing.FailureCount + (success ? 0 : 1),
                    LastUsed = DateTime.UtcNow
                });
            }
            else
            {
                _plans.Add(new StoredPlan(
                    query, norm, plan,
                    success ? 1 : 0,
                    success ? 0 : 1,
                    DateTime.UtcNow));
            }

            // Keep max 100 plans, evict lowest quality
            if (_plans.Count > 100)
            {
                _plans = _plans
                    .OrderByDescending(p => p.SuccessCount - p.FailureCount)
                    .ThenByDescending(p => p.LastUsed)
                    .Take(100)
                    .ToList();
            }
        }

        await SaveAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<StoredPlan>> GetAllPlansAsync(CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        lock (_lock) return [.. _plans];
    }

    private async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_loaded) return;

        try
        {
            if (File.Exists(_storePath))
            {
                var json = await File.ReadAllTextAsync(_storePath, ct).ConfigureAwait(false);
                lock (_lock)
                    _plans = JsonSerializer.Deserialize<List<StoredPlan>>(json, JsonOpts) ?? [];
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PlanLearningStore: failed to load plans from {Path}", _storePath);
        }
        finally
        {
            _loaded = true;
        }
    }

    private async Task SaveAsync(CancellationToken ct = default)
    {
        try
        {
            string json;
            lock (_lock)
                json = JsonSerializer.Serialize(_plans, JsonOpts);

            await File.WriteAllTextAsync(_storePath, json, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PlanLearningStore: failed to save plans");
        }
    }

    private static string Normalize(string query) =>
        string.Join(" ", query.Trim().ToLowerInvariant()
            .Split([' ', '\t', '\n', '\r', ',', '.', '!', '?', '：', '，', '。', '！', '？'], StringSplitOptions.RemoveEmptyEntries));

    private static HashSet<string> Tokenize(string normalized) =>
        [.. normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
}
