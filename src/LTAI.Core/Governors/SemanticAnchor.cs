using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.Governors;

public sealed record AnchorState
{
    public string Label { get; init; } = "";
    public float RhoD { get; set; }     // effective support (ρ_d)
    public float DR { get; set; }       // representational mismatch (d_r)
    public float GammaLogK { get; set; } // adaptive anchoring budget (γ log k)
    public bool IsAnchored { get; set; }
    public double PhaseTransitionPoint { get; set; }
    public float[]? Centroid { get; set; }
    public int Observations { get; set; }
    public DateTime LastAnchoredAt { get; set; } = DateTime.UtcNow;
}

public sealed class SemanticAnchor
{
    private readonly ConcurrentDictionary<string, AnchorState> _anchors = new();
    private readonly ParetoRouter _router;
    private readonly ILogger<SemanticAnchor> _logger;
    private float _phaseThreshold;
    private float _adaptiveGamma;
    private int _totalObservations;
    private int _anchoredCount;

    public int AnchorCount => _lockedByPhaseTransition ? _anchors.Count(a => a.Value.IsAnchored) : _anchors.Count;
    public int TotalObservations => _totalObservations;
    public int AnchoredLabels => _anchoredCount;
    public double AnchorRate => _totalObservations > 0 ? (double)_anchoredCount / _totalObservations : 0;
    private bool _lockedByPhaseTransition = false;

    public SemanticAnchor(
        ParetoRouter router,
        float phaseThreshold = 0.75f,
        float adaptiveGamma = 0.1f,
        ILogger<SemanticAnchor>? logger = null)
    {
        _router = router;
        _phaseThreshold = phaseThreshold;
        _adaptiveGamma = adaptiveGamma;
        _logger = logger ?? NullLogger<SemanticAnchor>.Instance;
    }

    public AnchorState Observe(string label, float[] embedding, float quality)
    {
        Interlocked.Increment(ref _totalObservations);

        var anchor = _anchors.GetOrAdd(label, _ => new AnchorState
        {
            Label = label,
            RhoD = 0,
            DR = 0,
            GammaLogK = 0,
            IsAnchored = false
        });

        anchor.Observations++;
        UpdateRhoD(anchor, embedding, quality);
        anchor.DR = ComputeDr(anchor, embedding);
        anchor.GammaLogK = _adaptiveGamma * MathF.Log(Math.Max(1, anchor.Observations));

        if (anchor.Observations > 0)
        {
            anchor.Centroid = UpdateCentroid(anchor.Centroid, embedding, anchor.Observations);
        }

        var phasePoint = anchor.RhoD - anchor.DR + anchor.GammaLogK;
        anchor.PhaseTransitionPoint = phasePoint;

        if (phasePoint > _phaseThreshold && !anchor.IsAnchored)
        {
            anchor.IsAnchored = true;
            anchor.LastAnchoredAt = DateTime.UtcNow;
            Interlocked.Increment(ref _anchoredCount);

            var point = new ParetoPoint
            {
                Id = $"anchor_{label}_{anchor.Observations}",
                Label = label,
                Quality = Math.Clamp(anchor.RhoD, 0, 1),
                Speed = 1.0f - Math.Clamp(anchor.DR, 0, 1),
                Cost = Math.Max(0, 1.0f - anchor.GammaLogK),
                Embedding = anchor.Centroid ?? embedding
            };

            _router.AddFrontierPoint(point);
            _router.PruneDominated();

            _logger.LogInformation("[SemanticAnchor] Phase transition: '{Label}' anchored " +
                "(ρ_d={RhoD:F2}, d_r={DR:F2}, γlogk={Gamma:F2}, phase={Phase:F2})",
                label, anchor.RhoD, anchor.DR, anchor.GammaLogK, phasePoint);
        }

        return anchor;
    }

    public AnchorState? GetAnchor(string label)
    {
        _anchors.TryGetValue(label, out var anchor);
        return anchor;
    }

    public bool IsLabelAnchored(string label)
        => _anchors.TryGetValue(label, out var a) && a.IsAnchored;

    public IReadOnlyList<AnchorState> GetAnchoredLabels()
        => _anchors.Values.Where(a => a.IsAnchored).OrderByDescending(a => a.RhoD).ToList();

    public ParetoPoint? GetAnchoredPoint(string label)
    {
        if (!IsLabelAnchored(label)) return null;
        var a = _anchors[label];
        return new ParetoPoint
        {
            Id = $"anchor_{label}",
            Label = label,
            Quality = Math.Clamp(a.RhoD, 0, 1),
            Speed = 1.0f - Math.Clamp(a.DR, 0, 1),
            Cost = Math.Max(0, 1.0f - a.GammaLogK),
            Embedding = a.Centroid ?? new float[768]
        };
    }

    public void SetPhaseThreshold(float threshold)
    {
        var clamped = Math.Clamp(threshold, 0.01f, 2.0f);
        _logger.LogInformation("[SemanticAnchor] Phase threshold: {Old:F2} → {New:F2}", _phaseThreshold, clamped);
        _phaseThreshold = clamped;
    }

    public void SetAdaptiveGamma(float gamma)
    {
        var clamped = Math.Clamp(gamma, 0.001f, 1.0f);
        _logger.LogInformation("[SemanticAnchor] Adaptive gamma: {Old:F2} → {New:F2}", _adaptiveGamma, clamped);
        _adaptiveGamma = clamped;
    }

    public (float PhaseThreshold, float AdaptiveGamma) GetAnchorParams()
        => (_phaseThreshold, _adaptiveGamma);

    public void Reset()
    {
        _anchors.Clear();
        _totalObservations = 0;
        _anchoredCount = 0;
        _logger.LogInformation("SemanticAnchor reset");
    }

    private void UpdateRhoD(AnchorState anchor, float[] embedding, float quality)
    {
        var current = anchor.RhoD;
        var n = anchor.Observations;

        var update = quality * EmbeddingMagnitude(embedding);
        anchor.RhoD = current + (update - current) / n;
    }

    private float ComputeDr(AnchorState anchor, float[] embedding)
    {
        if (anchor.Centroid == null) return 0;
        return 1.0f - CosineSimilarity(anchor.Centroid, embedding);
    }

    private static float[] UpdateCentroid(float[]? current, float[] incoming, int totalObs)
    {
        if (current == null) return incoming.ToArray();
        var result = new float[current.Length];
        for (int i = 0; i < current.Length; i++)
            result[i] = current[i] + (incoming[i] - current[i]) / totalObs;
        return result;
    }

    private static float EmbeddingMagnitude(float[] emb)
    {
        float sum = 0;
        for (int i = 0; i < emb.Length; i++) sum += emb[i] * emb[i];
        return MathF.Sqrt(sum / emb.Length);
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        float dot = 0, normA = 0, normB = 0;
        int len = Math.Min(a.Length, b.Length);
        for (int i = 0; i < len; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        if (normA == 0 || normB == 0) return 0;
        return dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
    }
}
