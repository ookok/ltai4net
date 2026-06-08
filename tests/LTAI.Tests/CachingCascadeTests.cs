// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  CachingCascadeTests — three-tier memory caching tests
// ═══════════════════════════════════════════════════════════════

using Xunit;
using LTAI.Agent.Caching;

namespace LTAI.Tests;

public class CachingCascadeTier1Memory
{
    [Fact]
    public async Task StoreAndLookup_Roundtrip()
    {
        using var store = new MemoryCachingStore(maxEntries: 16);
        var data = "hello"u8.ToArray();
        await store.StoreAsync("k1", data, 100);
        var result = await store.LookupAsync("k1");
        Assert.NotNull(result);
        Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(result));
    }

    [Fact]
    public async Task Lookup_MissingKey_ReturnsNull()
    {
        using var store = new MemoryCachingStore(maxEntries: 16);
        Assert.Null(await store.LookupAsync("nonexistent"));
    }

    [Fact]
    public async Task Eviction_LruEvictsOldest()
    {
        using var store = new MemoryCachingStore(maxEntries: 3);
        await store.StoreAsync("k1", "a"u8.ToArray(), 10);
        await store.StoreAsync("k2", "b"u8.ToArray(), 20);
        await store.StoreAsync("k3", "c"u8.ToArray(), 30);
        // Evicts k1
        await store.StoreAsync("k4", "d"u8.ToArray(), 40);

        Assert.Null(await store.LookupAsync("k1"));
        Assert.NotNull(await store.LookupAsync("k2"));
        Assert.NotNull(await store.LookupAsync("k3"));
        Assert.NotNull(await store.LookupAsync("k4"));
    }

    [Fact]
    public async Task FindNearestAsync_ReturnsClosest()
    {
        using var store = new MemoryCachingStore(maxEntries: 16);
        await store.StoreAsync("session:s1:pos:100", "a"u8.ToArray(), 100);
        await store.StoreAsync("session:s1:pos:200", "b"u8.ToArray(), 200);
        await store.StoreAsync("session:s1:pos:300", "c"u8.ToArray(), 300);

        var nearest = await store.FindNearestAsync("s1", 250);
        Assert.NotNull(nearest);
        Assert.Equal(200, nearest.Value.tokenCount);
    }

    [Fact]
    public async Task FindNearestAsync_MissingSession_ReturnsNull()
    {
        using var store = new MemoryCachingStore(maxEntries: 16);
        Assert.Null(await store.FindNearestAsync("nonexistent", 100));
    }

    [Fact]
    public async Task InvalidateSession_RemovesAll()
    {
        using var store = new MemoryCachingStore(maxEntries: 16);
        await store.StoreAsync("session:s1:pos:100", "a"u8.ToArray(), 100);
        await store.StoreAsync("session:s1:pos:200", "b"u8.ToArray(), 200);
        await store.StoreAsync("session:s2:pos:100", "c"u8.ToArray(), 100);
        await store.InvalidateSessionAsync("s1");
        Assert.Null(await store.LookupAsync("session:s1:pos:100"));
        Assert.Null(await store.LookupAsync("session:s1:pos:200"));
        Assert.NotNull(await store.LookupAsync("session:s2:pos:100"));
    }

    [Fact]
    public async Task Clear_RemovesAll()
    {
        using var store = new MemoryCachingStore(maxEntries: 16);
        await store.StoreAsync("k1", "a"u8.ToArray(), 10);
        await store.ClearAsync();
        Assert.Equal(0, store.CheckpointCount);
    }

    [Fact]
    public async Task FindRangeAsync_ReturnsSorted()
    {
        using var store = new MemoryCachingStore(maxEntries: 16);
        await store.StoreAsync("session:s1:pos:300", "c"u8.ToArray(), 300);
        await store.StoreAsync("session:s1:pos:100", "a"u8.ToArray(), 100);
        await store.StoreAsync("session:s1:pos:200", "b"u8.ToArray(), 200);

        var range = await store.FindRangeAsync("s1", 50, 350);
        Assert.Equal(3, range.Count);
        Assert.Equal(100, range[0].TokenCount);
        Assert.Equal(200, range[1].TokenCount);
        Assert.Equal(300, range[2].TokenCount);
    }
}

public class CachingCascadeTier2File
{
    [Fact]
    public async Task StoreAndLookup_Persists()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ltai-cache-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using var store = new FileCachingStore(dir);
            await store.StoreAsync("k1", "hello"u8.ToArray(), 100);
            var result = await store.LookupAsync("k1");
            Assert.NotNull(result);
            Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(result));
        }
        finally
        {
            if (Directory.Exists(dir)) try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task Clear_RemovesAll()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ltai-cache-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using var store = new FileCachingStore(dir);
            await store.StoreAsync("k1", "a"u8.ToArray(), 10);
            await store.ClearAsync();
            Assert.Equal(0, store.CheckpointCount);
        }
        finally
        {
            if (Directory.Exists(dir)) try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task InvalidateSession_Works()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ltai-cache-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using var store = new FileCachingStore(dir);
            await store.StoreAsync("session:s1:pos:100", "a"u8.ToArray(), 100);
            await store.InvalidateSessionAsync("s1");
            Assert.Null(await store.LookupAsync("session:s1:pos:100"));
        }
        finally
        {
            if (Directory.Exists(dir)) try { Directory.Delete(dir, true); } catch { }
        }
    }
}

public class CachingCascadeFullCascade
{
    [Fact]
    public async Task Cascade_WriteThroughReadThrough()
    {
        using var cascade = new CachingCascade(
            tier1: new MemoryCachingStore(maxEntries: 16),
            tier2: new NullCachingStore(), // No SQLite in unit tests
            tier3: new NullCachingStore());

        var data = "test"u8.ToArray();
        await cascade.StoreAsync("k1", data, 50);

        // Read from tier 1
        var result = await cascade.LookupAsync("k1");
        Assert.NotNull(result);
    }

    [Fact]
    public async Task Cascade_NullTier_Graceful()
    {
        using var cascade = new CachingCascade(
            tier1: new NullCachingStore(),
            tier2: new NullCachingStore(),
            tier3: new NullCachingStore());

        var result = await cascade.LookupAsync("any");
        Assert.Null(result);

        // Should not throw
        await cascade.StoreAsync("k1", "data"u8.ToArray(), 10);
        await cascade.InvalidateSessionAsync("s1");
        await cascade.ClearAsync();
    }

    [Fact]
    public async Task Cascade_FindNearest_FallsBack()
    {
        using var memory = new MemoryCachingStore(maxEntries: 16);
        await memory.StoreAsync("session:s1:pos:50", "a"u8.ToArray(), 50);

        using var cascade = new CachingCascade(
            tier1: memory,
            tier2: new NullCachingStore(),
            tier3: new NullCachingStore());

        var nearest = await cascade.FindNearestAsync("s1", 60);
        Assert.NotNull(nearest);
        Assert.Equal(50, nearest.Value.tokenCount);
    }

    [Fact]
    public async Task Cascade_FindRange_Aggregates()
    {
        using var store = new MemoryCachingStore(maxEntries: 16);
        await store.StoreAsync("session:s1:pos:100", "a"u8.ToArray(), 100);
        await store.StoreAsync("session:s1:pos:200", "b"u8.ToArray(), 200);

        using var cascade = new CachingCascade(
            tier1: store,
            tier2: new NullCachingStore(),
            tier3: new NullCachingStore());

        var range = await cascade.FindRangeAsync("s1", 50, 250);
        Assert.Equal(2, range.Count);
    }
}
