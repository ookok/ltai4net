// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  TrainedPca — SVD-based PCA projector (higher accuracy)
//
//  Phase 1b: trains on a sample of embeddings to learn the optimal
//  projection matrix via SVD of the covariance matrix.
//
//  Unlike RandomPca, this requires a training step but provides
//  strictly better accuracy for the target domain.
//
//  Training: Fit(samples) → extracts top-k principal components
//  Projection: multiply vector by the (OutputDim × InputDim) matrix
// ═══════════════════════════════════════════════════════════════

using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace LTAI.AI.DimReduction;

/// <summary>
/// PCA trained via eigendecomposition of the covariance matrix.
///
/// Training algorithm:
///   1. Center the data (subtract mean)
///   2. Compute covariance matrix (InputDim × InputDim)
///   3. Extract top-k eigenvectors via power iteration
///   4. Projection matrix = transpose(top-k eigenvectors)
///
/// Thread-safe after construction (projection matrix + mean are frozen).
/// </summary>
public sealed class TrainedPca : IPcaProjector, IDisposable
{
    private readonly float[] _mean;       // InputDim
    private readonly float[][] _components; // [OutputDim][InputDim]
    private bool _disposed;

    public int InputDim { get; }
    public int OutputDim { get; }

    /// <summary>
    /// Number of power-iteration steps for eigenvector extraction.
    /// Higher = more accurate, slower training.
    /// </summary>
    public int PowerIterations { get; }

    private TrainedPca(int inputDim, int outputDim, float[] mean,
        float[][] components, int powerIterations)
    {
        InputDim = inputDim;
        OutputDim = outputDim;
        _mean = mean;
        _components = components;
        PowerIterations = powerIterations;
    }

    /// <summary>
    /// Train the PCA projector on a sample of vectors.
    /// </summary>
    /// <param name="samples">Training data (each vector of length InputDim).</param>
    /// <param name="outputDim">Target dimensionality.</param>
    /// <param name="powerIterations">Power iteration count for SVD. Default 10.</param>
    /// <returns>A trained TrainedPca instance.</returns>
    public static TrainedPca Fit(
        ReadOnlySpan<float[]> samples,
        int outputDim,
        int powerIterations = 10)
    {
        if (samples.Length == 0)
            throw new ArgumentException("At least one sample required for training");

        int inputDim = samples[0].Length;
        if (outputDim <= 0 || outputDim > inputDim)
            throw new ArgumentOutOfRangeException(nameof(outputDim));

        // Step 1: Compute mean vector
        var mean = new float[inputDim];
        foreach (var vec in samples)
        {
            for (int i = 0; i < inputDim; i++)
                mean[i] += vec[i];
        }
        float invN = 1.0f / samples.Length;
        for (int i = 0; i < inputDim; i++)
            mean[i] *= invN;

        // Step 2: Center the data
        var centered = new float[samples.Length][];
        for (int s = 0; s < samples.Length; s++)
        {
            var c = new float[inputDim];
            for (int i = 0; i < inputDim; i++)
                c[i] = samples[s][i] - mean[i];
            centered[s] = c;
        }

        // Step 3: Compute covariance matrix (X^T * X) / (n-1)
        // We use the centered data matrix directly.
        // covariance[i,j] = sum over samples of c[s][i] * c[s][j] / (n-1)
        var cov = new float[inputDim][];
        for (int i = 0; i < inputDim; i++)
        {
            cov[i] = new float[inputDim];
            for (int j = i; j < inputDim; j++)
            {
                float sum = 0;
                for (int s = 0; s < centered.Length; s++)
                    sum += centered[s][i] * centered[s][j];
                sum /= (samples.Length - 1);
                cov[i][j] = sum;
                cov[j][i] = sum; // symmetric
            }
        }

        // Step 4: Extract top-k eigenvectors via power iteration
        var components = ExtractTopEigenvectors(cov, outputDim, powerIterations);

        return new TrainedPca(inputDim, outputDim, mean, components, powerIterations);
    }

    /// <summary>
    /// Extract top-k eigenvectors from a symmetric matrix via power iteration +
    /// Hotelling deflation.
    /// </summary>
    private static float[][] ExtractTopEigenvectors(
        float[][] matrix, int k, int iterations)
    {
        int n = matrix.Length;
        var eigenvectors = new float[k][];

        // Work on a copy to avoid mutating the original
        var working = new float[n][];
        for (int i = 0; i < n; i++)
        {
            working[i] = new float[n];
            Array.Copy(matrix[i], working[i], n);
        }

        for (int comp = 0; comp < k; comp++)
        {
            // Initialize with random vector
            var rng = new Random(42 + comp);
            var eigenvec = new float[n];
            for (int i = 0; i < n; i++)
                eigenvec[i] = (float)(rng.NextDouble() * 2 - 1);

            // Power iteration
            for (int iter = 0; iter < iterations; iter++)
            {
                // Multiply: result = working * eigenvec
                var result = new float[n];
                for (int i = 0; i < n; i++)
                {
                    float sum = 0;
                    for (int j = 0; j < n; j++)
                        sum += working[i][j] * eigenvec[j];
                    result[i] = sum;
                }

                // Normalize
                float norm = 0;
                for (int i = 0; i < n; i++)
                    norm += result[i] * result[i];
                norm = MathF.Sqrt(norm);
                if (norm > 1e-10f)
                {
                    for (int i = 0; i < n; i++)
                        eigenvec[i] = result[i] / norm;
                }
            }

            eigenvectors[comp] = eigenvec;

            // Hotelling deflation: remove this component from the working matrix
            // Reconstruct the rank-1 approximation: lambda * v * v^T
            // First compute the eigenvalue: lambda = v^T * A * v
            float eigenvalue = 0;
            for (int i = 0; i < n; i++)
            {
                float rowSum = 0;
                for (int j = 0; j < n; j++)
                    rowSum += working[i][j] * eigenvec[j];
                eigenvalue += rowSum * eigenvec[i];
            }

            // Deflate: A = A - lambda * v * v^T
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                    working[i][j] -= eigenvalue * eigenvec[i] * eigenvec[j];
            }
        }

        return eigenvectors;
    }

    /// <inheritdoc />
    public float[] Project(ReadOnlySpan<float> vector)
    {
        if (vector.Length != InputDim)
            throw new ArgumentException(
                $"Expected {InputDim}-dim vector, got {vector.Length}");

        // Center the vector
        float[] centered;
        if (IsZeroMean(_mean))
        {
            centered = vector.ToArray();
        }
        else
        {
            centered = new float[InputDim];
            for (int i = 0; i < InputDim; i++)
                centered[i] = vector[i] - _mean[i];
        }

        // Project onto components
        var result = new float[OutputDim];
        for (int i = 0; i < OutputDim; i++)
        {
            var comp = _components[i];
            float dot = 0;

            // SIMD-accelerated dot product
            if (Vector.IsHardwareAccelerated && InputDim >= Vector<float>.Count)
            {
                int vecLen = Vector<float>.Count;
                var vResult = Vector<float>.Zero;
                int j = 0;

                for (; j <= InputDim - vecLen; j += vecLen)
                {
                    var vComp = new Vector<float>(comp.AsSpan(j));
                    var vCent = new Vector<float>(centered.AsSpan(j));
                    vResult += vComp * vCent;
                }

                for (int k = 0; k < vecLen; k++)
                    dot += vResult[k];

                for (; j < InputDim; j++)
                    dot += comp[j] * centered[j];
            }
            else
            {
                for (int j = 0; j < InputDim; j++)
                    dot += comp[j] * centered[j];
            }

            result[i] = dot;
        }

        return result;
    }

    /// <inheritdoc />
    public float[][] ProjectBatch(ReadOnlySpan<float[]> vectors)
    {
        var results = new float[vectors.Length][];
        for (int i = 0; i < vectors.Length; i++)
            results[i] = Project(vectors[i]);
        return results;
    }

    private static bool IsZeroMean(float[] mean)
    {
        foreach (var v in mean)
            if (Math.Abs(v) > 1e-10f) return false;
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
