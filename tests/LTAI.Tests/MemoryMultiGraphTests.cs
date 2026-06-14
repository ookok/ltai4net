using LTAI.Agent.Memory;
using Xunit;

namespace LTAI.Tests;

public sealed class MultiGraphStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly MultiGraphStore _store;

    public MultiGraphStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ltai-mg-ut-{Guid.NewGuid():N}.db");
        _store = new MultiGraphStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public void StoreAndGetNode_Roundtrip()
    {
        _store.StoreNode("n1", "session-1", "Hello world");
        var node = _store.GetNode("n1");
        Assert.NotNull(node);
        Assert.Equal("n1", node.Value.Id);
        Assert.Equal("Hello world", node.Value.Content);
    }

    [Fact]
    public void StoreNode_WithEmbedding_StoresBlob()
    {
        _store.StoreNode("n2", "session-1", "content with emb", [0x01, 0x02, 0x03, 0x04]);
        Assert.NotNull(_store.GetNode("n2"));
    }

    [Fact]
    public void GetNode_NonExistent_ReturnsNull()
    {
        Assert.Null(_store.GetNode("nonexistent"));
    }

    [Fact]
    public void SearchContent_FindsKeyword()
    {
        _store.StoreNode("n3", "s1", "This is about authentication");
        _store.StoreNode("n4", "s1", "This is about authorization");
        var results = _store.SearchContent("authentication");
        Assert.Contains("n3", results);
        Assert.DoesNotContain("n4", results);
    }

    [Fact]
    public void TemporalGraph_Append_CreatesChain()
    {
        _store.Temporal.Append("a", 1);
        _store.Temporal.Append("b", 2);
        _store.Temporal.Append("c", 3);
        var chain = _store.Temporal.GetChain(10);
        Assert.NotEmpty(chain);
        Assert.Contains("a", chain);
    }

    [Fact]
    public void TemporalGraph_GetChain_ReturnsOrderedResults()
    {
        _store.Temporal.Append("x", 1);
        _store.Temporal.Append("y", 2);
        var chain = _store.Temporal.GetChain(5);
        Assert.NotEmpty(chain);
    }

    [Fact]
    public void CausalGraph_AddAndGetEdges()
    {
        _store.Causal.AddEdge("cause1", "effect1", 0.85);
        _store.Causal.AddEdge("cause2", "effect1", 0.72);
        var causes = _store.Causal.GetCauses("effect1");
        Assert.Contains(causes, c => c.NodeId == "cause1");
        Assert.Contains(causes, c => c.NodeId == "cause2");
    }

    [Fact]
    public void CausalGraph_BelowThreshold_Ignored()
    {
        _store.Causal.AddEdge("low", "effect2", 0.3);
        Assert.DoesNotContain(_store.Causal.GetCauses("effect2"), c => c.NodeId == "low");
    }

    [Fact]
    public void CausalGraph_GetEffects()
    {
        _store.Causal.AddEdge("source", "effect_a", 0.9, "causes");
        _store.Causal.AddEdge("source", "effect_b", 0.8, "causes");
        var effects = _store.Causal.GetEffects("source");
        Assert.Equal(2, effects.Count);
        Assert.Contains(effects, e => e.NodeId == "effect_a");
        Assert.Contains(effects, e => e.NodeId == "effect_b");
    }

    [Fact]
    public void CausalGraph_GetCauses_IncludesLabel()
    {
        _store.Causal.AddEdge("cause1", "effect1", 0.9, "because of X");
        var causes = _store.Causal.GetCauses("effect1");
        var cause = Assert.Single(causes, c => c.NodeId == "cause1");
        Assert.Equal("because of X", cause.Label);
    }

    [Fact]
    public void IntentRouter_Classify_ReturnsCorrectIntent()
    {
        var router = new IntentRouter();
        Assert.Equal(QueryIntent.Why, router.Classify("为什么天是蓝色的"));
        Assert.Equal(QueryIntent.When, router.Classify("什么时候开始"));
        Assert.Equal(QueryIntent.Who, router.Classify("谁负责这个"));
        Assert.Equal(QueryIntent.What, router.Classify("这个函数是做什么的"));
        Assert.Equal(QueryIntent.What, router.Classify(""));
        Assert.Equal(QueryIntent.When, router.Classify("when did this happen"));
        Assert.Equal(QueryIntent.Where, router.Classify("在哪里下载"));
        Assert.Equal(QueryIntent.How, router.Classify("怎么配置"));
    }

    [Fact]
    public void IntentRouter_Classify_CaseInsensitive()
    {
        var router = new IntentRouter();
        Assert.Equal(QueryIntent.Why, router.Classify("WHY is this"));
        Assert.Equal(QueryIntent.Who, router.Classify("WHO did that"));
    }

    [Fact]
    public void AdaptiveBeamTraverser_MockStore()
    {
        using var store = new MultiGraphStore(Path.Combine(Path.GetTempPath(), $"ltai-abt-{Guid.NewGuid():N}.db"));
        store.StoreNode("root", "s1", "root node");
        store.StoreNode("a", "s1", "child a");
        store.StoreNode("b", "s1", "child b");
        store.Semantic.AddEdge("root", "a", 0.8);
        store.Semantic.AddEdge("root", "b", 0.6);

        var traverser = new AdaptiveBeamTraverser(store);
        var results = traverser.Traverse("root", QueryIntent.What, topK: 5);
        Assert.NotEmpty(results);
    }

    [Fact]
    public void ConsolidationQueue_DequeueAndMark()
    {
        _store.StoreNode("cq1", "s1", "queue test");
        var batch = _store.DequeueConsolidationBatch(10);
        Assert.NotEmpty(batch);
        Assert.Contains("cq1", batch);
        _store.MarkConsolidated("cq1");
        Assert.DoesNotContain("cq1", _store.DequeueConsolidationBatch(10));
    }
}
