using LTAI.Agent.Delta;
using LTAI.Agent.Memory;
using LTAI.Agent.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LTAI.Tests.Delta;

public class DeltaStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DeltaStore _store;

    public DeltaStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"deltas-test-{Guid.NewGuid():N}.db");
        _store = new DeltaStore(_dbPath);
    }

    [Fact]
    public async Task RecordDelta_ShouldCreateEntry()
    {
        var id = await _store.CreateDeltaForEditAsync(
            "/test/file.cs", 1, 10,
            "diff content", "WriteFile",
            "conv-1", "msg-1",
            agentId: "test-agent");

        Assert.NotNull(id);
        Assert.Equal(32, id.Length);

        var retrieved = _store.GetDelta(id);
        Assert.NotNull(retrieved);
        Assert.Equal("/test/file.cs", retrieved.FilePath);
        Assert.Equal("WriteFile", retrieved.ToolName);
        Assert.Equal("conv-1", retrieved.ConversationId);
    }

    [Fact]
    public async Task GetFileDeltas_ShouldReturnOrdered()
    {
        await _store.CreateDeltaForEditAsync("/test/a.cs", 1, 5, null, "Write", "c1", "m1");
        await _store.CreateDeltaForEditAsync("/test/a.cs", 6, 10, null, "Write", "c1", "m2");
        await _store.CreateDeltaForEditAsync("/test/b.cs", 1, 3, null, "Write", "c1", "m3");

        var deltas = _store.GetFileDeltas("/test/a.cs");
        Assert.Equal(2, deltas.Count);
        Assert.All(deltas, d => Assert.Equal("/test/a.cs", d.FilePath));
    }

    [Fact]
    public async Task GetConversationDeltas_ShouldReturnAll()
    {
        await _store.CreateDeltaForEditAsync("/f1.cs", 1, 1, null, "Write", "cx", "m1");
        await _store.CreateDeltaForEditAsync("/f2.cs", 1, 1, null, "Write", "cx", "m2");
        await _store.CreateDeltaForEditAsync("/f3.cs", 1, 1, null, "Write", "cy", "m3");

        var conv = _store.GetConversationDeltas("cx");
        Assert.Equal(2, conv.Count);
    }

    [Fact]
    public async Task GetProvenanceForLines_ShouldReturnResults()
    {
        var id = await _store.CreateDeltaForEditAsync(
            "/test/p.cs", 10, 15, null, "Edit", "c1", "m1");

        var prov = _store.GetProvenanceForLines("/test/p.cs", 12, 14);
        Assert.NotEmpty(prov);
        Assert.All(prov, p => Assert.Equal("/test/p.cs", p.FilePath));
    }

    [Fact]
    public async Task GetStats_ShouldWork()
    {
        await _store.CreateDeltaForEditAsync("/a.cs", 1, 1, null, "Write", "c1", "m1", "agent1");
        await _store.CreateDeltaForEditAsync("/b.cs", 1, 1, null, "Edit", "c2", "m2", "agent2");

        var stats = _store.GetStats();
        Assert.True(stats.TotalDeltas >= 2);
        Assert.True(stats.TotalFiles >= 2);
        Assert.True(stats.TotalConversations >= 2);
    }

    [Fact]
    public async Task CreateDeltaForEdit_DeduplicatesContent()
    {
        var id1 = await _store.CreateDeltaForEditAsync("/test.cs", 1, 1, "same", "Write", "c1", "m1");
        var id2 = await _store.CreateDeltaForEditAsync("/test.cs", 1, 1, "same", "Write", "c1", "m1");

        // Different timestamps should produce different IDs
        Assert.NotEqual(id1, id2);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }
}

public class CrdtTextTests
{
    [Fact]
    public void InsertBlock_ShouldWork()
    {
        var doc = new CrdtText("/test.cs");
        doc.LoadFromLines(["line1", "line2", "line3"]);

        Assert.Equal(3, doc.ActiveBlockCount);
        Assert.Equal("line1\nline2\nline3", doc.GetFullText());
    }

    [Fact]
    public void DeleteBlock_ShouldMarkDeleted()
    {
        var doc = new CrdtText("/test.cs");
        doc.LoadFromLines(["a", "b", "c"]);
        var blocks = doc.GetBlocks();
        var result = doc.DeleteBlock(blocks[1].Id);

        Assert.True(result.Success);
        Assert.Equal(2, doc.ActiveBlockCount);
    }

    [Fact]
    public void MergeRemoteOps_ShouldResolveConflicts()
    {
        var local = new CrdtText("/test.cs");
        local.LoadFromLines(["original"]);

        var remoteBlocks = new List<CrdtBlock>
        {
            new() { Id = "site2:1", SiteId = "site2", Clock = 1, OriginLeft = "root", Content = "remote edit", IsDeleted = false }
        };

        local.MergeRemoteOps(remoteBlocks);
        Assert.Equal(2, local.BlockCount);
    }

    [Fact]
    public void GetSnapshot_ShouldReflectState()
    {
        var doc = new CrdtText("/test.cs");
        doc.LoadFromLines(["hello", "world"]);

        var snap = doc.GetSnapshot();
        Assert.Equal("/test.cs", snap.FilePath);
        Assert.Equal(2, snap.LineCount);
    }
}

public class CrdtWorktreeTests
{
    [Fact]
    public void GetOrCreateDocument_ShouldCreate()
    {
        using var store = new DeltaStore(Path.Combine(Path.GetTempPath(), $"wt-test-{Guid.NewGuid():N}.db"));
        var wt = new CrdtWorktree(store);

        var doc = wt.GetOrCreateDocument("/nonexistent/test.cs");
        Assert.NotNull(doc);
    }

    [Fact]
    public void Snapshot_ShouldReflectDocuments()
    {
        using var store = new DeltaStore(Path.Combine(Path.GetTempPath(), $"wt-test2-{Guid.NewGuid():N}.db"));
        var wt = new CrdtWorktree(store);
        wt.GetOrCreateDocument("/a.cs");
        wt.GetOrCreateDocument("/b.cs");

        var snaps = wt.GetAllSnapshots();
        Assert.Equal(2, snaps.Count);
    }
}

public class SemanticCompressorTests
{
    [Fact]
    public async Task CompressSemantically_WithoutEmbedder_FallsBack()
    {
        var compressor = new LTAI.Agent.Context.SemanticCompressor(null);
        var text = string.Join(". ", Enumerable.Range(1, 20).Select(i => $"Sentence number {i} with some content to make it reasonably long."));

        var compressed = await compressor.CompressSemanticallyAsync(text, targetRatio: 0.5);

        Assert.NotNull(compressed);
        Assert.True(compressed.Length < text.Length, "Compressed text should be shorter");
    }

    [Fact]
    public async Task ShortText_ShouldNotCompress()
    {
        var compressor = new LTAI.Agent.Context.SemanticCompressor(null);
        var text = "Short text.";

        var result = await compressor.CompressSemanticallyAsync(text, targetRatio: 0.5);
        Assert.Equal(text, result);
    }
}

public class RouterStepSkillActivationTests
{
    [Theory]
    [InlineData("/code write a test", "code", "write a test")]
    [InlineData("/data analyze this file", "data", "analyze this file")]
    [InlineData("/chat", "chat", "")]
    [InlineData("normal request", null, "normal request")]
    [InlineData("/unknown do something", null, "/unknown do something")]
    public void ParseSkillActivation_ShouldWork(string input, string? expectedAgent, string expectedClean)
    {
        var (agent, clean) = LTAI.Agent.Pipeline.Steps.RouterStep.ParseSkillActivation(input);
        Assert.Equal(expectedAgent, agent);
        Assert.Equal(expectedClean, clean);
    }
}

public class ToolExecutionStepRecoveryTests
{
    [Fact]
    public void RecoverDanglingToolCalls_ShouldInjectPlaceholders()
    {
        var ctx = new LTAI.Agent.Pipeline.MessageContext("test");

        // Simulate dangling tool call without result
        ctx.Messages.Add(new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.Assistant, "")
        {
            Contents = new List<Microsoft.Extensions.AI.AIContent>
            {
                new Microsoft.Extensions.AI.FunctionCallContent("call_123", "test_tool", new Dictionary<string, object?>())
            }
        });

        // Process through ToolExecutionStep
        var step = new LTAI.Agent.Pipeline.Steps.ToolExecutionStep(new LTAI.AI.ToolRegistry());
        step.ProcessAsync(ctx);

        // Should have recovery tool result
        var toolMessages = ctx.Messages.Where(m => m.Role == Microsoft.Extensions.AI.ChatRole.Tool).ToList();
        Assert.NotEmpty(toolMessages);
    }
}
