// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  ProductQuantizer — Product Quantization implementation
//
//  Phase 1c: splits D-dim vectors into M sub-spaces, each trained with
//  k-means (k=256, 1 byte per sub-quantizer index).
//
//  Compression ratio for 384-dim float32 (1536 bytes):
//    M=8:  8 bytes  (192x compression)
//    M=16: 16 bytes (96x)
//    M=32: 32 bytes (48x)
//
//  ADC search: O(M) per candidate after O(k×M×D) precomputation per query.
// ═══════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Linq;

namespace LTAI.Agent.Vector.Quantization;

/// <summary>
/// Product Quantizer with k-means trained codebooks.
///
/// Training: call <see cref="Fit(float[][], int)"/> with a representative
/// sample. After training, <see cref="Encode"/> compresses vectors and
/// <see cref="ComputeDistanceTable"/> enables fast ADC search.
///
/// Thread-safety: all public methods are thread-safe after construction.
/// Training (Fit) must happen before use.
/// </summary>
public sealed class ProductQuantizer : IPqCodec
{
    /// <summary>Number of centroids per sub-space (k). Fixed at 256 = 1 byte.</summary>
    public const int K = 256;

    private readonly int _m;        // sub-quantizer count
    private readonly int _dim;      // input dimension
    private readonly int _subDim;   // dim per sub-space
    private float[][][]? _codebooks; // [m][k][subDim]
    private bool _trained;
    private readonly object _lock = new();
    private bool _disposed;

    public int SubQuantizerCount => _m;
    public int EncodedSize => _m; // 1 byte per sub-quantizer (k=256)
    public int Dimension => _dim;

    private ProductQuantizer(int dim, int m)
    {
        _dim = dim;
        _m = m;
        _subDim = (dim + m - 1) / m; // ceil division
    }

    /// <summary>
    /// Train a ProductQuantizer on a sample of vectors.
    /// </summary>
    /// <param name="samples">Training vectors (all must have same dimension).</param>
    /// <param name="m">Number of sub-quantizers (sub-spaces).</param>
    /// <param name="maxIterations">K-means iterations per sub-space.</param>
    /// <returns>A trained ProductQuantizer instance.</returns>
    public static ProductQuantizer Fit(float[][] samples, int m, int maxIterations = 20)
    {
        if (samples.Length == 0)
            throw new ArgumentException("At least one sample required for training");
        int dim = samples[0].Length;
        if (m <= 0 || m > dim)
            throw new ArgumentOutOfRangeException(nameof(m), $"M must be in [1, {dim}]");

        var pq = new ProductQuantizer(dim, m);
        pq.Train(samples, maxIterations);
        return pq;
    }

    /// <summary>
    /// Create an untrained ProductQuantizer. Caller must call
    /// <see cref="Train(float[][], int)"/> before encoding.
    /// Used for deserialization scenarios.
    /// </summary>
    public static ProductQuantizer CreateUntrained(int dim, int m)
    {
        return new ProductQuantizer(dim, m);
    }

    /// <summary>Train the codebooks on provided samples.</summary>
    public void Train(float[][] samples, int maxIterations = 20)
    {
        lock (_lock)
        {
            if (_trained) return;

            _codebooks = new float[_m][][];
            var rng = new Random(42);

            for (int sub = 0; sub < _m; sub++)
            {
                // Extract sub-vectors for this subspace
                int offset = sub * _subDim;
                int actualSubDim = Math.Min(_subDim, _dim - offset);
                var subVectors = new float[samples.Length][];
                for (int s = 0; s < samples.Length; s++)
                {
                    var sv = new float[actualSubDim];
                    Array.Copy(samples[s], offset, sv, 0, actualSubDim);
                    subVectors[s] = sv;
                }

                // K-means++ initialization
                var centroids = KMeansPlusPlus(subVectors, K, rng);

                // K-means iterations
                for (int iter = 0; iter < maxIterations; iter++)
                {
                    // Assignment step
                    var assignments = new int[subVectors.Length];
                    for (int s = 0; s < subVectors.Length; s++)
                        assignments[s] = FindNearestCentroid(subVectors[s], centroids);

                    // Update step
                    var newCentroids = new float[K][];
                    var counts = new int[K];
                    for (int c = 0; c < K; c++)
                        newCentroids[c] = new float[actualSubDim];

                    for (int s = 0; s < subVectors.Length; s++)
                    {
                        int c = assignments[s];
                        for (int d = 0; d < actualSubDim; d++)
                            newCentroids[c][d] += subVectors[s][d];
                        counts[c]++;
                    }

                    // Handle empty clusters
                    for (int c = 0; c < K; c++)
                    {
                        if (counts[c] == 0)
                        {
                            // Re-initialize with random sample
                            var randSample = subVectors[rng.Next(subVectors.Length)];
                            Array.Copy(randSample, newCentroids[c], actualSubDim);
                            counts[c] = 1;
                        }
                        else
                        {
                            for (int d = 0; d < actualSubDim; d++)
                                newCentroids[c][d] /= counts[c];
                        }
                    }

                    // Check convergence
                    bool converged = true;
                    for (int c = 0; c < K && converged; c++)
                    {
                        float diff = 0;
                        for (int d = 0; d < actualSubDim; d++)
                        {
                            float delta = centroids[c][d] - newCentroids[c][d];
                            diff += delta * delta;
                        }
                        if (diff > 1e-6f) converged = false;
                    }

                    centroids = newCentroids;
                    if (converged) break;
                }

                _codebooks[sub] = centroids;
            }

            _trained = true;
        }
    }

    /// <inheritdoc />
    public byte[] Encode(float[] vector)
    {
        ThrowIfNotTrained();
        if (vector.Length != _dim)
            throw new ArgumentException($"Expected {_dim}-dim vector, got {vector.Length}");

        var encoded = new byte[_m];
        for (int sub = 0; sub < _m; sub++)
        {
            int offset = sub * _subDim;
            int actualSubDim = Math.Min(_subDim, _dim - offset);

            var subVec = new float[actualSubDim];
            Array.Copy(vector, offset, subVec, 0, actualSubDim);

            encoded[sub] = (byte)FindNearestCentroid(subVec, _codebooks[sub]);
        }
        return encoded;
    }

    /// <inheritdoc />
    public float[] Decode(byte[] encoded)
    {
        ThrowIfNotTrained();
        if (encoded.Length != _m)
            throw new ArgumentException($"Expected {_m} bytes, got {encoded.Length}");

        var result = new float[_dim];
        for (int sub = 0; sub < _m; sub++)
        {
            int centroidIdx = encoded[sub];
            var centroid = _codebooks[sub][centroidIdx];
            int offset = sub * _subDim;
            Array.Copy(centroid, 0, result, offset, centroid.Length);
        }
        return result;
    }

    /// <inheritdoc />
    public float[][] ComputeDistanceTable(float[] query)
    {
        ThrowIfNotTrained();
        if (query.Length != _dim)
            throw new ArgumentException($"Expected {_dim}-dim query, got {query.Length}");

        var table = new float[_m][];
        for (int sub = 0; sub < _m; sub++)
        {
            int offset = sub * _subDim;
            int actualSubDim = Math.Min(_subDim, _dim - offset);

            // Extract query sub-vector
            float[] qSub = new float[actualSubDim];
            Array.Copy(query, offset, qSub, 0, actualSubDim);

            // Compute distance to each centroid
            var codebook = _codebooks[sub];
            table[sub] = new float[K];
            for (int c = 0; c < K; c++)
            {
                float dot = 0, normQ = 0, normC = 0;
                for (int d = 0; d < actualSubDim; d++)
                {
                    dot += qSub[d] * codebook[c][d];
                    normQ += qSub[d] * qSub[d];
                    normC += codebook[c][d] * codebook[c][d];
                }
                float denom = MathF.Sqrt(normQ) * MathF.Sqrt(normC);
                table[sub][c] = denom == 0 ? 1.0f : 1.0f - dot / denom; // cosine distance
            }
        }
        return table;
    }

    /// <inheritdoc />
    public float AdcDistance(float[][] distanceTable, byte[] encoded)
    {
        // Sum of sub-vector distances (for L2/PQ distance).
        // For cosine distance ADC, we use the asymmetric approximation:
        // d(q, x) ≈ sum over sub-spaces of d_sub(q_sub, centroid_idx(x_sub))
        float totalDist = 0;
        for (int sub = 0; sub < _m; sub++)
            totalDist += distanceTable[sub][encoded[sub]];
        return totalDist / _m; // normalize by sub-space count
    }

    /// <summary>Serialize codebooks to a compact format (for caching).</summary>
    public byte[] Serialize()
    {
        ThrowIfNotTrained();
        // Format: [dim:4][m:4][subDim:4] then for each sub: [k:4][codebook: k*subDim*4 bytes]
        using var ms = new System.IO.MemoryStream();
        using var bw = new System.IO.BinaryWriter(ms);
        bw.Write(_dim);
        bw.Write(_m);
        bw.Write(_subDim);

        for (int sub = 0; sub < _m; sub++)
        {
            var codebook = _codebooks[sub];
            int k = codebook.Length;
            int subDim = codebook[0].Length;
            bw.Write(k);
            bw.Write(subDim);
            for (int c = 0; c < k; c++)
                for (int d = 0; d < subDim; d++)
                    bw.Write(codebook[c][d]);
        }

        return ms.ToArray();
    }

    /// <summary>Deserialize codebooks from previously serialized data.</summary>
    public static ProductQuantizer Deserialize(byte[] data)
    {
        using var ms = new System.IO.MemoryStream(data);
        using var br = new System.IO.BinaryReader(ms);
        int dim = br.ReadInt32();
        int m = br.ReadInt32();
        int subDim = br.ReadInt32();

        var pq = new ProductQuantizer(dim, m)
        {
            _codebooks = new float[m][][],
            _trained = true
        };

        for (int sub = 0; sub < m; sub++)
        {
            int k = br.ReadInt32();
            int actualSubDim = br.ReadInt32();
            var codebook = new float[k][];
            for (int c = 0; c < k; c++)
            {
                codebook[c] = new float[actualSubDim];
                for (int d = 0; d < actualSubDim; d++)
                    codebook[c][d] = br.ReadSingle();
            }
            pq._codebooks[sub] = codebook;
        }

        return pq;
    }

    private void ThrowIfNotTrained()
    {
        if (!_trained)
            throw new InvalidOperationException(
                "ProductQuantizer not trained. Call Train() or use Fit() to create.");
    }

    private static int FindNearestCentroid(float[] vector, float[][] centroids)
    {
        int best = 0;
        float bestDist = float.MaxValue;
        for (int c = 0; c < centroids.Length; c++)
        {
            float dist = 0;
            for (int d = 0; d < vector.Length; d++)
            {
                float delta = vector[d] - centroids[c][d];
                dist += delta * delta;
            }
            if (dist < bestDist)
            {
                bestDist = dist;
                best = c;
            }
        }
        return best;
    }

    private static float[][] KMeansPlusPlus(float[][] data, int k, Random rng)
    {
        int n = data.Length;
        int dim = data[0].Length;
        var centroids = new float[k][];

        // First centroid: random sample
        centroids[0] = new float[dim];
        Array.Copy(data[rng.Next(n)], centroids[0], dim);

        // Remaining centroids: distance-weighted sampling
        var minDists = new float[n];
        for (int c = 1; c < k; c++)
        {
            float totalDist = 0;
            for (int s = 0; s < n; s++)
            {
                float minDist = float.MaxValue;
                for (int pc = 0; pc < c; pc++)
                {
                    float dist = 0;
                    for (int d = 0; d < dim; d++)
                    {
                        float delta = data[s][d] - centroids[pc][d];
                        dist += delta * delta;
                    }
                    if (dist < minDist) minDist = dist;
                }
                minDists[s] = minDist;
                totalDist += minDist;
            }

            // Weighted random selection
            float sample = (float)rng.NextDouble() * totalDist;
            float cumSum = 0;
            int chosen = 0;
            for (int s = 0; s < n; s++)
            {
                cumSum += minDists[s];
                if (cumSum >= sample) { chosen = s; break; }
            }

            centroids[c] = new float[dim];
            Array.Copy(data[chosen], centroids[c], dim);
        }

        return centroids;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
