using System.Collections.Concurrent;
using System.Threading;

namespace LTAI.Vector.Knowledge;

public sealed record ShardKey(
    string ShardId,
    int BucketIndex,
    string SessionId)
{
    public static ShardKey From(string key, int totalShards)
    {
        var hash = (uint)key.GetHashCode();
        var bucket = (int)(hash % totalShards);
        return new ShardKey(key, bucket, $"shard_{bucket}");
    }
}

public sealed class ShardBlock
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Content { get; set; } = "";
    public double Score { get; set; }
    public long Version { get; set; }
    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public Dictionary<string, string> Metadata { get; set; } = new();
}

public sealed class ShardStats
{
    public int BlockCount;
    public long TotalBytes;
    public int ReadHits;
    public int WriteCount;
    public int CasConflicts;
    public int CrossShardMerges;
}

public sealed class ShardedMemoryStore
{
    private readonly int _totalShards;
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<string, ShardBlock>> _shards = new();
    private readonly ConcurrentDictionary<string, ShardStats> _shardStats = new();

    public ShardedMemoryStore(int totalShards = 8)
    {
        _totalShards = Math.Max(1, totalShards);
        for (int i = 0; i < _totalShards; i++)
        {
            _shards[i] = new();
            _shardStats[$"shard_{i}"] = new();
        }
    }

    public void Write(string key, string content, double score = 1.0, Dictionary<string, string>? metadata = null)
    {
        var shardKey = ShardKey.From(key, _totalShards);
        var bucket = GetBucket(shardKey);

        var block = new ShardBlock
        {
            Id = key,
            Content = content,
            Score = score,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Metadata = metadata ?? new()
        };

        bucket.AddOrUpdate(key,
            _ => { IncrWrite(shardKey); return block; },
            (_, existing) =>
            {
                existing.Version++;
                existing.Content = content;
                existing.Score = score;
                existing.Timestamp = block.Timestamp;
                existing.Metadata = block.Metadata;
                IncrWrite(shardKey);
                return existing;
            });
    }

    public bool CompareAndSwap(string key, string expectedContent, string newContent, double score = 1.0)
    {
        var shardKey = ShardKey.From(key, _totalShards);
        var bucket = GetBucket(shardKey);

        if (!bucket.TryGetValue(key, out var existing))
        {
            if (string.IsNullOrEmpty(expectedContent))
            {
                Write(key, newContent, score);
                return true;
            }
            return false;
        }

        if (existing.Content != expectedContent)
        {
            IncrCas(shardKey);
            return false;
        }

        existing.Content = newContent;
        existing.Score = score;
        existing.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        existing.Version++;
        IncrWrite(shardKey);
        return true;
    }

    public ShardBlock? Read(string key)
    {
        var shardKey = ShardKey.From(key, _totalShards);
        var bucket = GetBucket(shardKey);

        if (bucket.TryGetValue(key, out var block))
        {
            IncrRead(shardKey);
            return block;
        }
        return null;
    }

    public List<ShardBlock> SearchAllShards(string query, int topK = 10)
    {
        var allResults = new List<ShardBlock>();

        for (int i = 0; i < _totalShards; i++)
        {
            var bucket = _shards[i];

            foreach (var kv in bucket)
            {
                var relevance = ComputeRelevance(kv.Value, query);
                if (relevance > 0)
                {
                    kv.Value.Score = relevance;
                    allResults.Add(kv.Value);
                }
            }
        }

        IncrCrossShard();

        return allResults
            .OrderByDescending(b => b.Score)
            .Take(topK)
            .ToList();
    }

    public List<ShardBlock> SearchInShard(string query, int shardIndex, int topK = 5)
    {
        if (!_shards.TryGetValue(shardIndex, out var bucket))
            return new();

        var results = new List<ShardBlock>();
        foreach (var kv in bucket)
        {
            var relevance = ComputeRelevance(kv.Value, query);
            if (relevance > 0)
                results.Add(kv.Value);
        }

        return results
            .OrderByDescending(b => b.Score)
            .Take(topK)
            .ToList();
    }

    public bool Delete(string key)
    {
        var shardKey = ShardKey.From(key, _totalShards);
        var bucket = GetBucket(shardKey);
        return bucket.TryRemove(key, out _);
    }

    public int PurgeShard(int shardIndex, Func<ShardBlock, bool> predicate)
    {
        if (!_shards.TryGetValue(shardIndex, out var bucket))
            return 0;

        int removed = 0;
        var keysToRemove = new List<string>();

        foreach (var kv in bucket)
        {
            if (predicate(kv.Value))
                keysToRemove.Add(kv.Key);
        }

        foreach (var key in keysToRemove)
        {
            if (bucket.TryRemove(key, out _))
                removed++;
        }

        return removed;
    }

    public int GetTotalBlockCount()
    {
        return _shards.Sum(s => s.Value.Count);
    }

    public Dictionary<string, ShardStats> GetAllShardStats()
    {
        var result = new Dictionary<string, ShardStats>();
        foreach (var (name, stats) in _shardStats)
        {
            var bucket = _shards[int.Parse(name.Replace("shard_", ""))];
            var copy = new ShardStats
            {
                BlockCount = bucket.Count,
                TotalBytes = bucket.Values.Sum(b => (long)(b.Content.Length * 2)),
                ReadHits = stats.ReadHits,
                WriteCount = stats.WriteCount,
                CasConflicts = stats.CasConflicts,
                CrossShardMerges = stats.CrossShardMerges
            };
            result[name] = copy;
        }
        return result;
    }

    public void ClearAll()
    {
        for (int i = 0; i < _totalShards; i++)
        {
            _shards[i].Clear();
            _shardStats[$"shard_{i}"] = new();
        }
    }

    private ConcurrentDictionary<string, ShardBlock> GetBucket(ShardKey key)
        => _shards[key.BucketIndex];

    private void IncrWrite(ShardKey key)
    {
        var name = $"shard_{key.BucketIndex}";
        _shardStats.AddOrUpdate(name,
            _ => new ShardStats { WriteCount = 1 },
            (_, stats) => { Interlocked.Increment(ref stats.WriteCount); return stats; });
    }

    private void IncrCas(ShardKey key)
    {
        var name = $"shard_{key.BucketIndex}";
        _shardStats.AddOrUpdate(name,
            _ => new ShardStats { CasConflicts = 1 },
            (_, stats) => { Interlocked.Increment(ref stats.CasConflicts); return stats; });
    }

    private void IncrRead(ShardKey key)
    {
        var name = $"shard_{key.BucketIndex}";
        _shardStats.AddOrUpdate(name,
            _ => new ShardStats { ReadHits = 1 },
            (_, stats) => { Interlocked.Increment(ref stats.ReadHits); return stats; });
    }

    private void IncrCrossShard()
    {
        _shardStats.AddOrUpdate("cross",
            _ => new ShardStats { CrossShardMerges = 1 },
            (_, stats) => { Interlocked.Increment(ref stats.CrossShardMerges); return stats; });
    }

    private static double ComputeRelevance(ShardBlock block, string query)
    {
        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(block.Content))
            return 0;

        var bl = block.Content.ToLower();
        var ql = query.ToLower();

        if (bl.Contains(ql))
            return 0.8 + Math.Min(0.2, ql.Length / (double)Math.Max(1, bl.Length));

        var queryWords = ql.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (queryWords.Length == 0) return 0;

        var matchCount = queryWords.Count(w => bl.Contains(w));
        return matchCount / (double)queryWords.Length * 0.5;
    }
}
