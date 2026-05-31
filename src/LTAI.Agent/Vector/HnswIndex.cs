using System.Buffers;

namespace LTAI.Agent.Vector;

/// <summary>
/// HNSW (Hierarchical Navigable Small World) index for approximate nearest neighbor search.
/// Thread-safe: concurrent reads, exclusive writes.
/// </summary>
public sealed class HnswIndex : IDisposable
{
    private sealed record HnswNode(float[] Vector, List<int>[] Links);
    private readonly List<HnswNode> _nodes = [];
    private volatile int _entryPoint = -1;
    private int _maxLevel;
    private readonly ReaderWriterLockSlim _rwLock = new();

    private const int M = 16;
    private const int Mmax = 32;
    private const int Mmax0 = M;
    private const int EfConstruction = 200;
    private static readonly double ML = 1.0 / Math.Log(M);

    private static readonly Random _rng = new();

    public int Count { get { _rwLock.EnterReadLock(); try { return _nodes.Count; } finally { _rwLock.ExitReadLock(); } } }

    /// <summary>Insert a vector and return its index position.</summary>
    public int Insert(float[] vector)
    {
        var level = RandomLevel();
        var node = new HnswNode(vector, new List<int>[level + 1]);
        for (int l = 0; l <= level; l++) node.Links[l] = [];

        _rwLock.EnterWriteLock();
        try
        {
            int idx = _nodes.Count;
            _nodes.Add(node);

            if (_entryPoint < 0)
            {
                _entryPoint = idx;
                _maxLevel = level;
                return idx;
            }

            int currEntry = _entryPoint;
            float dist;
            // Phase 1: traverse from top to level+1, find nearest entry
            for (int l = _maxLevel; l > level; l--)
            {
                (currEntry, dist) = SearchLayer(query: vector, entry: currEntry, ef: 1, layer: l);
            }

            // Phase 2: insert at each level from min(level, maxLevel) down to 0
            var candidates = new List<(int idx, float dist)>();
            for (int l = Math.Min(level, _maxLevel); l >= 0; l--)
            {
                var ef = l == level ? EfConstruction : 1;
                var eps = new List<int> { currEntry };
                candidates = SearchLayerBatched(query: vector, eps: eps, ef: ef, layer: l);

                var neighbors = SelectNeighbors(candidates, l == 0 ? Mmax0 : Mmax);
                node.Links[l].AddRange(neighbors);

                foreach (var (nid, _) in neighbors.Select(n => (n, 0f)))
                {
                    var neighborNode = _nodes[nid];
                    var linkList = neighborNode.Links[l];
                    linkList.Add(idx);
                    if (linkList.Count > (l == 0 ? Mmax0 : Mmax))
                        ShrinkConnections(nid, l);
                }
            }

            if (level > _maxLevel)
            {
                _maxLevel = level;
                _entryPoint = idx;
            }

            return idx;
        }
        finally { _rwLock.ExitWriteLock(); }
    }

    /// <summary>Search top-K nearest neighbors by cosine distance.</summary>
    public List<(int index, float distance)> Search(float[] query, int topK)
    {
        _rwLock.EnterReadLock();
        try
        {
            if (_entryPoint < 0 || _nodes.Count == 0) return [];

            int currEntry = _entryPoint;
            for (int l = _maxLevel; l > 0; l--)
            {
                (currEntry, _) = SearchLayer(query, currEntry, ef: 1, l);
            }

            var candidates = SearchLayerBatched(query, [currEntry], ef: topK * 4, layer: 0);

            candidates.Sort((a, b) => a.dist.CompareTo(b.dist));
            return candidates.Take(topK).Select(c => (c.idx, c.dist)).ToList();
        }
        finally { _rwLock.ExitReadLock(); }
    }

    private (int nearest, float minDist) SearchLayer(float[] query, int entry, int ef, int layer)
    {
        var visited = new HashSet<int> { entry };
        var candidates = new SortedSet<(float dist, int idx)> { (Dist(query, _nodes[entry].Vector), entry) };
        var results = new SortedSet<(float dist, int idx)> { (Dist(query, _nodes[entry].Vector), entry) };

        while (candidates.Count > 0)
        {
            var (dCur, cur) = candidates.Min;
            candidates.Remove(candidates.Min);
            var farthest = results.Max.dist;

            if (dCur > farthest) break;

            foreach (var neighbor in _nodes[cur].Links[layer])
            {
                if (visited.Add(neighbor))
                {
                    var d = Dist(query, _nodes[neighbor].Vector);
                    if (d < farthest || results.Count < ef)
                    {
                        candidates.Add((d, neighbor));
                        results.Add((d, neighbor));
                        if (results.Count > ef)
                            results.Remove(results.Max);
                        farthest = results.Max.dist;
                    }
                }
            }
        }

        return (results.Min.idx, results.Min.dist);
    }

    private List<(int idx, float dist)> SearchLayerBatched(float[] query, List<int> eps, int ef, int layer)
    {
        var visited = new HashSet<int>(eps);
        var candidates = new SortedSet<(float dist, int idx)>();
        var results = new SortedSet<(float dist, int idx)>();

        foreach (var e in eps)
        {
            var d = Dist(query, _nodes[e].Vector);
            candidates.Add((d, e));
            results.Add((d, e));
        }

        while (candidates.Count > 0)
        {
            var (dCur, cur) = candidates.Min;
            candidates.Remove(candidates.Min);
            var farthest = results.Max.dist;

            if (dCur > farthest) break;

            foreach (var neighbor in _nodes[cur].Links[layer])
            {
                if (visited.Add(neighbor))
                {
                    var d = Dist(query, _nodes[neighbor].Vector);
                    if (d < farthest || results.Count < ef)
                    {
                        candidates.Add((d, neighbor));
                        results.Add((d, neighbor));
                        if (results.Count > ef)
                        {
                            var max = results.Max;
                            results.Remove(max);
                        }
                        farthest = results.Max.dist;
                    }
                }
            }
        }

        return results.Select(r => (r.idx, r.dist)).ToList();
    }

    private static List<int> SelectNeighbors(List<(int idx, float dist)> candidates, int M)
    {
        candidates.Sort((a, b) => a.dist.CompareTo(b.dist));
        return candidates.Take(M).Select(c => c.idx).ToList();
    }

    private void ShrinkConnections(int nodeIdx, int layer)
    {
        var node = _nodes[nodeIdx];
        var links = node.Links[layer];
        var vec = node.Vector;
        // Keep only the M closest neighbors
        var scored = links.Select(n => (idx: n, dist: Dist(vec, _nodes[n].Vector)))
                          .OrderBy(x => x.dist)
                          .Take(layer == 0 ? Mmax0 : Mmax)
                          .Select(x => x.idx)
                          .ToList();
        links.Clear();
        links.AddRange(scored);
    }

    private static int RandomLevel()
    {
        return (int)(-Math.Log(_rng.NextDouble()) * ML);
    }

    internal static float Dist(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        float dot = 0, normA = 0, normB = 0;
        int i = 0;

        if (System.Numerics.Vector.IsHardwareAccelerated && a.Length >= System.Numerics.Vector<float>.Count)
        {
            int vecLen = System.Numerics.Vector<float>.Count;
            var aVecs = System.Runtime.InteropServices.MemoryMarshal.Cast<float, System.Numerics.Vector<float>>(a);
            var bVecs = System.Runtime.InteropServices.MemoryMarshal.Cast<float, System.Numerics.Vector<float>>(b);
            var vdot = System.Numerics.Vector<float>.Zero;
            var vna = System.Numerics.Vector<float>.Zero;
            var vnb = System.Numerics.Vector<float>.Zero;
            for (int j = 0; j < aVecs.Length; j++)
            {
                vdot += aVecs[j] * bVecs[j];
                vna += aVecs[j] * aVecs[j];
                vnb += bVecs[j] * bVecs[j];
            }
            for (int k = 0; k < vecLen; k++)
            {
                dot += vdot[k];
                normA += vna[k];
                normB += vnb[k];
            }
            i += aVecs.Length * vecLen;
        }

        for (; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom == 0 ? 1 : 1 - dot / denom;
    }

    /// <summary>Rebuild index from an enumerable of vectors (clears existing).</summary>
    public void Rebuild(IEnumerable<float[]> vectors)
    {
        _rwLock.EnterWriteLock();
        try
        {
            _nodes.Clear();
            _entryPoint = -1;
            _maxLevel = 0;
            foreach (var v in vectors) Insert(v);
        }
        finally { _rwLock.ExitWriteLock(); }
    }

    public void Dispose()
    {
        _rwLock.Dispose();
    }
}
