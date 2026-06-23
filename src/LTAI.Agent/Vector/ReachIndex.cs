// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════
//  ReachIndex — precomputed depth-3 reachability for
//  O(seeds × community) impact analysis.
//
//  Inspired by zzet/gortex: baked reach index turns
//  blast-radius queries into O(1) map lookups.
// ═══════════════════════════════════════════════════════

using System.Collections.Concurrent;
using LTAI.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Vector;

/// <summary>
/// Precomputed reachability index for sub-millisecond impact analysis.
/// Built after graph construction; maps symbol → set of reachable symbols
/// at depth 1, 2, 3 with edge-kind filters.
/// </summary>
public sealed class ReachIndex
{
    // Depth-3 forward reach: symbolId → reachable set per depth
    private ConcurrentDictionary<long, ReachSet>? _forward;
    // Depth-3 reverse reach (who-calls-me): symbolId → reachable set per depth
    private ConcurrentDictionary<long, ReachSet>? _reverse;
    private readonly ILogger<ReachIndex>? _logger;

    private volatile bool _built;
    private int _version;

    public ReachIndex(ILogger<ReachIndex>? logger = null)
    {
        _logger = logger ?? NullLogger<ReachIndex>.Instance;
    }

    /// <summary>Number of indexed nodes.</summary>
    public int NodeCount => _forward?.Count ?? 0;

    /// <summary>True after BuildAsync completes.</summary>
    public bool Built => _built;

    /// <summary>Version stamp — incremented on each rebuild.</summary>
    public int Version => _version;

    /// <summary>
    /// Build the reach index from the graph store.
    /// Walks up to depth 3 via BFS for every node with outgoing CALLS edges,
    /// stores compressed reach sets.
    /// </summary>
    /// <summary>Max nodes to index. -1 = unlimited. Override via LTAI_REACH_INDEX_MAX_NODES env var.</summary>
    public static int MaxNodes { get; set; } = -1; // unlimited

    /// <summary>Max edges to load. -1 = unlimited. Override via LTAI_REACH_INDEX_MAX_EDGES env var.</summary>
    public static int MaxEdges { get; set; } = -1;

    // Static ctor reads env vars
    static ReachIndex()
    {
        MaxNodes = EnvironmentConfig.ReachIndexMaxNodes;
        MaxEdges = EnvironmentConfig.ReachIndexMaxEdges;
    }

    public async Task BuildAsync(KgStore store, CancellationToken ct = default)
    {
        var allNodes = await store.GetAllNodes().ConfigureAwait(false);
        var nodeIds = allNodes.Select(n => n.Id).ToList();

        // ── Bulk load CALLS edges (single query, shared for sampling + adj) ──
        var allEdges = await store.GetEdges(null, relation: "CALLS").ConfigureAwait(false);

        // Apply node cap for very large graphs (e.g., linux kernel: 1.6M nodes)
        if (MaxNodes > 0 && nodeIds.Count > MaxNodes)
        {
            var edgeCounts = new ConcurrentDictionary<long, int>(Environment.ProcessorCount * 2, nodeIds.Count);
            foreach (var edge in allEdges)
            {
                edgeCounts.AddOrUpdate(edge.Src, 1, (_, c) => c + 1);
                edgeCounts.AddOrUpdate(edge.Dst, 1, (_, c) => c + 1);
            }
            nodeIds = nodeIds
                .OrderByDescending(id => edgeCounts.TryGetValue(id, out var c) ? c : 0)
                .Take(MaxNodes)
                .ToList();
            _logger?.LogInformation("ReachIndex: sampling {N} nodes with highest connectivity", nodeIds.Count);
        }

        // Apply edge cap
        if (MaxEdges > 0 && allEdges.Count > MaxEdges)
            allEdges = allEdges.OrderByDescending(e => e.Weight).Take(MaxEdges).ToList();

        var nodeSet = new HashSet<long>(nodeIds);
        var fwdAdj = new Dictionary<long, List<long>>();
        var revAdj = new Dictionary<long, List<long>>();

        foreach (var edge in allEdges)
        {
            if (nodeSet.Contains(edge.Src) && nodeSet.Contains(edge.Dst))
            {
                if (!fwdAdj.TryGetValue(edge.Src, out var fList))
                    fwdAdj[edge.Src] = fList = new List<long>();
                fList.Add(edge.Dst);

                if (!revAdj.TryGetValue(edge.Dst, out var rList))
                    revAdj[edge.Dst] = rList = new List<long>();
                rList.Add(edge.Src);
            }
        }

        _logger?.LogInformation("ReachIndex: loaded {E} edges for {N} nodes", allEdges.Count, nodeIds.Count);

        // ── Parallel BFS using in-memory adjacency ──
        var fwd = new ConcurrentDictionary<long, ReachSet>(Environment.ProcessorCount * 2, nodeIds.Count);
        var rev = new ConcurrentDictionary<long, ReachSet>(Environment.ProcessorCount * 2, nodeIds.Count);

        var opts = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
            CancellationToken = ct
        };

        await Parallel.ForEachAsync(nodeIds, opts, (nodeId, token) =>
        {
            token.ThrowIfCancellationRequested();

            // Forward
            var fwdD1 = BfsInMemory(fwdAdj, nodeId, 1);
            var fwdD2 = BfsInMemory(fwdAdj, nodeId, 2);
            var fwdD3 = BfsInMemory(fwdAdj, nodeId, 3);
            if (fwdD1.Count > 0 || fwdD2.Count > 0 || fwdD3.Count > 0)
                fwd[nodeId] = new ReachSet(
                    D1: fwdD1.ToArray(),
                    D2: fwdD2.ToArray(),
                    D3: fwdD3.ToArray());

            // Reverse
            var revD1 = BfsInMemory(revAdj, nodeId, 1);
            var revD2 = BfsInMemory(revAdj, nodeId, 2);
            var revD3 = BfsInMemory(revAdj, nodeId, 3);
            if (revD1.Count > 0 || revD2.Count > 0 || revD3.Count > 0)
                rev[nodeId] = new ReachSet(
                    D1: revD1.ToArray(),
                    D2: revD2.ToArray(),
                    D3: revD3.ToArray());

            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);

        Interlocked.Exchange(ref _forward, fwd);
        Interlocked.Exchange(ref _reverse, rev);
        Interlocked.Increment(ref _version);
        _built = true;
    }

    /// <summary>
    /// Query impact: "what breaks if I change this symbol?"
    /// Returns all symbols reachable in forward direction (things this symbol calls)
    /// and reverse direction (things that call this symbol), up to depth <paramref name="depth"/>.
    /// </summary>
    public ImpactResult QueryImpact(long symbolId, int depth = 3)
    {
        var fwd = _forward;
        var rev = _reverse;
        if (fwd == null || rev == null)
            return ImpactResult.Empty;

        var forward = fwd.TryGetValue(symbolId, out var f) ? f : ReachSet.Empty;
        var reverse = rev.TryGetValue(symbolId, out var r) ? r : ReachSet.Empty;

        var (affectedFwd, affectedRev) = depth switch
        {
            1 => (forward.D1, reverse.D1),
            2 => (Union(forward.D1, forward.D2), Union(reverse.D1, reverse.D2)),
            _ => (Union(forward.D1, forward.D2, forward.D3), Union(reverse.D1, reverse.D2, reverse.D3)),
        };

        return new ImpactResult(symbolId, affectedFwd, affectedRev, _version);
    }

    public void Invalidate()
    {
        _built = false;
        Interlocked.Increment(ref _version);
    }

    // ── Private helpers ──

    /// <summary>In-memory BFS using pre-loaded adjacency list.</summary>
    private static HashSet<long> BfsInMemory(Dictionary<long, List<long>> adj, long seed, int maxDepth)
    {
        var visited = new HashSet<long> { seed };
        var current = new List<long> { seed };
        var result = new HashSet<long>();

        for (int depth = 1; depth <= maxDepth; depth++)
        {
            if (current.Count == 0) break;
            var next = new List<long>();
            foreach (var nodeId in current)
            {
                if (!adj.TryGetValue(nodeId, out var neighbors)) continue;
                foreach (var n in neighbors)
                {
                    if (visited.Add(n))
                    {
                        next.Add(n);
                        result.Add(n);
                    }
                }
            }
            current = next;
        }

        return result;
    }

    private static long[] Union(long[] a, long[] b)
    {
        if (a.Length == 0) return b;
        if (b.Length == 0) return a;
        var set = new HashSet<long>(a);
        foreach (var x in b) set.Add(x);
        return set.ToArray();
    }

    private static long[] Union(long[] a, long[] b, long[] c)
    {
        var set = new HashSet<long>(a.Length + b.Length + c.Length);
        foreach (var x in a) set.Add(x);
        foreach (var x in b) set.Add(x);
        foreach (var x in c) set.Add(x);
        return set.ToArray();
    }
}

// ═══════════════════════════════════════════════════
//  Data types
// ═══════════════════════════════════════════════════

public sealed record ReachSet(long[] D1, long[] D2, long[] D3)
{
    public static readonly ReachSet Empty = new([], [], []);
}

public sealed record ImpactResult(
    long Seed,
    long[] ForwardReachable,
    long[] ReverseReachable,
    int IndexVersion)
{
    public static readonly ImpactResult Empty = new(0, [], [], -1);

    public int TotalAffected => ForwardReachable.Length + ReverseReachable.Length;
    public bool IsEmpty => TotalAffected == 0;
}
