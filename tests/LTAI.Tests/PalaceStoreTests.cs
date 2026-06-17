using LTAI.AI;
using LTAI.Agent.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LTAI.Tests;

[Trait("Category", "Integration")]
public sealed class PalaceStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly EmbeddingClient _embedder;
    private readonly PalaceStore _store;

    public PalaceStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ltai-palace-ut-{Guid.NewGuid():N}.db");
        _embedder = new EmbeddingClient();
        _store = new PalaceStore(_embedder, _dbPath, NullLogger<PalaceStore>.Instance);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + ".hnsw"); } catch { }
    }

    [Fact]
    public async Task GetDrawerById_StoredDrawer_ReturnsDrawer()
    {
        var id = await _store.StoreAsync("test", "room1", "hello world");
        var drawer = _store.GetDrawerById(id);
        Assert.NotNull(drawer);
        Assert.Equal("hello world", drawer!.Content);
        Assert.Equal("test", drawer.Wing);
        Assert.Equal("room1", drawer.Room);
    }

    [Fact]
    public async Task Count_WithEntries_ReturnsCorrectCount()
    {
        Assert.Equal(0, _store.Count());
        await _store.StoreAsync("w1", "r1", "a");
        await _store.StoreAsync("w1", "r2", "b");
        await _store.StoreAsync("w2", "r1", "c");
        Assert.Equal(3, _store.Count());
    }

    [Fact]
    public async Task SearchFts_KeywordMatch_ReturnsResults()
    {
        var id1 = await _store.StoreAsync("w1", "r1", "machine learning pipeline design");
        var id2 = await _store.StoreAsync("w1", "r2", "database schema migration plan");
        await _store.StoreAsync("w2", "r1", "unrelated content here");

        await Task.Delay(50);

        var hits = _store.SearchFts("pipeline", topK: 5);
        Assert.NotEmpty(hits);
        Assert.Contains(hits, h => h.DrawerId == id1);
    }

    [Fact]
    public async Task SearchFts_NoMatch_ReturnsEmpty()
    {
        await _store.StoreAsync("w1", "r1", "hello world");
        await Task.Delay(50);
        var hits = _store.SearchFts("nonexistentxyz", topK: 5);
        Assert.Empty(hits);
    }

    [Fact]
    public async Task DeleteDrawer_RemovesFromCount()
    {
        var id = await _store.StoreAsync("w1", "r1", "to delete");
        Assert.Equal(1, _store.Count());
        _store.DeleteDrawer(id);
        Assert.Equal(0, _store.Count());
    }

    [Fact]
    public async Task ListWings_ReturnsDistinctWings()
    {
        await _store.StoreAsync("wing_a", "r1", "a");
        await _store.StoreAsync("wing_a", "r2", "b");
        await _store.StoreAsync("wing_b", "r1", "c");
        var wings = _store.ListWings();
        Assert.Contains("wing_a", wings);
        Assert.Contains("wing_b", wings);
    }

    [Fact]
    public async Task GetDrawer_ExactMatch_Works()
    {
        await _store.StoreAsync("wing", "room", "content");
        var drawer = _store.GetDrawer("wing", "room", _store.SearchByWingExact("wing")[0].DrawerId);
        Assert.NotNull(drawer);
        Assert.Equal("content", drawer!.Content);
    }

    [Fact]
    public async Task GetRecentDrawers_ReturnsOrderedByCreatedAt()
    {
        await _store.StoreAsync("w", "r", "first");
        await Task.Delay(10);
        await _store.StoreAsync("w", "r", "second");

        var recent = _store.GetRecentDrawers("w", "r", limit: 2);
        Assert.Equal(2, recent.Count);
        Assert.Equal("second", recent[0].Content);
    }

    [Fact]
    public async Task GetImportantDrawers_FiltersByThreshold()
    {
        await _store.StoreAsync("w", "r_high", "important", importance: 0.9);
        await _store.StoreAsync("w", "r_low", "unimportant", importance: 0.1);

        var important = _store.GetImportantDrawers("w", threshold: 0.8);
        Assert.Single(important);
        Assert.Equal("important", important[0].Content);
    }

    [Fact]
    public async Task HybridSearchAsync_ReturnsFusedResults()
    {
        await _store.StoreAsync("wing", "room1", "python async programming patterns");
        await _store.StoreAsync("wing", "room2", "database connection pooling optimization");
        await _store.StoreAsync("wing", "room3", "unrelated meeting notes");

        await Task.Delay(50);
        await _store.WarmupHnswAsync();

        var vec = await _store.GenerateEmbeddingAsync("async programming");
        var results = await _store.HybridSearchAsync(vec, "database", topK: 3);
        Assert.True(results.Count >= 1);
    }

    [Fact]
    public void MaxEntries_DefaultIsNonZero()
    {
        Assert.Equal(PalaceStore.DefaultMaxEntries, _store.MaxEntries);
    }

    [Fact]
    public async Task TrimAsync_ReducesCount()
    {
        for (int i = 0; i < 10; i++)
            await _store.StoreAsync("w", $"r{i}", $"content{i}");

        Assert.Equal(10, _store.Count());
        await _store.TrimAsync(5);
        Assert.True(_store.Count() <= 5);
    }

    [Fact]
    public void GetDrawerById_NonExistent_ReturnsNull()
    {
        var drawer = _store.GetDrawerById("nonexistent");
        Assert.Null(drawer);
    }
}
