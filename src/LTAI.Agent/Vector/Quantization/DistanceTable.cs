// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  DistanceTable — Asymmetric Distance Computation lookup table
//
//  Phase 1c: pre-computed lookup table for fast ADC distance computation.
//  Once built, each encoded vector's distance is computed in O(M) via
//  table lookups — no floating-point arithmetic per candidate.
//
//  Usage:
//    var table = new DistanceTable(pq, query);
//    foreach (var encoded in candidates)
//        float dist = table.Lookup(encoded);
// ═══════════════════════════════════════════════════════════════

using System;

namespace LTAI.Agent.Vector.Quantization;

/// <summary>
/// Pre-computed distance lookup table for Asymmetric Distance Computation
/// (ADC). Once constructed from a query vector, distances to PQ-encoded
/// vectors are computed via O(M) table lookups.
///
/// Thread-safe after construction (table is immutable).
/// </summary>
public sealed class DistanceTable
{
    private readonly float[][] _lut; // [subQuantizerIndex][centroidIndex]
    private readonly int _m;

    /// <summary>Number of sub-quantizers (M).</summary>
    public int SubQuantizerCount => _m;

    /// <summary>
    /// Build a distance lookup table from a query vector.
    /// </summary>
    /// <param name="codec">Trained PQ codec.</param>
    /// <param name="query">Query vector to precompute distances for.</param>
    public DistanceTable(IPqCodec codec, float[] query)
    {
        _m = codec.SubQuantizerCount;
        _lut = codec.ComputeDistanceTable(query);
    }

    /// <summary>
    /// Build from a pre-computed LUT (e.g. from IPqCodec.ComputeDistanceTable).
    /// </summary>
    public DistanceTable(float[][] lut)
    {
        _lut = lut ?? throw new ArgumentNullException(nameof(lut));
        _m = lut.Length;
    }

    /// <summary>
    /// Look up the approximate distance between the query and an encoded vector.
    /// O(M) — M table lookups + accumulate.
    /// </summary>
    public float Lookup(byte[] encoded)
    {
        if (encoded.Length != _m)
            throw new ArgumentException(
                $"Expected {_m} bytes, got {encoded.Length}");

        float total = 0;
        for (int sub = 0; sub < _m; sub++)
            total += _lut[sub][encoded[sub]];
        return total / _m;
    }

    /// <summary>
    /// Lookup for multiple encoded vectors in batch (cache-local).
    /// </summary>
    public float[] LookupBatch(byte[][] encodedVectors)
    {
        var results = new float[encodedVectors.Length];
        for (int i = 0; i < encodedVectors.Length; i++)
            results[i] = Lookup(encodedVectors[i]);
        return results;
    }
}
