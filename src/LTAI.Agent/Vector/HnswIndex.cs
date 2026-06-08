using TurboQuant.Core.Packing;

namespace LTAI.Agent.Vector;

public sealed class HnswIndex : IDisposable
{
    private sealed record HnswNode(PackedVector Packed, List<int>[] Links);
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

    public int Insert(ReadOnlySpan<float> vector)
        => InsertPacked(VectorQuantizer.Quantize(vector.ToArray()));

    public int InsertPacked(PackedVector packed)
    {
        var level = RandomLevel();
        var node = new HnswNode(packed, new List<int>[level + 1]);
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
            for (int l = _maxLevel; l > level; l--)
            {
                (currEntry, dist) = SearchLayer(packed, currEntry, ef: 1, layer: l);
            }

            var candidates = new List<(int idx, float dist)>();
            for (int l = Math.Min(level, _maxLevel); l >= 0; l--)
            {
                var ef = l == level ? EfConstruction : 1;
                var eps = new List<int> { currEntry };
                candidates = SearchLayerBatched(packed, eps, ef, l);

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

    public List<(int index, float distance)> Search(ReadOnlySpan<float> query, int topK)
    {
        _rwLock.EnterReadLock();
        try
        {
            if (_entryPoint < 0 || _nodes.Count == 0) return [];

            var queryPacked = VectorQuantizer.Quantize(query.ToArray());

            int currEntry = _entryPoint;
            for (int l = _maxLevel; l > 0; l--)
            {
                (currEntry, _) = SearchLayer(queryPacked, currEntry, ef: 1, l);
            }

            var candidates = SearchLayerBatched(queryPacked, [currEntry], ef: topK * 4, layer: 0);

            candidates.Sort((a, b) => a.dist.CompareTo(b.dist));
            return candidates.Take(topK).Select(c => (c.idx, c.dist)).ToList();
        }
        finally { _rwLock.ExitReadLock(); }
    }

    private (int nearest, float minDist) SearchLayer(PackedVector query, int entry, int ef, int layer)
    {
        var visited = new HashSet<int> { entry };
        var candidates = new SortedSet<(float dist, int idx)>
            { (VectorQuantizer.CosineDistance(query, _nodes[entry].Packed), entry) };
        var results = new SortedSet<(float dist, int idx)>
            { (VectorQuantizer.CosineDistance(query, _nodes[entry].Packed), entry) };

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
                    var d = VectorQuantizer.CosineDistance(query, _nodes[neighbor].Packed);
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

    private List<(int idx, float dist)> SearchLayerBatched(PackedVector query, List<int> eps, int ef, int layer)
    {
        var visited = new HashSet<int>(eps);
        var candidates = new SortedSet<(float dist, int idx)>();
        var results = new SortedSet<(float dist, int idx)>();

        foreach (var e in eps)
        {
            var d = VectorQuantizer.CosineDistance(query, _nodes[e].Packed);
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
                    var d = VectorQuantizer.CosineDistance(query, _nodes[neighbor].Packed);
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
        var packed = node.Packed;
        var scored = links.Select(n => (idx: n, dist: VectorQuantizer.CosineDistance(packed, _nodes[n].Packed)))
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

    public void Rebuild(IEnumerable<ReadOnlyMemory<float>> vectors)
    {
        _rwLock.EnterWriteLock();
        try
        {
            _nodes.Clear();
            _entryPoint = -1;
            _maxLevel = 0;
            foreach (var v in vectors) Insert(v.Span);
        }
        finally { _rwLock.ExitWriteLock(); }
    }

    public void Dispose()
    {
        _rwLock.Dispose();
    }
}
