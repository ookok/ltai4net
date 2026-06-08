// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  IVectorStore — vector storage abstraction
//
//  Phase 1a of refactor-plan-v2.md: extract HNSW search logic
//  from KgStore into a pluggable IVectorStore interface.
//
//  Intended implementations:
//    - HnswVectorStore : wraps the current TurboQuant + HNSW logic
//      (extracted from KgStore._hnsw + _hnswNodeIds)
//    - (future) PgVectorStore, QdrantVectorStore, etc.
// ═══════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LTAI.Agent.Vector;

/// <summary>
/// Vector storage abstraction for approximate nearest neighbor (ANN) search.
/// Supports insert, search, delete, and bulk rebuild operations.
/// </summary>
public interface IVectorStore : IDisposable
{
    /// <summary>Number of vectors currently stored.</summary>
    int Count { get; }

    /// <summary>Insert or update a vector embedding for a node.</summary>
    /// <param name="nodeId">Opaque node identifier (e.g. KgStore rowid).</param>
    /// <param name="embedding">Raw float embedding (Dim-dimensional).</param>
    void Insert(long nodeId, ReadOnlySpan<float> embedding);

    /// <summary>
    /// Search for the nearest neighbors of the query vector.
    /// Returns (nodeId, cosineDistance) pairs sorted by ascending distance.
    /// </summary>
    /// <param name="query">Query vector (Dim-dimensional).</param>
    /// <param name="topK">Maximum number of results.</param>
    /// <param name="ef">Optional search effort (higher = more accurate but slower).</param>
    List<(long nodeId, float distance)> Search(ReadOnlySpan<float> query, int topK, int ef = -1);

    /// <summary>Delete the vector entry for the given nodeId.</summary>
    void Delete(long nodeId);

    /// <summary>Rebuild the index from scratch. Clears all existing data.</summary>
    void Rebuild(IEnumerable<(long nodeId, float[] embedding)> vectors);

    /// <summary>Clear all vectors from the store.</summary>
    void Clear();
}
