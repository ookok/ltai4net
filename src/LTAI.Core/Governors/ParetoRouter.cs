using System.Collections.Concurrent;
using System.Numerics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.Governors;

public sealed record ParetoPoint
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Label { get; init; } = "";  // reflex/local/L1/L2
    public float Quality { get; init; }
    public float Speed { get; init; }
    public float Cost { get; init; }
    public float[] Embedding { get; init; } = Array.Empty<float>();
}

public sealed record ParetoDecision
{
    public string Route { get; init; } = "local";
    public float Confidence { get; init; }
    public ParetoPoint? NearestPoint { get; init; }
    public bool IsShadowRouted { get; init; }
    public long ElapsedUs { get; init; }
}

public enum ParetoDistanceMetric
{
    Euclidean,
    Cosine,
    Mahalanobis
}

public sealed class ParetoRouter
{
    private readonly ILogger<ParetoRouter> _logger;
    private readonly ConcurrentDictionary<string, ParetoPoint> _frontier = new();
    private readonly float[][] _projectionMatrix; // [3, embeddingDim]
    private readonly ConcurrentQueue<ParetoDecision> _shadowLog = new();
    private int _totalDecisions;
    private int _shadowDecisions;
    private float _shadowRate = 0.10f;
    private readonly ParetoDistanceMetric _metric;
    private readonly object _mergeLock = new();
    private readonly GenePool? _genePool;

    private readonly string[] _routeHistory;
    private int _routeHistoryIndex;
    private readonly object _routeLock = new();
    private int _routeLockCounter;
    private string _lockedRoute = "";
    private const int RouteHistorySize = 32;
    private const float JitterThreshold = 0.40f;
    private const int LockDuration = 20;

    public ParetoRouter(
        int embeddingDim = 768,
        ParetoDistanceMetric metric = ParetoDistanceMetric.Cosine,
        ILogger<ParetoRouter>? logger = null,
        GenePool? genePool = null)
    {
        _logger = logger ?? NullLogger<ParetoRouter>.Instance;
        _metric = metric;
        _genePool = genePool;
        _projectionMatrix = InitializeProjectionMatrix(embeddingDim);
        _routeHistory = new string[RouteHistorySize];
        SeedDefaultFrontier();
    }

    public int FrontierSize => _frontier.Count;
    public int TotalDecisions => _totalDecisions;
    public float ShadowRate => _shadowRate;

    public ParetoDecision Decide(float[] embedding, string? triggerOverride = null)
    {
        var sw = global::System.Diagnostics.Stopwatch.StartNew();

        Interlocked.Increment(ref _totalDecisions);
        var isShadow = ShouldShadowRoute();

        if (triggerOverride != null)
        {
            RecordRouteDecision(triggerOverride);
            return new ParetoDecision
            {
                Route = triggerOverride,
                Confidence = 1.0f,
                IsShadowRouted = false,
                ElapsedUs = sw.ElapsedTicks * 1_000_000 / global::System.Diagnostics.Stopwatch.Frequency
            };
        }

        if (TryGetLockedRoute(out var lockedRoute))
        {
            return new ParetoDecision
            {
                Route = lockedRoute,
                Confidence = 0.85f,
                IsShadowRouted = isShadow,
                ElapsedUs = sw.ElapsedTicks * 1_000_000 / global::System.Diagnostics.Stopwatch.Frequency
            };
        }

        var projected = ProjectTo3D(embedding);

        var nearest = FindNearest(projected);
        var distance = nearest != null ? ComputeDistance(projected, ToVector(nearest), _metric) : float.MaxValue;
        var confidence = distance < 0.15f ? 0.95f :
                         distance < 0.30f ? 0.80f :
                         distance < 0.50f ? 0.60f : 0.35f;

        var route = nearest?.Label ?? "local";
        RecordRouteDecision(route);

        var jitter = GetJitter();
        if (jitter > JitterThreshold)
            EnterRouteLock(FindModeRoute());

        var decision = new ParetoDecision
        {
            Route = nearest?.Label ?? "local",
            Confidence = confidence,
            NearestPoint = nearest,
            IsShadowRouted = isShadow,
            ElapsedUs = sw.ElapsedTicks * 1_000_000 / global::System.Diagnostics.Stopwatch.Frequency
        };

        if (isShadow)
        {
            _shadowLog.Enqueue(decision);
            while (_shadowLog.Count > 100)
                _shadowLog.TryDequeue(out _);
        }

        return decision;
    }

    public void AddFrontierPoint(ParetoPoint point)
    {
        _frontier[point.Id] = point;
    }

    public void RemoveFrontierPoint(string id)
    {
        _frontier.TryRemove(id, out _);
    }

    public IReadOnlyList<ParetoPoint> GetFrontier() => _frontier.Values.ToList();

    public float[] ProjectEmbedding(float[] embedding)
    {
        return ProjectTo3D(embedding);
    }

    public void SetShadowRate(float rate)
    {
        _shadowRate = Math.Clamp(rate, 0f, 1f);
        _logger.LogInformation("Shadow rate set to {Rate:P0}", _shadowRate);
    }

    public ParetoDecision[] DrainShadowLog()
    {
        var batch = _shadowLog.ToArray();
        while (_shadowLog.TryDequeue(out _)) { }
        return batch;
    }

    public float GetJitter()
    {
        var count = 0;
        var routes = new List<string>();
        lock (_routeLock)
        {
            foreach (var r in _routeHistory)
            {
                if (r == null) continue;
                count++;
                routes.Add(r);
            }
        }
        if (count < 4) return 0;

        var modeKey = routes.GroupBy(r => r).OrderByDescending(g => g.Count()).First().Key;
        var different = routes.Count(r => r != modeKey);
        return (float)different / count;
    }

    private void RecordRouteDecision(string route)
    {
        lock (_routeLock)
        {
            _routeHistory[_routeHistoryIndex % RouteHistorySize] = route;
            _routeHistoryIndex++;
        }
    }

    private bool TryGetLockedRoute(out string route)
    {
        lock (_routeLock)
        {
            if (_routeLockCounter > 0)
            {
                _routeLockCounter--;
                route = _lockedRoute;
                return true;
            }
            route = "";
            return false;
        }
    }

    private void EnterRouteLock(string modeRoute)
    {
        lock (_routeLock)
        {
            _lockedRoute = modeRoute;
            _routeLockCounter = LockDuration;
            _logger.LogWarning("Jitter detected ({Jitter:F2}) — locking route to '{Route}' for {Duration} decisions",
                GetJitter(), modeRoute, LockDuration);
        }
    }

    private string FindModeRoute()
    {
        var routes = new List<string>();
        lock (_routeLock)
            routes = _routeHistory.Where(r => r != null).ToList();

        return routes.GroupBy(r => r!)
            .OrderByDescending(g => g.Count())
            .First().Key!;
    }

    public void RecordFeedback(string pointId, float quality, float speed, float cost)
    {
        if (_frontier.TryGetValue(pointId, out var existing))
        {
            var updated = existing with
            {
                Quality = (existing.Quality * 0.9f + quality * 0.1f),
                Speed = (existing.Speed * 0.9f + speed * 0.1f),
                Cost = (existing.Cost * 0.9f + cost * 0.1f)
            };
            _frontier[pointId] = updated;
        }

        PruneDominated();
    }

    public void UpdateProjectionMatrix(float[][] matrix)
    {
        lock (_mergeLock)
        {
            if (matrix.Length >= 3 && matrix[0] != null)
            {
                for (var i = 0; i < 3; i++)
                {
                    for (var j = 0; j < Math.Min(matrix[i].Length, _projectionMatrix[i].Length); j++)
                        _projectionMatrix[i][j] = matrix[i][j];
                }
                _logger.LogInformation("Projection matrix updated from external source");
            }
        }
    }

    public float[][] GetProjectionMatrix()
    {
        lock (_mergeLock)
        {
            var copy = new float[3][];
            for (var i = 0; i < 3; i++)
            {
                copy[i] = new float[_projectionMatrix[i].Length];
                Array.Copy(_projectionMatrix[i], copy[i], _projectionMatrix[i].Length);
            }
            return copy;
        }
    }

    public void PruneDominated()
    {
        var all = _frontier.Values.ToList();
        var dominated = new HashSet<string>();

        for (var i = 0; i < all.Count; i++)
        {
            for (var j = 0; j < all.Count; j++)
            {
                if (i == j) continue;
                if (IsDominated(all[i], all[j]))
                {
                    dominated.Add(all[i].Id);
                    break;
                }
            }
        }

        foreach (var id in dominated)
            _frontier.TryRemove(id, out _);

        if (dominated.Count > 0)
            _logger.LogDebug("Pruned {Count} dominated points from frontier", dominated.Count);
    }

    private float[] ProjectTo3D(float[] embedding)
    {
        var result = new float[3];
        for (var i = 0; i < 3; i++)
        {
            var sum = 0.0f;
            for (var j = 0; j < Math.Min(embedding.Length, _projectionMatrix[i].Length); j++)
                sum += embedding[j] * _projectionMatrix[i][j];
            result[i] = Sigmoid(sum);
        }
        return result;
    }

    private static float Sigmoid(float x) => 1.0f / (1.0f + MathF.Exp(-x));

    private static float[] ToVector(ParetoPoint p) => new[] { p.Quality, p.Speed, p.Cost };

    private ParetoPoint? FindNearest(float[] projected)
    {
        ParetoPoint? best = null;
        var bestDist = float.MaxValue;

        foreach (var point in _frontier.Values)
        {
            var pointVec = new[] { point.Quality, point.Speed, point.Cost };
            var dist = ComputeDistance(projected, pointVec, _metric);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = point;
            }
        }

        return best;
    }

    private static float ComputeDistance(float[] a, float[] b, ParetoDistanceMetric metric)
    {
        return metric switch
        {
            ParetoDistanceMetric.Cosine => 1.0f - CosineSimilarity(a, b),
            ParetoDistanceMetric.Euclidean => MathF.Sqrt(a.Zip(b, (x, y) => (x - y) * (x - y)).Sum()),
            _ => MathF.Sqrt(a.Zip(b, (x, y) => (x - y) * (x - y)).Sum())
        };
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        float dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        return normA < 1e-9f || normB < 1e-9f ? 0 : dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
    }

    private static bool IsDominated(ParetoPoint a, ParetoPoint b)
    {
        return a.Quality <= b.Quality && a.Speed <= b.Speed && a.Cost >= b.Cost;
    }

    private bool ShouldShadowRoute()
    {
        if (_shadowRate <= 0) return false;
        var r = Random.Shared.NextSingle();
        return r < _shadowRate;
    }

    private static float[][] InitializeProjectionMatrix(int embeddingDim)
    {
        var rng = new Random(42);
        var matrix = new float[3][];
        for (var i = 0; i < 3; i++)
        {
            matrix[i] = new float[embeddingDim];
            for (var j = 0; j < embeddingDim; j++)
                matrix[i][j] = (float)(rng.NextDouble() * 0.1 - 0.05);
        }
        return matrix;
    }

    private void SeedDefaultFrontier()
    {
        if (_genePool != null)
        {
            try
            {
                var topGenes = _genePool.SelectTopN(5);
                if (topGenes.Count > 0)
                {
                    var geneSeeds = topGenes.Select(g => new ParetoPoint
                    {
                        Id = $"gene_{g.Id}",
                        Label = g.RouteLabel switch
                        {
                            "reflex" => "reflex",
                            "local" => "local",
                            "L1" => "L1",
                            "L2" => "L2",
                            _ => "L1"
                        },
                        Quality = (float)Math.Clamp(g.Fitness, 0, 1),
                        Speed = g.RouteLabel switch { "reflex" => 1.0f, "L1" => 0.5f, "L2" => 0.15f, _ => 0.5f },
                        Cost = g.RouteLabel switch { "reflex" => 0.0f, "L1" => 0.15f, "L2" => 1.0f, _ => 0.15f },
                    }).ToList();

                    foreach (var seed in geneSeeds)
                        _frontier[seed.Id] = seed;

                    PruneDominated();
                    _logger.LogInformation("ParetoRouter initialized with {Count} gene-driven seed points",
                        _frontier.Count);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GenePool seed loading failed, falling back to hardcoded seeds");
            }
        }

        var seeds = new[]
        {
            new ParetoPoint { Id = "seed_reflex",   Label = "reflex",  Quality = 0.30f, Speed = 1.00f, Cost = 0.00f },
            new ParetoPoint { Id = "seed_local",    Label = "local",   Quality = 0.55f, Speed = 0.80f, Cost = 0.05f },
            new ParetoPoint { Id = "seed_l1",       Label = "L1",      Quality = 0.75f, Speed = 0.50f, Cost = 0.15f },
            new ParetoPoint { Id = "seed_l2",       Label = "L2",      Quality = 0.95f, Speed = 0.15f, Cost = 1.00f },
            new ParetoPoint { Id = "seed_l2_redux", Label = "L2",      Quality = 0.85f, Speed = 0.25f, Cost = 0.60f },
        };

        foreach (var seed in seeds)
            _frontier[seed.Id] = seed;

        PruneDominated();
        _logger.LogInformation("ParetoRouter initialized with {Count} hardcoded seed points (after Pareto pruning)",
            _frontier.Count);
    }
}
