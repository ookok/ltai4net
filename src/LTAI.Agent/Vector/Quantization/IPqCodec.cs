// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  IPqCodec — Product Quantization codec interface
//
//  Phase 1c of refactor-plan-v2.md: PQ compression for IVectorStore.
//
//  Encodes float vectors to compact byte[] representations via
//  product quantization (M sub-spaces, each with k-means codebook).
//  Supports ADC (Asymmetric Distance Computation) for fast search.
// ═══════════════════════════════════════════════════════════════

namespace LTAI.Agent.Vector.Quantization;

/// <summary>
/// Product Quantization codec. Encodes float vectors to compact byte[]
/// representations and decodes them back (with loss).
/// </summary>
public interface IPqCodec : IDisposable
{
    /// <summary>Number of sub-quantizers (M in PQ literature).</summary>
    int SubQuantizerCount { get; }

    /// <summary>Bytes per encoded vector (M × log2(k) / 8).</summary>
    int EncodedSize { get; }

    /// <summary>Input vector dimension.</summary>
    int Dimension { get; }

    /// <summary>
    /// Encode a float vector into a compact byte[] representation.
    /// </summary>
    byte[] Encode(float[] vector);

    /// <summary>
    /// Decode a byte[] back to a float vector (lossy reconstruction).
    /// </summary>
    float[] Decode(byte[] encoded);

    /// <summary>
    /// Pre-compute a distance lookup table for ADC search.
    /// Called once per query, then each encoded vector is looked up in O(M).
    /// </summary>
    /// <param name="query">Query vector (Dimension-length).</param>
    /// <returns>A lookup table: [subQuantizerIndex][centroidIndex] = distance.</returns>
    float[][] ComputeDistanceTable(float[] query);

    /// <summary>
    /// Compute approximate cosine distance between query and encoded vector
    /// using the precomputed ADC lookup table. O(SubQuantizerCount).
    /// </summary>
    float AdcDistance(float[][] distanceTable, byte[] encoded);
}
