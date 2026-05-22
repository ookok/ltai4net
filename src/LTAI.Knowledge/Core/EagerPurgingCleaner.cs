using System.Collections.Concurrent;
using LTAI.Knowledge.Core.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.Knowledge.Core;

public enum BlockTier { Hot, Warm, Cold }

public sealed class EagerPurgeBlock
{
    public string Id { get; set; } = "";
    public string Content { get; set; } = "";
    public BlockTier Tier { get; set; } = BlockTier.Warm;
    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public long LastAccess { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public int AccessCount { get; set; }
    public long ByteSize { get; set; }
    public double RelevanceScore { get; set; } = 0.5;
}

public sealed class PurgeStats
{
    public int HotCount { get; set; }
    public int WarmCount { get; set; }
    public int ColdCount { get; set; }
    public int PurgedThisCycle { get; set; }
    public int TotalPurged { get; set; }
    public long BytesPurgedThisCycle { get; set; }
    public long TotalBytesPurged { get; set; }
    public DateTimeOffset LastPurgeTime { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class EagerPurgingCleaner
{
    private readonly ConcurrentDictionary<string, EagerPurgeBlock> _blocks = new();
    private readonly ILogger<EagerPurgingCleaner>? _logger;

    private const long HotThresholdSec = 3600;
    private const long WarmThresholdSec = 86400;
    private const int PageBatchSize = 64;
    private const int MaxTotalBlocks = 100_000;
    private const int ColdAccessThreshold = 2;
    private const long PurgeIntervalMs = 300_000;
    private const double LowRelevanceThreshold = 0.1;

    private long _lastPurgeMs;

    public PurgeStats Stats { get; } = new();
    public event Action<List<EagerPurgeBlock>>? OnPurged;

    public EagerPurgingCleaner(ILogger<EagerPurgingCleaner>? logger = null)
    {
        _logger = logger;
        _lastPurgeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    public void RegisterBlock(string id, string content, double relevanceScore = 0.5)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        _blocks.AddOrUpdate(id,
            _ => new EagerPurgeBlock
            {
                Id = id, Content = content, Tier = BlockTier.Hot,
                Timestamp = now, LastAccess = now, AccessCount = 1,
                ByteSize = content.Length * 2, RelevanceScore = relevanceScore
            },
            (_, existing) =>
            {
                var tier = ClassifyTier(existing, now);
                existing.Tier = tier;
                existing.LastAccess = now;
                existing.AccessCount++;
                existing.Content = content;
                existing.ByteSize = content.Length * 2;
                existing.RelevanceScore = relevanceScore;
                return existing;
            });
    }

    public EagerPurgeBlock? AccessBlock(string id)
    {
        if (!_blocks.TryGetValue(id, out var block))
            return null;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        block.LastAccess = now;
        block.AccessCount++;
        block.Tier = ClassifyTier(block, now);
        return block;
    }

    public int PurgeColdBlocks()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var blocksToPurge = new List<EagerPurgeBlock>();

        foreach (var kv in _blocks)
        {
            var block = kv.Value;
            var tier = ClassifyTier(block, now);

            var shouldPurge = tier == BlockTier.Cold
                && block.AccessCount < ColdAccessThreshold
                && (now - block.LastAccess) > WarmThresholdSec;

            if (!shouldPurge) continue;

            block.Tier = BlockTier.Cold;
            blocksToPurge.Add(block);
        }

        var purgedCount = EagerPurgePages(blocksToPurge);
        Stats.PurgedThisCycle = purgedCount;
        Stats.TotalPurged += purgedCount;
        Stats.LastPurgeTime = DateTimeOffset.UtcNow;

        _logger?.LogInformation(
            "EagerPurging: purged={Purged} blocks={Hot}/{Warm}/{Cold} bytes={Bytes}",
            purgedCount, Stats.HotCount, Stats.WarmCount, Stats.ColdCount,
            Stats.BytesPurgedThisCycle);

        return purgedCount;
    }

    public int PurgeLowRelevance()
    {
        var blocksToPurge = _blocks.Values
            .Where(b => b.RelevanceScore < LowRelevanceThreshold && b.AccessCount < 3)
            .Take(PageBatchSize * 4)
            .ToList();

        return EagerPurgePages(blocksToPurge);
    }

    public bool TryAutoPurge()
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (nowMs - _lastPurgeMs < PurgeIntervalMs)
            return false;

        _lastPurgeMs = nowMs;
        var totalBlocks = _blocks.Count;

        if (totalBlocks > MaxTotalBlocks)
        {
            var overflow = totalBlocks - MaxTotalBlocks;
            var toPurge = _blocks.Values
                .OrderBy(b => b.RelevanceScore)
                .ThenBy(b => b.LastAccess)
                .Take(overflow)
                .ToList();
            EagerPurgePages(toPurge);
            _logger?.LogWarning("EagerPurging: overflow purge {Count} blocks above limit", overflow);
        }

        var purged = PurgeColdBlocks();
        if (purged == 0 && totalBlocks > PageBatchSize * 2)
            PurgeLowRelevance();

        RecalculateStats();
        return purged > 0;
    }

    public List<EagerPurgeBlock> QueryBlocks(
        Func<EagerPurgeBlock, bool>? filter = null,
        int topK = 20)
    {
        var query = filter != null
            ? _blocks.Values.Where(filter)
            : _blocks.Values;

        return query
            .OrderByDescending(b => b.RelevanceScore)
            .ThenByDescending(b => b.AccessCount)
            .Take(topK)
            .ToList();
    }

    public Dictionary<string, object> GetStats()
    {
        RecalculateStats();
        return new()
        {
            ["hot"] = Stats.HotCount,
            ["warm"] = Stats.WarmCount,
            ["cold"] = Stats.ColdCount,
            ["total_purged"] = Stats.TotalPurged,
            ["bytes_purged"] = Stats.TotalBytesPurged,
            ["last_purge"] = Stats.LastPurgeTime.ToString("o"),
            ["purge_interval_sec"] = PurgeIntervalMs / 1000.0,
            ["total_blocks"] = _blocks.Count
        };
    }

    public void Clear()
    {
        var all = _blocks.Values.ToList();
        _blocks.Clear();
        OnPurged?.Invoke(all);
    }

    private int EagerPurgePages(List<EagerPurgeBlock> blocksToPurge)
    {
        var purged = 0;
        var bytesPurged = 0L;

        foreach (var batch in blocksToPurge.Chunk(PageBatchSize))
        {
            foreach (var block in batch)
            {
                if (_blocks.TryRemove(block.Id, out _))
                {
                    purged++;
                    bytesPurged += block.ByteSize;
                }
            }
        }

        Stats.BytesPurgedThisCycle = bytesPurged;
        Stats.TotalBytesPurged += bytesPurged;

        if (blocksToPurge.Count > 0)
            OnPurged?.Invoke(blocksToPurge);

        return purged;
    }

    private static BlockTier ClassifyTier(EagerPurgeBlock block, long now)
    {
        var age = now - block.Timestamp;
        if (age < HotThresholdSec && block.AccessCount >= 1)
            return BlockTier.Hot;
        if (age < WarmThresholdSec)
            return BlockTier.Warm;
        return BlockTier.Cold;
    }

    private void RecalculateStats()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var h = 0; var w = 0; var c = 0;

        foreach (var block in _blocks.Values)
        {
            var tier = ClassifyTier(block, now);
            block.Tier = tier;
            switch (tier)
            {
                case BlockTier.Hot: h++; break;
                case BlockTier.Warm: w++; break;
                case BlockTier.Cold: c++; break;
            }
        }

        Stats.HotCount = h;
        Stats.WarmCount = w;
        Stats.ColdCount = c;
    }
}
