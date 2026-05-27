using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.Governors;

public enum BootstrapPhase
{
    Teaching,
    Shadowing,
    Autonomous
}

public sealed record BootstrapStats
{
    public BootstrapPhase Phase { get; init; }
    public int TotalQueries { get; init; }
    public int L2Queries { get; init; }
    public int Agreements { get; init; }
    public int Disagreements { get; init; }
    public double CurrentAccuracy { get; init; }
    public double CuriosityBudget { get; init; }
    public DateTime PhaseStartedAt { get; init; }
}

public sealed class BootstrapTeacher
{
    private readonly ParetoRouter _router;
    private readonly ILogger<BootstrapTeacher> _logger;
    private readonly ConcurrentQueue<(float[] Emb, string Route, float Q, float S, float C)> _knowledgeBase = new();
    private BootstrapPhase _phase = BootstrapPhase.Teaching;
    private int _totalQueries;
    private int _l2Queries;
    private int _agreements;
    private int _disagreements;
    private double _curiosityBudget = 100.0;
    private DateTime _phaseStartedAt = DateTime.UtcNow;
    private readonly object _lock = new();

    private int _teachingStalemateCount;
    private int _shadowStalemateCount;
    private readonly double _originalTeachingAccuracy;
    private readonly double _originalShadowingAccuracy;
    private readonly string _thresholdsFile;

    public int StalemateThreshold { get; set; } = 5;
    public double StalemateRelaxStep { get; set; } = 0.02;
    public double MaxRelaxation { get; set; } = 0.10;

    public int TeachingQuota { get; internal set; } = 2000;
    public double TeachingAccuracyThreshold { get; internal set; } = 0.85;
    public int ShadowingExtraQueries { get; internal set; } = 1000;
    public double ShadowingAccuracyThreshold { get; internal set; } = 0.95;
    public double ShadowRate { get; internal set; } = 0.10;
    public double AutonomousSpotCheckRate { get; internal set; } = 0.02;

    public BootstrapTeacher(
        ParetoRouter router,
        string? thresholdsDir = null,
        ILogger<BootstrapTeacher>? logger = null)
    {
        _router = router;
        _logger = logger ?? NullLogger<BootstrapTeacher>.Instance;
        var dir = thresholdsDir ?? Path.Combine(AppContext.BaseDirectory, "rules");
        _thresholdsFile = Path.Combine(dir, "bootstrap_thresholds.json");
        _originalTeachingAccuracy = TeachingAccuracyThreshold;
        _originalShadowingAccuracy = ShadowingAccuracyThreshold;
        TryLoadThresholds();
    }

    public BootstrapPhase Phase
    {
        get { lock (_lock) return _phase; }
    }

    public BootstrapStats GetStats()
    {
        lock (_lock)
        {
            return new BootstrapStats
            {
                Phase = _phase,
                TotalQueries = _totalQueries,
                L2Queries = _l2Queries,
                Agreements = _agreements,
                Disagreements = _disagreements,
                CurrentAccuracy = _l2Queries > 0 ? (double)_agreements / _l2Queries : 0,
                CuriosityBudget = _curiosityBudget,
                PhaseStartedAt = _phaseStartedAt
            };
        }
    }

    public Task<bool> ShouldUseL2Async(float[] embedding, CancellationToken ct = default)
    {
        BootstrapPhase currentPhase;
        int total;
        double curiosity;
        string l0Route;
        float l0Confidence;

        lock (_lock)
        {
            currentPhase = _phase;
            total = _totalQueries;
            curiosity = _curiosityBudget;
        }

        var decision = _router.Decide(embedding);
        l0Route = decision.Route;
        l0Confidence = decision.Confidence;

        var result = currentPhase switch
        {
            BootstrapPhase.Teaching => true,

            BootstrapPhase.Shadowing =>
                l0Confidence < 0.7f ||
                Random.Shared.NextDouble() < ShadowRate ||
                (curiosity > 10 && Random.Shared.NextDouble() < 0.05),

            BootstrapPhase.Autonomous =>
                l0Confidence < 0.5f ||
                Random.Shared.NextDouble() < AutonomousSpotCheckRate,

            _ => true
        };

        return Task.FromResult(result);
    }

    public Task RecordL2DecisionAsync(
        float[] embedding,
        string route,
        float quality,
        float speed,
        float cost,
        CancellationToken ct = default)
    {
        var l0Decision = _router.Decide(embedding);
        var agreed = string.Equals(l0Decision.Route, route, StringComparison.OrdinalIgnoreCase);

        lock (_lock)
        {
            _totalQueries++;
            _l2Queries++;

            if (agreed)
                _agreements++;
            else
                _disagreements++;

            _curiosityBudget = Math.Max(10, _curiosityBudget - 0.05);
        }

        var projected = _router.ProjectEmbedding(embedding);
        var pointId = Guid.NewGuid().ToString("N")[..8];
        _router.AddFrontierPoint(new ParetoPoint
        {
            Id = pointId,
            Label = route,
            Quality = quality,
            Speed = speed,
            Cost = cost,
            Embedding = projected
        });

        if (!agreed)
            _router.RecordFeedback(pointId, quality, speed, cost);

        _knowledgeBase.Enqueue((embedding, route, quality, speed, cost));
        while (_knowledgeBase.Count > 5000)
            _knowledgeBase.TryDequeue(out _);

        AdvancePhaseIfReadyAsync(ct);

        return Task.CompletedTask;
    }

    public Task RecordL0DecisionAsync(float[] embedding, string route, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _totalQueries++;
        }

        var decision = _router.Decide(embedding);
        if (!string.Equals(decision.Route, route, StringComparison.OrdinalIgnoreCase))
        {
            var point = decision.NearestPoint;
            if (point != null)
            {
                _router.RecordFeedback(point.Id,
                    point.Quality * 0.95f,
                    point.Speed * 0.95f,
                    point.Cost * 1.05f);
            }
        }

        return Task.CompletedTask;
    }

    public Task<BootstrapPhase> AdvancePhaseIfReadyAsync(CancellationToken ct = default)
    {
        BootstrapPhase currentPhase;
        int total;
        double accuracy;

        lock (_lock)
        {
            currentPhase = _phase;
            total = _totalQueries;
            accuracy = _l2Queries > 0 ? (double)_agreements / _l2Queries : 0;
        }

        switch (currentPhase)
        {
            case BootstrapPhase.Teaching:
                if (total >= TeachingQuota && accuracy >= TeachingAccuracyThreshold)
                {
                    AdvancePhase(BootstrapPhase.Shadowing, total, accuracy, TeachingAccuracyThreshold);
                    _teachingStalemateCount = 0;
                    _ = PersistThresholdsAsync(ct);
                }
                else if (total >= TeachingQuota &&
                         accuracy >= TeachingAccuracyThreshold - MaxRelaxation)
                {
                    _teachingStalemateCount++;
                    if (_teachingStalemateCount >= StalemateThreshold)
                    {
                        var oldThreshold = TeachingAccuracyThreshold;
                        TeachingAccuracyThreshold = Math.Max(
                            TeachingAccuracyThreshold - StalemateRelaxStep,
                            _originalTeachingAccuracy - MaxRelaxation);
                        _logger.LogWarning(
                            "Bootstrap: Teaching stalemate — relaxing accuracy threshold from {Old:F3} to {New:F3} (accuracy={Acc:F3})",
                            oldThreshold, TeachingAccuracyThreshold, accuracy);
                        _ = PersistThresholdsAsync(ct);

                        if (accuracy >= TeachingAccuracyThreshold)
                        {
                            AdvancePhase(BootstrapPhase.Shadowing, total, accuracy, TeachingAccuracyThreshold);
                        }
                        _teachingStalemateCount = 0;
                    }
                }
                else
                {
                    _teachingStalemateCount = 0;
                }
                break;

            case BootstrapPhase.Shadowing:
                if (total >= TeachingQuota + ShadowingExtraQueries && accuracy >= ShadowingAccuracyThreshold)
                {
                    AdvancePhase(BootstrapPhase.Autonomous, total, accuracy, ShadowingAccuracyThreshold);
                    _shadowStalemateCount = 0;
                    _ = PersistThresholdsAsync(ct);
                }
                else if (total >= TeachingQuota + ShadowingExtraQueries &&
                         accuracy >= ShadowingAccuracyThreshold - MaxRelaxation)
                {
                    _shadowStalemateCount++;
                    if (_shadowStalemateCount >= StalemateThreshold)
                    {
                        var oldThreshold = ShadowingAccuracyThreshold;
                        ShadowingAccuracyThreshold = Math.Max(
                            ShadowingAccuracyThreshold - StalemateRelaxStep,
                            _originalShadowingAccuracy - MaxRelaxation);
                        _logger.LogWarning(
                            "Bootstrap: Shadowing stalemate — relaxing accuracy threshold from {Old:F3} to {New:F3} (accuracy={Acc:F3})",
                            oldThreshold, ShadowingAccuracyThreshold, accuracy);
                        _ = PersistThresholdsAsync(ct);

                        if (accuracy >= ShadowingAccuracyThreshold)
                        {
                            AdvancePhase(BootstrapPhase.Autonomous, total, accuracy, ShadowingAccuracyThreshold);
                        }
                        _shadowStalemateCount = 0;
                    }
                }
                else
                {
                    _shadowStalemateCount = 0;
                }
                break;

            case BootstrapPhase.Autonomous:
                if (accuracy < TeachingAccuracyThreshold && total > TeachingQuota + ShadowingExtraQueries + 500)
                {
                    lock (_lock)
                    {
                        _phase = BootstrapPhase.Shadowing;
                        _phaseStartedAt = DateTime.UtcNow;
                        _curiosityBudget = 100.0;
                    }
                    _logger.LogWarning(
                        "Bootstrap: Autonomous ↓ Shadowing (accuracy regressed to {Acc:F3})",
                        accuracy);
                }
                break;
        }

        lock (_lock) { currentPhase = _phase; }
        return Task.FromResult(currentPhase);
    }

    private void AdvancePhase(BootstrapPhase nextPhase, int total, double accuracy, double threshold)
    {
        var phaseName = nextPhase.ToString();
        lock (_lock)
        {
            _phase = nextPhase;
            _phaseStartedAt = DateTime.UtcNow;
            _curiosityBudget = 100.0;
        }
        _logger.LogInformation(
            "Bootstrap: {Current} → {Next} (queries={Total}, accuracy={Acc:F3}, threshold={Threshold})",
            _phase == BootstrapPhase.Shadowing ? "Teaching" : "Shadowing", phaseName,
            total, accuracy, threshold);
        Publish(new CoordinationEvent
        {
            Type = CoordinationEventType.BootstrapPhaseAdvanced,
            Source = "BootstrapTeacher",
            Payload = phaseName
        });
    }

    private void Publish(CoordinationEvent evt)
    {
        try
        {
            CoordinationPublisher?.Invoke(evt);
        }
        catch
        {
        }
    }

    public Action<CoordinationEvent>? CoordinationPublisher { get; set; }

    public Task ForceAdvancePhaseAsync(BootstrapPhase targetPhase, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _phase = targetPhase;
            _phaseStartedAt = DateTime.UtcNow;
            _curiosityBudget = 100.0;
        }

        _logger.LogInformation("Bootstrap: FORCE phase -> {Phase}", targetPhase);
        return Task.CompletedTask;
    }

    public Task FeedCuriosityBudgetAsync(double amount, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _curiosityBudget = Math.Min(200.0, _curiosityBudget + amount);
        }
        _logger.LogDebug("Curiosity budget: {Budget:F1}", _curiosityBudget);
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            _phase = BootstrapPhase.Teaching;
            _totalQueries = 0;
            _l2Queries = 0;
            _agreements = 0;
            _disagreements = 0;
            _curiosityBudget = 100.0;
            _phaseStartedAt = DateTime.UtcNow;
        }

        while (_knowledgeBase.TryDequeue(out _)) { }
        _logger.LogInformation("Bootstrap: reset to Teaching phase");
        return Task.CompletedTask;
    }

    private void TryLoadThresholds()
    {
        try
        {
            if (!File.Exists(_thresholdsFile)) return;

            var json = File.ReadAllText(_thresholdsFile);
            var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (data == null) return;

            if (data.TryGetValue("TeachingAccuracyThreshold", out var tat) && tat.TryGetDouble(out var tatVal))
                TeachingAccuracyThreshold = tatVal;
            if (data.TryGetValue("ShadowingAccuracyThreshold", out var sat) && sat.TryGetDouble(out var satVal))
                ShadowingAccuracyThreshold = satVal;
            if (data.TryGetValue("StalemateThreshold", out var st) && st.TryGetInt32(out var stVal))
                StalemateThreshold = stVal;

            _logger.LogInformation("Bootstrap: loaded persisted thresholds — teaching={T:F3}, shadowing={S:F3}",
                TeachingAccuracyThreshold, ShadowingAccuracyThreshold);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Bootstrap: failed to load persisted thresholds (non-critical)");
        }
    }

    public async Task PersistThresholdsAsync(CancellationToken ct = default)
    {
        try
        {
            var dir = Path.GetDirectoryName(_thresholdsFile);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var data = new
            {
                TeachingAccuracyThreshold,
                ShadowingAccuracyThreshold,
                StalemateThreshold
            };

            var json = JsonSerializer.Serialize(data);
            var tmpPath = _thresholdsFile + ".tmp";
            await File.WriteAllTextAsync(tmpPath, json, ct).ConfigureAwait(false);
            File.Move(tmpPath, _thresholdsFile, true);

            _logger.LogDebug("Bootstrap: persisted thresholds to {File}", _thresholdsFile);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Bootstrap: failed to persist thresholds (non-critical)");
        }
    }
}
