// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  VectorSearchResult — result DTO for IVectorStore.Search
// ═══════════════════════════════════════════════════════════════

namespace LTAI.Agent.Vector;

/// <summary>
/// A single result from an IVectorStore nearest-neighbor search.
/// </summary>
/// <param name="NodeId">Opaque node identifier (e.g. KgStore rowid).</param>
/// <param name="Distance">Cosine distance [0, 2]. Lower = more similar.</param>
/// <param name="Metadata">Optional payload from the store (e.g. kind, source).</param>
public sealed record VectorSearchResult(
    long NodeId,
    float Distance,
    IReadOnlyDictionary<string, object>? Metadata = null);
