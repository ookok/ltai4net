using System.Text.Json;
using TurboQuant.Core.Packing;

namespace LTAI.Agent.Vector;

/// <summary>
/// Configurable HNSW index parameters.
/// </summary>
public sealed record HnswOptions
{
    /// <summary>Number of bi-directional links per element (default 16). Higher = more accurate, more memory.</summary>
    public int M { get; init; } = 16;

    /// <summary>Maximum number of links per element at all levels (default 32).</summary>
    public int Mmax { get; init; } = 32;

    /// <summary>Maximum number of links per element at level 0 (default 16).</summary>
    public int Mmax0 { get; init; } = 16;

    /// <summary>Search effort during construction (default 200). Higher = better quality, slower build.</summary>
    public int EfConstruction { get; init; } = 200;

    internal double ML => 1.0 / Math.Log(M);

    /// <summary>Default options (balanced).</summary>
    public static HnswOptions Default => new();

    /// <summary>High-accuracy options (slower build, better recall).</summary>
    public static HnswOptions HighAccuracy => new() { M = 32, Mmax = 64, Mmax0 = 32, EfConstruction = 400 };

    /// <summary>Low-memory options (faster build, lower recall).</summary>
    public static HnswOptions LowMemory => new() { M = 8, Mmax = 16, Mmax0 = 8, EfConstruction = 100 };
}

/// <summary>
/// Hierarchical Navigable Small World index for approximate nearest neighbor search.
/// Supports only insert and search operations. Individual node removal is not supported;
/// use <see cref="Rebuild"/> to clear and rebuild the entire index when nodes are deleted.
/// </summary>
public sealed class HnswIndex : IDisposable
{
    private sealed record HnswNode(PackedVector Packed, List<int>[] Links);
    private readonly List<HnswNode> _nodes = [];
    private volatile int _entryPoint = -1;
    private int _maxLevel;
    private readonly ReaderWriterLockSlim _rwLock = new();

    private readonly int _m;
    private readonly int _mmax;
    private readonly int _mmax0;
    private readonly int _efConstruction;
    private readonly double _ml;

    private static readonly Random _rng = Random.Shared;

    public HnswIndex(HnswOptions? options = null)
    {
        options ??= HnswOptions.Default;
        _m = options.M;
        _mmax = options.Mmax;
        _mmax0 = options.Mmax0;
        _efConstruction = options.EfConstruction;
        _ml = 1.0 / Math.Log(_m);
    }

    public int Count { get { _rwLock.EnterReadLock(); try { return _nodes.Count; } finally { _rwLock.ExitReadLock(); } } }

    public int Insert(ReadOnlySpan<float> vector)
        => InsertPacked(VectorQuantizer.Quantize(vector.ToArray()));

    public int InsertPacked(PackedVector packed)
    {
        var level = RandomLevel();
        var node = new HnswNode(packed, new List<int>[level + 1]);
        for (int l = 0; l <= level; l++) node.Links[l] = [];

        // Phase 1 (upgradeable read): allocate index + read-only search.
        // Other readers continue; only one upgradeable thread at a time.
        _rwLock.EnterUpgradeableReadLock();
        try
        {
            int idx = _nodes.Count;
            _nodes.Add(node);

            if (_entryPoint < 0)
            {
                _rwLock.EnterWriteLock();
                try { _entryPoint = idx; _maxLevel = level; }
                finally { _rwLock.ExitWriteLock(); }
                return idx;
            }

            int currEntry = _entryPoint;
            for (int l = _maxLevel; l > level; l--)
                (currEntry, _) = SearchLayer(packed, currEntry, ef: 1, layer: l);

            var candidates = new List<(int idx, float dist)>();
            for (int l = Math.Min(level, _maxLevel); l >= 0; l--)
            {
                var ef = l == level ? _efConstruction : 1;
                candidates = SearchLayerBatched(packed, [currEntry], ef, l);
                var neighbors = SelectNeighbors(candidates, l == 0 ? _mmax0 : _mmax);
                node.Links[l].AddRange(neighbors);
            }

            // Phase 2 (write): modify graph structure — brief, only when needed
            _rwLock.EnterWriteLock();
            try
            {
                for (int l = Math.Min(level, _maxLevel); l >= 0; l--)
                {
                    foreach (var nid in node.Links[l])
                    {
                        var neighborNode = _nodes[nid];
                        var linkList = neighborNode.Links[l];
                        linkList.Add(idx);
                        if (linkList.Count > (l == 0 ? _mmax0 : _mmax))
                            ShrinkConnections(nid, l);
                    }
                }

                if (level > _maxLevel)
                {
                    _maxLevel = level;
                    _entryPoint = idx;
                }
            }
            finally { _rwLock.ExitWriteLock(); }

            return idx;
        }
        finally { _rwLock.ExitUpgradeableReadLock(); }
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
                           .Take(layer == 0 ? _mmax0 : _mmax)
                          .Select(x => x.idx)
                          .ToList();
        links.Clear();
        links.AddRange(scored);
    }

    private int RandomLevel()
    {
        return (int)(-Math.Log(_rng.NextDouble()) * _ml);
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

    /// <summary>Snapshot the index to a stream (JSON format).</summary>
    public void SaveSnapshot(Stream stream)
    {
        _rwLock.EnterReadLock();
        try
        {
            var nodes = _nodes.Select(n => new
            {
                Data = Convert.ToBase64String(n.Packed.ToBytes()),
                Links = n.Links.Select(l => l.ToArray()).ToArray()
            }).ToList();
            var snapshot = new
            {
                EntryPoint = _entryPoint,
                MaxLevel = _maxLevel,
                M = _m,
                Mmax = _mmax,
                Mmax0 = _mmax0,
                EfConstruction = _efConstruction,
                Nodes = nodes
            };
            JsonSerializer.Serialize(stream, snapshot);
        }
        finally { _rwLock.ExitReadLock(); }
    }

    /// <summary>Load a snapshot from a stream. Replaces current index contents.</summary>
    public static HnswIndex LoadSnapshot(Stream stream, HnswOptions? options = null)
    {
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        var idx = new HnswIndex(options ?? new HnswOptions
        {
            M = root.GetProperty("M").GetInt32(),
            Mmax = root.GetProperty("Mmax").GetInt32(),
            Mmax0 = root.GetProperty("Mmax0").GetInt32(),
            EfConstruction = root.GetProperty("EfConstruction").GetInt32()
        });
        idx._entryPoint = root.GetProperty("EntryPoint").GetInt32();
        idx._maxLevel = root.GetProperty("MaxLevel").GetInt32();
        foreach (var nodeEl in root.GetProperty("Nodes").EnumerateArray())
        {
            var data = Convert.FromBase64String(nodeEl.GetProperty("Data").GetString()!);
            var packed = TurboQuant.Core.Packing.PackedVector.FromBytes(data);
            var linksArr = nodeEl.GetProperty("Links").EnumerateArray()
                .Select(l => l.EnumerateArray().Select(e => e.GetInt32()).ToArray())
                .ToArray();
            var node = new HnswNode(packed, linksArr.Select(l => new List<int>(l)).ToArray());
            idx._nodes.Add(node);
        }
        return idx;
    }

    /// <summary>Save snapshot to a file path.</summary>
    public void SaveSnapshotToFile(string path)
    {
        using var fs = File.Create(path);
        SaveSnapshot(fs);
    }

    /// <summary>Load snapshot from a file path.</summary>
    public static HnswIndex LoadSnapshotFromFile(string path, HnswOptions? options = null)
    {
        using var fs = File.OpenRead(path);
        return LoadSnapshot(fs, options);
    }

    public void Dispose()
    {
        _rwLock.Dispose();
    }
}
