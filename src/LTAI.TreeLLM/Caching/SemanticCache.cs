using System.Collections.Concurrent;
using LTAI.TreeLLM.Models;

namespace LTAI.TreeLLM.Caching;

public sealed class SemanticDedupCache
{
    private const int LshBits = 16;
    private const int LshHashMask = (1 << LshBits) - 1;

    private static readonly Lazy<SemanticDedupCache> LazyInstance = new(() => new SemanticDedupCache());
    public static SemanticDedupCache Instance => LazyInstance.Value;

    private readonly int _ttlSeconds;
    private readonly int _maxEntries;
    private readonly int _dimension;
    private readonly float[][] _hyperplanes;
    private readonly ConcurrentDictionary<int, CachedEntry> _store = new();
    private long _hits;
    private long _misses;

    private sealed class CachedEntry
    {
        public string Answer { get; }
        public long Timestamp { get; }
        public int AccessCount;

        public CachedEntry(string answer, long timestamp, int accessCount = 0)
        {
            Answer = answer;
            Timestamp = timestamp;
            AccessCount = accessCount;
        }
    }

    public SemanticDedupCache(int ttlSeconds = 3600, int maxEntries = 500, int dimension = 384)
    {
        _ttlSeconds = ttlSeconds;
        _maxEntries = maxEntries;
        _dimension = dimension;
        _hyperplanes = GenerateHyperplanes(LshBits, dimension, 42);
    }

    public string? Get(float[] embedding)
    {
        if (embedding == null || embedding.Length != _dimension)
            return null;

        var hash = ComputeLshHash(embedding, _hyperplanes);

        if (!_store.TryGetValue(hash, out var entry))
        {
            Interlocked.Increment(ref _misses);
            return null;
        }

        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - entry.Timestamp > _ttlSeconds)
        {
            _store.TryRemove(hash, out _);
            Interlocked.Increment(ref _misses);
            return null;
        }

        Interlocked.Increment(ref entry.AccessCount);
        Interlocked.Increment(ref _hits);
        return entry.Answer;
    }

    public void Set(float[] embedding, string answer)
    {
        if (embedding == null || embedding.Length != _dimension)
            return;

        var hash = ComputeLshHash(embedding, _hyperplanes);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        _store.AddOrUpdate(hash,
            _ => new CachedEntry(answer, now, 0),
            (_, existing) => new CachedEntry(answer, now, existing.AccessCount));

        if (_store.Count > _maxEntries)
            EvictLowest();
    }

    public double HitRate
    {
        get
        {
            var total = Interlocked.Read(ref _hits) + Interlocked.Read(ref _misses);
            return total == 0 ? 0.0 : (double)Interlocked.Read(ref _hits) / total;
        }
    }

    public IReadOnlyDictionary<string, object> Stats()
    {
        return new Dictionary<string, object>
        {
            ["entries"] = _store.Count,
            ["max_entries"] = _maxEntries,
            ["ttl_seconds"] = _ttlSeconds,
            ["hits"] = Interlocked.Read(ref _hits),
            ["misses"] = Interlocked.Read(ref _misses),
            ["hit_rate"] = HitRate,
            ["lsh_bits"] = LshBits,
            ["dimension"] = _dimension
        };
    }

    private static float[][] GenerateHyperplanes(int numPlanes, int dimension, int seed)
    {
        var rng = new Random(seed);
        var planes = new float[numPlanes][];

        for (var i = 0; i < numPlanes; i++)
        {
            planes[i] = new float[dimension];
            for (var j = 0; j < dimension; j++)
                planes[i][j] = (float)SampleGaussian(rng);
        }

        return planes;
    }

    private static int ComputeLshHash(float[] embedding, float[][] hyperplanes)
    {
        var hash = 0;

        for (var i = 0; i < hyperplanes.Length; i++)
        {
            var dot = 0.0f;
            var plane = hyperplanes[i];
            for (var j = 0; j < embedding.Length; j++)
                dot += embedding[j] * plane[j];

            if (dot > 0)
                hash |= 1 << i;
        }

        return hash & LshHashMask;
    }

    private void EvictLowest()
    {
        KeyValuePair<int, CachedEntry>? victim = null;
        var minAccess = int.MaxValue;

        foreach (var kvp in _store)
        {
            if (kvp.Value.AccessCount < minAccess)
            {
                minAccess = kvp.Value.AccessCount;
                victim = kvp;
            }
        }

        if (victim.HasValue)
            _store.TryRemove(victim.Value.Key, out _);
    }

    private static double SampleGaussian(Random rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }
}
