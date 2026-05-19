using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Cell.Lifecycle;

public sealed class Regen
{
    private static readonly Lazy<Regen> _instance = new(() =>
        new Regen(NullLoggerFactory.Instance.CreateLogger<Regen>()));

    public static Regen Instance => _instance.Value;

    private readonly ConcurrentDictionary<string, RegenReport> _reports = new();
    private readonly Random _rng = new();
    private readonly ILogger<Regen> _logger;

    public Regen() : this(NullLogger<Regen>.Instance) { }

    public Regen(ILogger<Regen> logger)
    {
        _logger = logger ?? NullLogger<Regen>.Instance;
    }

    public Task<RegenReport> HealAsync(string trigger, float damageScore)
    {
        var id = $"regen_{Guid.NewGuid().ToString("N")[..8]}";
        var healed = damageScore < 0.7f;
        var cellsReplaced = _rng.Next(1, 11);
        var recoveryTimeMs = (long)(damageScore * 1000 + _rng.Next(0, 100));

        var weightsCount = _rng.Next(1, 6);
        var newWeights = new Dictionary<string, float?>();
        for (int i = 0; i < weightsCount; i++)
        {
            var key = $"w_{_rng.Next(0, 100)}";
            newWeights[key] = (float)(_rng.NextDouble());
        }

        var report = new RegenReport
        {
            Id = id,
            Trigger = trigger,
            DamageScore = damageScore,
            Healed = healed,
            CellsReplaced = cellsReplaced,
            RecoveryTimeMs = recoveryTimeMs,
            NewWeights = newWeights
        };

        _reports[id] = report;

        _logger.LogInformation("Regen {ReportId}: trigger={Trigger}, damage={Damage}, healed={Healed}",
            id, trigger, damageScore, healed);

        return Task.FromResult(report);
    }

    public RegenReport? GetReport(string reportId)
    {
        return _reports.GetValueOrDefault(reportId);
    }

    public IReadOnlyList<RegenReport> GetHistory()
    {
        return _reports.Values.ToList();
    }

    public Dictionary<string, object> GetStats()
    {
        var values = _reports.Values.ToList();
        var count = values.Count;
        var healedCount = values.Count(r => r.Healed);

        return new Dictionary<string, object>
        {
            ["total"] = count,
            ["healed"] = healedCount,
            ["healed_rate"] = count > 0 ? (double)healedCount / count : 0.0
        };
    }
}
