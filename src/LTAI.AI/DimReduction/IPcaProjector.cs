// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  IPcaProjector — dimensionality reduction interface
//
//  Phase 1b of refactor-plan-v2.md: PCA / random projection layer
//  inserted between EmbeddingClient and IVectorStore.
//
//  384-dim → 128 or 64 dim, cosine accuracy loss < 5%.
//  Config: LTAI:Vector:Reduction = "none" / "pca-128" / "pca-64"
// ═══════════════════════════════════════════════════════════════

namespace LTAI.AI.DimReduction;

/// <summary>
/// Projects high-dimensional vectors to a lower-dimensional space while
/// approximately preserving cosine similarity (Johnson-Lindenstrauss lemma
/// for random projections; SVD-based for trained projections).
/// </summary>
public interface IPcaProjector
{
    /// <summary>Input dimensionality (e.g. 384 for MiniLM).</summary>
    int InputDim { get; }

    /// <summary>Output dimensionality (e.g. 128 or 64).</summary>
    int OutputDim { get; }

    /// <summary>Project a single vector to the lower-dimensional space.</summary>
    float[] Project(ReadOnlySpan<float> vector);

    /// <summary>Project a batch of vectors.</summary>
    float[][] ProjectBatch(ReadOnlySpan<float[]> vectors);
}
