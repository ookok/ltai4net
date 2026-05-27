using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

// ============================================================================
// Weight Subspace Hypothesis implementation (Kaushik et al. 2025, arXiv:2512.05117)
// DNNs converge to shared low-dimensional spectral subspaces across tasks/domains.
// This analyzer performs PCA, subspace overlap, and projection for efficient
// knowledge transfer, model merging, and federated weight sharing.
// ============================================================================

public sealed record SubspaceComponents
{
    public float[][] Basis { get; init; } = Array.Empty<float[]>();
    public float[] SingularValues { get; init; } = Array.Empty<float>();
    public int Rank { get; init; }
    public double ExplainedVarianceRatio { get; init; }
    public int OriginalDim { get; init; }
    public string SourceId { get; init; } = "";
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

public sealed record SubspaceOverlap
{
    public double OverlapScore { get; init; }
    public double GrassmannDistance { get; init; }
    public double SharedVarianceRatio { get; init; }
    public int SharedRank { get; init; }
    public string SourceA { get; init; } = "";
    public string SourceB { get; init; } = "";
}

public sealed record WeightProjection
{
    public float[][] Projected { get; init; } = Array.Empty<float[]>();
    public float[][] Residual { get; init; } = Array.Empty<float[]>();
    public double CompressionRatio { get; init; }
    public double ReconstructionError { get; init; }
    public int OriginalSize { get; init; }
    public int ProjectedSize { get; init; }
}

public sealed class WeightSubspaceAnalyzer
{
    private readonly ILogger<WeightSubspaceAnalyzer> _logger;
    private readonly ConcurrentDictionary<string, SubspaceComponents> _registeredSubspaces = new();
    private SubspaceComponents? _universalSubspace;
    private int _analyzedCount;
    private readonly double _varianceRetentionThreshold;
    private readonly int _minComponents;

    public WeightSubspaceAnalyzer(
        double varianceRetentionThreshold = 0.95,
        int minComponents = 4,
        ILogger<WeightSubspaceAnalyzer>? logger = null)
    {
        _varianceRetentionThreshold = varianceRetentionThreshold;
        _minComponents = minComponents;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<WeightSubspaceAnalyzer>.Instance;
    }

    // ========================================================================
    // 1. PCA via Power Iteration — find top-K principal components efficiently
    // ========================================================================

    public SubspaceComponents Analyze(float[][] weightMatrix, string sourceId, int? maxComponents = null)
    {
        if (weightMatrix is null or { Length: 0 })
            return new SubspaceComponents { SourceId = sourceId };

        var n = weightMatrix.Length;
        var m = weightMatrix[0].Length;
        var k = maxComponents ?? Math.Min(Math.Min(_minComponents, n), m);

        var meanVector = ComputeMean(weightMatrix, n, m);
        var centered = CenterMatrix(weightMatrix, meanVector, n, m);

        var covariance = ComputeCovariance(centered, n, m);

        var (eigenvectors, eigenvalues) = PowerIterationTopK(covariance, m, k);

        var totalVariance = 0.0;
        for (int i = 0; i < m; i++)
            totalVariance += covariance[i][i];

        var explainedVariance = eigenvalues.Take(k).Sum();
        var ratio = totalVariance > 0 ? explainedVariance / totalVariance : 0;

        var result = new SubspaceComponents
        {
            Basis = eigenvectors.Take(k).ToArray(),
            SingularValues = eigenvalues.Take(k).Select(v => (float)Math.Sqrt(Math.Abs(v))).ToArray(),
            Rank = k,
            ExplainedVarianceRatio = ratio,
            OriginalDim = m,
            SourceId = sourceId
        };

        _registeredSubspaces[sourceId] = result;
        Interlocked.Increment(ref _analyzedCount);

        if (_analyzedCount % 10 == 0)
            UpdateUniversalSubspace();

        _logger.LogInformation(
            "Subspace Analysis: id={SourceId} dim={Dim} rank={Rank} explainedVar={Var:P2}",
            sourceId, m, k, ratio);

        return result;
    }

    // ========================================================================
    // 2. Subspace Overlap — Grassmann distance between two subspaces
    // ========================================================================

    public SubspaceOverlap ComputeOverlap(SubspaceComponents a, SubspaceComponents b)
    {
        var maxRank = Math.Min(a.Basis.Length, b.Basis.Length);
        if (maxRank == 0)
            return new SubspaceOverlap { SourceA = a.SourceId, SourceB = b.SourceId };

        var q = ComputeGrassmannDistance(a.Basis, b.Basis, maxRank);

        var grassmannDist = Math.Sqrt(Math.Max(0, a.Basis.Length - q));
        var principalAngles = ComputePrincipalAngles(a.Basis, b.Basis, maxRank);

        var sharedRank = principalAngles.Count(x => x < Math.PI / 6);
        var avgCosine = principalAngles.Select(x => Math.Cos(x)).Average();

        return new SubspaceOverlap
        {
            OverlapScore = Math.Max(0, avgCosine),
            GrassmannDistance = grassmannDist,
            SharedVarianceRatio = (double)sharedRank / maxRank,
            SharedRank = sharedRank,
            SourceA = a.SourceId,
            SourceB = b.SourceId
        };
    }

    // ========================================================================
    // 3. Weight Projection — project weights onto a subspace for compression
    // ========================================================================

    public WeightProjection Project(float[][] weightMatrix, SubspaceComponents subspace)
    {
        var n = weightMatrix.Length;
        var m = weightMatrix[0].Length;
        var k = subspace.Basis.Length;

        var projected = new float[n][];
        var residual = new float[n][];
        var totalError = 0.0;

        for (int i = 0; i < n; i++)
        {
            projected[i] = new float[k];
            residual[i] = new float[m];

            for (int j = 0; j < k; j++)
            {
                var dot = 0f;
                for (int d = 0; d < m; d++)
                    dot += weightMatrix[i][d] * subspace.Basis[j][d];
                projected[i][j] = dot;
            }

            for (int d = 0; d < m; d++)
            {
                var recon = 0f;
                for (int j = 0; j < k; j++)
                    recon += projected[i][j] * subspace.Basis[j][d];
                residual[i][d] = weightMatrix[i][d] - recon;
                totalError += residual[i][d] * residual[i][d];
            }
        }

        var originalSize = n * m * sizeof(float);
        var projectedSize = n * k * sizeof(float);

        return new WeightProjection
        {
            Projected = projected,
            Residual = residual,
            CompressionRatio = (double)projectedSize / originalSize,
            ReconstructionError = totalError / (n * m),
            OriginalSize = originalSize,
            ProjectedSize = projectedSize
        };
    }

    // ========================================================================
    // 4. Project a single vector onto subspace (for embedding compression)
    // ========================================================================

    public float[] ProjectVector(float[] vector, SubspaceComponents subspace)
    {
        var k = subspace.Basis.Length;
        var projection = new float[k];

        for (int j = 0; j < k; j++)
        {
            var dot = 0f;
            for (int d = 0; d < vector.Length; d++)
                dot += vector[d] * subspace.Basis[j][d];
            projection[j] = dot;
        }

        return projection;
    }

    public float[] ReconstructVector(float[] projection, SubspaceComponents subspace)
    {
        var k = subspace.Basis.Length;
        var m = subspace.OriginalDim;
        var result = new float[m];

        for (int d = 0; d < m; d++)
        {
            var val = 0f;
            for (int j = 0; j < k; j++)
                val += projection[j] * subspace.Basis[j][d];
            result[d] = val;
        }

        return result;
    }

    // ========================================================================
    // 5. Universal Subspace — aggregate from all analyzed subspaces
    // ========================================================================

    public SubspaceComponents? GetUniversalSubspace() => _universalSubspace;

    private void UpdateUniversalSubspace()
    {
        if (_registeredSubspaces.Count < 2) return;

        var allBasis = new List<float[]>();
        var dim = 0;

        foreach (var (_, sub) in _registeredSubspaces)
        {
            allBasis.AddRange(sub.Basis);
            if (sub.OriginalDim > dim) dim = sub.OriginalDim;
        }

        if (allBasis.Count == 0) return;

        var aggregatedMatrix = new float[allBasis.Count][];
        for (int i = 0; i < allBasis.Count; i++)
        {
            aggregatedMatrix[i] = allBasis[i].Length == dim
                ? allBasis[i]
                : PadVector(allBasis[i], dim);
        }

        _universalSubspace = Analyze(aggregatedMatrix, "universal", Math.Min(_minComponents, allBasis.Count));

        _logger.LogInformation(
            "Universal subspace updated: rank={Rank} explainedVar={Var:P2} contributors={Count}",
            _universalSubspace.Rank, _universalSubspace.ExplainedVarianceRatio, _registeredSubspaces.Count);
    }

    // ========================================================================
    // 6. Subspace-aware merging — weighted average with subspace bias
    // ========================================================================

    public double ComputeMergeWeight(SubspaceComponents modelA, SubspaceComponents modelB)
    {
        var overlap = ComputeOverlap(modelA, modelB);
        return overlap.OverlapScore >= 0.7 ? 0.5 + overlap.OverlapScore * 0.5 : overlap.OverlapScore * 0.5;
    }

    // ========================================================================
    // Stats
    // ========================================================================

    public Dictionary<string, object> GetStats() => new()
    {
        ["analyzed_count"] = _analyzedCount,
        ["registered_subspaces"] = _registeredSubspaces.Count,
        ["universal_subspace_rank"] = _universalSubspace?.Rank ?? 0,
        ["universal_subspace_variance"] = _universalSubspace?.ExplainedVarianceRatio ?? 0,
        ["contributors"] = _registeredSubspaces.Keys.ToList()
    };

    // ========================================================================
    // Private math helpers
    // ========================================================================

    private static float[] ComputeMean(float[][] matrix, int n, int m)
    {
        var mean = new float[m];
        for (int j = 0; j < m; j++)
        {
            var sum = 0f;
            for (int i = 0; i < n; i++)
                sum += matrix[i][j];
            mean[j] = sum / n;
        }
        return mean;
    }

    private static float[][] CenterMatrix(float[][] matrix, float[] mean, int n, int m)
    {
        var centered = new float[n][];
        for (int i = 0; i < n; i++)
        {
            centered[i] = new float[m];
            for (int j = 0; j < m; j++)
                centered[i][j] = matrix[i][j] - mean[j];
        }
        return centered;
    }

    private static float[][] ComputeCovariance(float[][] centered, int n, int m)
    {
        var cov = new float[m][];
        for (int i = 0; i < m; i++)
        {
            cov[i] = new float[m];
            for (int j = 0; j <= i; j++)
            {
                var sum = 0f;
                for (int k = 0; k < n; k++)
                    sum += centered[k][i] * centered[k][j];
                var val = sum / (n - 1);
                cov[i][j] = val;
                cov[j][i] = val;
            }
        }
        return cov;
    }

    private static (float[][] eigenvectors, double[] eigenvalues) PowerIterationTopK(
        float[][] covariance, int m, int k)
    {
        var eigenvecs = new List<float[]>();
        var eigenvals = new List<double>();
        var residual = covariance.Select(row => row.ToArray()).ToArray();

        for (int comp = 0; comp < k; comp++)
        {
            var v = new float[m];
            v[comp % m] = 1f;

            for (int iter = 0; iter < 100; iter++)
            {
                var newV = new float[m];
                var norm = 0f;

                for (int i = 0; i < m; i++)
                {
                    var dot = 0f;
                    for (int j = 0; j < m; j++)
                        dot += residual[i][j] * v[j];
                    newV[i] = dot;
                    norm += dot * dot;
                }

                if (norm < 1e-12f) break;

                norm = MathF.Sqrt(norm);
                for (int i = 0; i < m; i++)
                    newV[i] /= norm;

                var changed = 0f;
                for (int i = 0; i < m; i++)
                    changed = Math.Max(changed, MathF.Abs(newV[i] - v[i]));

                v = newV;
                if (changed < 1e-7f) break;
            }

            var eigval = ComputeRayleighQuotient(residual, v, m);
            eigenvecs.Add(v);
            eigenvals.Add(eigval);

            for (int i = 0; i < m; i++)
            for (int j = 0; j < m; j++)
                residual[i][j] -= (float)(eigval * v[i] * v[j]);
        }

        return (eigenvecs.ToArray(), eigenvals.ToArray());
    }

    private static double ComputeRayleighQuotient(float[][] mat, float[] v, int m)
    {
        var num = 0.0;
        var den = 0.0;

        for (int i = 0; i < m; i++)
        {
            var rowDot = 0.0;
            for (int j = 0; j < m; j++)
                rowDot += mat[i][j] * v[j];
            num += v[i] * rowDot;
            den += v[i] * v[i];
        }

        return den > 0 ? num / den : 0;
    }

    // Grassmann distance: sqrt(k - ||A^T B||^2_F)
    private static double ComputeGrassmannDistance(float[][] basisA, float[][] basisB, int maxRank)
    {
        var frobSq = 0.0;
        for (int i = 0; i < basisA.Length; i++)
        for (int j = 0; j < basisB.Length; j++)
        {
            var dot = 0.0;
            for (int d = 0; d < Math.Min(basisA[i].Length, basisB[j].Length); d++)
                dot += basisA[i][d] * basisB[j][d];
            frobSq += dot * dot;
        }

        return Math.Max(0, Math.Sqrt(maxRank - frobSq));
    }

    private static List<double> ComputePrincipalAngles(float[][] basisA, float[][] basisB, int maxRank)
    {
        var angles = new List<double>();

        for (int i = 0; i < Math.Min(basisA.Length, basisB.Length); i++)
        {
            var maxCos = 0.0;
            for (int j = 0; j < basisB.Length; j++)
            {
                var dot = 0.0;
                var dim = Math.Min(basisA[i].Length, basisB[j].Length);
                for (int d = 0; d < dim; d++)
                    dot += basisA[i][d] * basisB[j][d];
                maxCos = Math.Max(maxCos, Math.Abs(dot));
            }
            angles.Add(Math.Acos(Math.Min(1.0, maxCos)));
        }

        return angles;
    }

    private static float[] PadVector(float[] vec, int targetDim)
    {
        var padded = new float[targetDim];
        Array.Copy(vec, padded, Math.Min(vec.Length, targetDim));
        return padded;
    }
}
