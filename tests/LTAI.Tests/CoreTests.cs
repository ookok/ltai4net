using Xunit;
using LTAI.Agent.Tools;
using LTAI.Agent.Vector;
using LTAI.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Tests;

public class KgStoreTests : IDisposable
{
    private readonly string _dir;
    public KgStoreTests() { _dir = Path.Combine(Path.GetTempPath(), "ltai-test-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [Fact]
    public async Task CreateStore_NodeUpsertAndGet_Works()
    {
        using var store = new KgStore(Path.Combine(_dir, "test.db"));
        var id = await store.UpsertNode(extId: "test:1", kind: "class", name: "TestClass",
            ns: "LTAI.Tests", signature: "", source: "test.cs");
        Assert.True(id > 0);

        var node = await store.GetNode(id);
        Assert.NotNull(node);
        Assert.Equal("class", node.Kind);
        Assert.Equal("TestClass", node.Name);
    }

    [Fact]
    public async Task FtsSearch_BasicQuery_ReturnsResults()
    {
        using var store = new KgStore(Path.Combine(_dir, "fts.db"));
        var id = await store.UpsertNode("doc:1", "document", "TestDoc");
        await store.AddDoc(id, "This is a test document about authentication");
        // Use the writer connection directly to ensure visibility
        await store.OptimizeFtsAsync();

        var results = await store.SearchFts("authentication");
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.nodeId == id);
    }

    [Fact]
    public async Task EdgeTraversal_Bfs_ReturnsNeighbors()
    {
        using var store = new KgStore(Path.Combine(_dir, "graph.db"));
        var a = await store.UpsertNode("n:a", "class", "ClassA");
        var b = await store.UpsertNode("n:b", "method", "MethodB");
        await store.AddEdge(a, b, "calls");

        var neighbors = await store.TraverseBfs([a], maxDepth: 1);
        Assert.Contains(neighbors, n => n.Id == b);
    }
}

public class SecretManagerTests
{
    [Fact]
    public void SetAndGet_EnvironmentVariable_Works()
    {
        var key = "LTAI_TEST_KEY_" + Guid.NewGuid().ToString("N")[..8];
        SecretManager.Set(key, "test-value", persistent: false);
        Assert.Equal("test-value", SecretManager.Get(key));
        SecretManager.Invalidate(key);
    }

    [Fact]
    public void Has_ReturnsCorrectValue()
    {
        var key = "LTAI_TEST_HAS_" + Guid.NewGuid().ToString("N")[..8];
        Assert.False(SecretManager.Has(key));
        SecretManager.Set(key, "value", persistent: false);
        Assert.True(SecretManager.Has(key));
        SecretManager.Invalidate(key);
    }
}

public class UsageTrackerTests
{
    [Fact]
    public void Record_IncreasesCounters()
    {
        var before = UsageTracker.TotalTokens;
        UsageTracker.Record(100, 50, "deepseek-v4-flash");
        Assert.True(UsageTracker.TotalTokens > before);
        Assert.Equal("deepseek-v4-flash", UsageTracker.ActiveModel);
    }

    [Fact]
    public void CacheTracking_Works()
    {
        var hitsBefore = UsageTracker.CacheHits;
        var missesBefore = UsageTracker.CacheMisses;
        UsageTracker.RecordCacheHit();
        UsageTracker.RecordCacheMiss();
        Assert.Equal(hitsBefore + 1, UsageTracker.CacheHits);
        Assert.Equal(missesBefore + 1, UsageTracker.CacheMisses);
    }
}
// ═══════════════════════════════════════════════════════════════
//  KgStore — Edge Case Tests (C4)
// ═══════════════════════════════════════════════════════════════

public class KgStoreEdgeCaseTests : IDisposable
{
    private readonly string _dir;
    public KgStoreEdgeCaseTests() { _dir = Path.Combine(Path.GetTempPath(), "ltai-edge-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [Fact]
    public async Task UpsertNode_UpdateExisting_KeepsSameId()
    {
        using var store = new KgStore(Path.Combine(_dir, "u.db"));
        var id1 = await store.UpsertNode("x:1", "class", "A");
        var id2 = await store.UpsertNode("x:1", "method", "B");
        Assert.Equal(id1, id2);
        Assert.Equal("method", (await store.GetNode(id1))!.Kind);
    }

    [Fact]
    public async Task DeleteNode_CascadeRemovesEdges()
    {
        using var store = new KgStore(Path.Combine(_dir, "d.db"));
        var a = await store.UpsertNode("d:a", "class", "A");
        var b = await store.UpsertNode("d:b", "class", "B");
        await store.AddEdge(a, b, "calls");
        await store.DeleteNode(a);
        Assert.Empty(await store.GetEdges(nodeId: a));
    }

    [Fact]
    public async Task FtsSearch_EmptyQuery_ReturnsEmpty()
    {
        using var store = new KgStore(Path.Combine(_dir, "e.db"));
        try { var r = await store.SearchFts(""); Assert.Empty(r); } catch (Microsoft.Data.Sqlite.SqliteException) { }
    }

    [Fact]
    public async Task FtsSearch_LongText_Works()
    {
        using var store = new KgStore(Path.Combine(_dir, "l.db"));
        var id = await store.UpsertNode("doc:l", "document", "Long");
        await store.AddDoc(id, string.Join(" ", Enumerable.Repeat("auth authz acct", 1000)));
        await store.OptimizeFtsAsync();
        Assert.NotEmpty(await store.SearchFts("auth"));
    }

    [Fact]
    public async Task TraverseBfs_NoEdges_ReturnsStartNode()
    {
        using var store = new KgStore(Path.Combine(_dir, "ne.db"));
        var a = await store.UpsertNode("n:a", "class", "A");
        Assert.Single(await store.TraverseBfs([a]));
    }

    [Fact]
    public async Task Meta_SetAndGet_Works()
    {
        using var store = new KgStore(Path.Combine(_dir, "m.db"));
        await store.SetMeta("ver", "1.0");
        Assert.Equal("1.0", await store.GetMeta("ver"));
    }

    [Fact]
    public async Task ConcurrentWrites_NoDeadlock()
    {
        using var store = new KgStore(Path.Combine(_dir, "cw.db"));
        var tasks = Enumerable.Range(0, 5).Select(i =>
            store.UpsertNode($"n:{i}", "class", $"Node{i}"));
        var ids = await Task.WhenAll(tasks);
        Assert.Equal(5, ids.Distinct().Count());
    }

    [Fact]
    public async Task GetNodeByExtId_NotFound_ReturnsNull()
    {
        using var store = new KgStore(Path.Combine(_dir, "nf.db"));
        Assert.Null(await store.GetNodeByExtId("nonexistent"));
    }

    [Fact]
    public async Task GetNodesByKind_NoMatches_ReturnsEmpty()
    {
        using var store = new KgStore(Path.Combine(_dir, "nk.db"));
        Assert.Empty(await store.GetNodesByKind("nonexistent"));
    }
}

// ═══════════════════════════════════════════════════════════════
//  SafeShellTool — Security Tests (C1)
// ═══════════════════════════════════════════════════════════════

public class SafeShellToolTests
{
    [Fact]
    public async Task RunCommand_Dangerous_ReturnsError()
    {
        var tool = new SafeShellTool(Directory.GetCurrentDirectory());
        var result = await tool.RunCommand("rm -rf /");
        Assert.Contains("危险", result);
    }

    [Fact]
    public async Task RunCommand_SimpleCommand_Runs()
    {
        var tool = new SafeShellTool(Directory.GetCurrentDirectory());
        var result = await tool.RunCommand("echo hello");
        Assert.Contains("hello", result);
    }

    [Fact]
    public async Task RunCommand_Safe_Runs()
    {
        var tool = new SafeShellTool(Directory.GetCurrentDirectory());
        var result = await tool.RunCommand("echo hello world");
        Assert.Contains("hello world", result);
    }
    [Fact]
    public async Task RunCommand_Sudo_Blocked()
    {
        var t = new SafeShellTool(Directory.GetCurrentDirectory());
        Assert.Contains("阻止", await t.RunCommand("sudo rm -rf /"));
    }

    [Fact]
    public async Task RunCommand_TokenLevel_Safe_Allowed()
    {
        var t = new SafeShellTool(Directory.GetCurrentDirectory());
        var r = await t.RunCommand("dotnet --version");
        Assert.False(string.IsNullOrWhiteSpace(r), r);
    }

    [Fact]
    public async Task RunCommand_RmWithArgs_SafeFile_Allowed()
    {
        var t = new SafeShellTool(Directory.GetCurrentDirectory());
        var r = await t.RunCommand("rm temp.txt");
        // rm without -rf and specific file should be allowed
        Assert.DoesNotContain("阻止", r);
    }

}

// ═══════════════════════════════════════════════════════════════
//  MultiProviderChatClient — Degradation Tests (C2)
// ═══════════════════════════════════════════════════════════════
// COMMENTED OUT: requires FakeChatClient from Microsoft.Extensions.AI.Testing
// which is not currently referenced. Restore when testing package is added.
/*
public class MultiProviderChatClientTests
{
    [Fact]
    public void Register_Provider_CanBeResolved()
    {
        var router = new LTAI.AI.MultiProviderChatClient(new LTAIOptions());
        router.Register("test", new FakeChatClient());
        Assert.Contains("test", router.RegisteredProviders);
    }

    [Fact]
    public async Task GetResponseAsync_NoProviders_ReturnsFailure()
    {
        var router = new LTAI.AI.MultiProviderChatClient(new LTAIOptions());
        var resp = await router.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        var text = resp.Messages?.LastOrDefault()?.Text ?? "";
        Assert.Contains("failed", text);
    }

    [Fact]
    public async Task GetResponseAsync_WithProvider_Succeeds()
    {
        var router = new LTAI.AI.MultiProviderChatClient(new LTAIOptions());
        router.Register("deepseek", new FakeChatClient().AddRoute("hi", _ => "hello back"));
        var resp = await router.GetResponseAsync([new ChatMessage(ChatRole.User, "say hi")]);
        var text = resp.Messages?.LastOrDefault()?.Text ?? "";
        Assert.Contains("hello back", text);
    }
}
*/


// ═══════════════════════════════════════════════════════════════
//  CgGraph — 代码图谱
// ═══════════════════════════════════════════════════════════════

public class CgGraphTests : IDisposable
{
    private readonly string _dir;
    public CgGraphTests() { _dir = Path.Combine(Path.GetTempPath(), "cg-test-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [Fact]
    public void Constructor_NullStore_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new CgGraph(null!));
    }

    [Fact]
    public async Task BuildAsync_InvalidDirectory_ReturnsError()
    {
        using var store = new KgStore(Path.Combine(_dir, "cg.db"));
        var graph = new CgGraph(store, logger: NullLogger<CgGraph>.Instance, ws: _dir);
        var result = await graph.BuildAsync("nonexistent_path_xyz");
        Assert.StartsWith("Directory not found", result);
    }

    [Fact]
    public async Task QueryAsync_NotBuilt_ReturnsMessage()
    {
        using var store = new KgStore(Path.Combine(_dir, "cg2.db"));
        var graph = new CgGraph(store, logger: NullLogger<CgGraph>.Instance, ws: _dir);
        var result = await graph.QueryAsync("test query");
        Assert.Contains("not built", result);
    }
}

// ═══════════════════════════════════════════════════════════════
//  Reranker — 重排序
// ═══════════════════════════════════════════════════════════════

public class RerankerTests
{
    [Fact]
    public void RankedResult_BlendedScore_IsWeighted()
    {
        var node = new NodeRow { Id = 1, Kind = "method", Name = "Foo" };
        var result = new RankedResult(node, 0.5f, 0.8f, 1);
        // BlendedScore = 0.5 * 0.3 + 0.8 * 0.7 = 0.15 + 0.56 = 0.71
        Assert.Equal(0.71f, result.BlendedScore, precision: 4);
    }

    [Fact]
    public void RankedResult_DefaultOrder_Works()
    {
        var node = new NodeRow { Id = 1, Kind = "method", Name = "Foo" };
        var result = new RankedResult(node, 1f, 1f, 0);
        Assert.NotNull(result.Node);
        Assert.Equal("method", result.Node.Kind);
    }
}


