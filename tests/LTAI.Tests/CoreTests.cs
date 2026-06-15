using Xunit;
using LTAI.Agent.Vector;
using LTAI.Agent.Tools;
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
        var result = await tool.RunCommand("rm -rf /", confirm: true);
        Assert.Contains("危险", result);
    }

    [Fact]
    public async Task RunCommand_WithoutConfirm_ReturnsWarning()
    {
        var tool = new SafeShellTool(Directory.GetCurrentDirectory());
        var result = await tool.RunCommand("echo hello", confirm: false);
        Assert.Contains("确认", result);
    }

    [Fact]
    public async Task RunCommand_WithConfirmAndSafe_Runs()
    {
        var tool = new SafeShellTool(Directory.GetCurrentDirectory());
        var result = await tool.RunCommand("echo hello world", confirm: true);
        Assert.Contains("hello world", result);
    }
    [Fact]
    public async Task RunCommand_Sudo_Blocked()
    {
        var t = new SafeShellTool(Directory.GetCurrentDirectory());
        Assert.Contains("阻止", await t.RunCommand("sudo rm -rf /", confirm: true));
    }

    [Fact]
    public async Task RunCommand_TokenLevel_Safe_Allowed()
    {
        var t = new SafeShellTool(Directory.GetCurrentDirectory());
        var r = await t.RunCommand("dotnet --version", confirm: true);
        Assert.False(string.IsNullOrWhiteSpace(r), r);
    }

    [Fact]
    public async Task RunCommand_RmWithArgs_SafeFile_Allowed()
    {
        var t = new SafeShellTool(Directory.GetCurrentDirectory());
        var r = await t.RunCommand("rm temp.txt", confirm: true);
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

// ═══════════════════════════════════════════════════════════════
//  DocumentTools — 文档读写测试
// ═══════════════════════════════════════════════════════════════

public class OfficeToolsTests : IDisposable
{
    private readonly string _dir;
    public OfficeToolsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ltai-office-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [Fact]
    public void WordRead_AfterWrite_ReturnsContent()
    {
        var path = Path.Combine(_dir, "test.docx");
        var tools = new DocumentTools(_dir);

        var writeResult = tools.WordWrite(path, "Hello World\nLine Two", create: true);
        Assert.StartsWith("Word saved:", writeResult);

        var readResult = tools.WordRead(path);
        Assert.Contains("Hello World", readResult);
        Assert.Contains("Line Two", readResult);
    }

    [Fact]
    public void ExcelRead_AfterWrite_ReturnsData()
    {
        var path = Path.Combine(_dir, "test.xlsx");
        var tools = new DocumentTools(_dir);

        var writeResult = tools.ExcelWrite(path, """[["A1","Value1"],["B2","Value2"]]""", create: true);
        Assert.StartsWith("Excel saved:", writeResult);

        var readResult = tools.ExcelRead(path, "Sheet1");
        Assert.Contains("Value1", readResult);
        Assert.Contains("Value2", readResult);
    }

    [Fact]
    public void PptRead_AfterWrite_ReturnsContent()
    {
        var path = Path.Combine(_dir, "test.pptx");
        var tools = new DocumentTools(_dir);

        var writeResult = tools.PptWrite(path, "Slide One\nSlide Two", create: true);
        Assert.StartsWith("PPT saved:", writeResult);

        var readResult = tools.PptRead(path);
        Assert.Contains("Slide One", readResult);
    }

    [Fact]
    public void ExcelRead_FileNotFound_ReturnsError()
    {
        var tools = new DocumentTools(_dir);
        var result = tools.ExcelRead(Path.Combine(_dir, "nonexistent.xlsx"), "Sheet1");
        Assert.Contains("read error", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WordRead_FileNotFound_ReturnsError()
    {
        var tools = new DocumentTools(_dir);
        var result = tools.WordRead(Path.Combine(_dir, "nonexistent.docx"));
        Assert.Contains("read error", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PptRead_FileNotFound_ReturnsError()
    {
        var tools = new DocumentTools(_dir);
        var result = tools.PptRead(Path.Combine(_dir, "nonexistent.pptx"));
        Assert.Contains("read error", result, StringComparison.OrdinalIgnoreCase);
    }
}

// ═══════════════════════════════════════════════════════════════
//  DocumentTools — 文档生成流水线
// ═══════════════════════════════════════════════

public class DocGenPipelineTests : IDisposable
{
    private readonly string _dir;
    public DocGenPipelineTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ltai-docgen-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [Fact]
    public void RenderTemplate_SimplePlaceholders_Replaced()
    {
        var pipe = new DocumentTools(_dir);
        var result = pipe.RenderTemplate("Hello {{name}}, your score is {{score}}",
            """{"name": "Alice", "score": "95"}""");
        Assert.Contains("Hello Alice", result);
        Assert.Contains("score is 95", result);
    }

    [Fact]
    public void RenderTemplate_SectionBlocks_Conditional()
    {
        var pipe = new DocumentTools(_dir);
        var tpl = "# {{title}}\n{{#items}}\n- {{item}}\n{{/items}}";
        var result = pipe.RenderTemplate(tpl, """{"title": "List", "items": ["A", "B"]}""");
        Assert.Contains("# List", result);
        Assert.DoesNotContain("{{#items}}", result);
    }

    [Fact]
    public void RenderTemplate_EmptySection_Removed()
    {
        var pipe = new DocumentTools(_dir);
        var tpl = "Start\n{{#optional}}hidden{{/optional}}\nEnd";
        var result = pipe.RenderTemplate(tpl, """{}""");
        Assert.Contains("Start", result);
        Assert.Contains("End", result);
        Assert.DoesNotContain("hidden", result);
    }

    [Fact]
    public void InferContentTypes_HeadingsAndBody_Detected()
    {
        var pipe = new DocumentTools(_dir);
        var text = "# Title\n## Section\nBody text here\n- list item\n```\ncode block\n```";
        var result = pipe.InferContentTypes(text);
        Assert.Contains("heading", result);
        Assert.Contains("body", result);
        Assert.Contains("list", result);
        Assert.Contains("code", result);
    }

    [Fact]
    public void GetDefaultStylesJson_ReturnsValidJson()
    {
        var json = DocumentTools.GetDefaultStylesJson();
        Assert.Contains("title", json);
        Assert.Contains("heading1", json);
        Assert.Contains("fontSize", json);
        var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(json);
        Assert.NotNull(parsed);
    }

    [Fact]
    public void BuildDocument_Word_Basic()
    {
        var pipe = new DocumentTools(_dir);
        var path = Path.Combine(_dir, "test.docx");
        var result = pipe.BuildDocumentAsync("test content", path).Result;
        Assert.StartsWith("Word saved:", result);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void BuildDocument_Ppt_Basic()
    {
        var pipe = new DocumentTools(_dir);
        var path = Path.Combine(_dir, "test.pptx");
        var result = pipe.BuildDocumentAsync("test content", path).Result;
        Assert.StartsWith("PPT saved:", result);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void BuildDocument_Excel_Basic()
    {
        var pipe = new DocumentTools(_dir);
        var path = Path.Combine(_dir, "test.xlsx");
        var result = pipe.BuildDocumentAsync("test content", path).Result;
        Assert.StartsWith("Excel saved:", result);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void BuildDocument_UnsupportedFormat_ReturnsError()
    {
        var pipe = new DocumentTools(_dir);
        var path = Path.Combine(_dir, "test.txt");
        var result = pipe.BuildDocumentAsync("test", path).Result;
        Assert.Contains("Unsupported format", result);
    }

    [Fact]
    public void BuildDocument_AlreadyExists_ReturnsError()
    {
        var pipe = new DocumentTools(_dir);
        var path = Path.Combine(_dir, "exists.docx");
        File.WriteAllText(path, "dummy");
        var result = pipe.BuildDocumentAsync("test", path).Result;
        Assert.Contains("already exists", result);
    }
}

// ═══════════════════════════════════════════════════════════════
//  ExcelCopyRange — 样式保留复制
// ═══════════════════════════════════════════════

public class ExcelCopyRangeTests : IDisposable
{
    private readonly string _dir;
    public ExcelCopyRangeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ltai-xlcpy-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [Fact]
    public void CopyRange_WithinSameFile_Works()
    {
        var tools = new DocumentTools(_dir);
        var srcPath = Path.Combine(_dir, "src.xlsx");
        tools.ExcelWrite(srcPath, """[["A1","Value1"],["B1","Value2"]]""", create: true);

        // Copy A1:B1 to D1
        var result = tools.ExcelCopyRange(srcPath, "A1:B1", srcPath, "D1");
        Assert.StartsWith("Copied", result);

        var readResult = tools.ExcelRead(srcPath, "Sheet1");
        Assert.Contains("Value1", readResult);
        Assert.Contains("Value2", readResult);
    }

    [Fact]
    public void CopyRange_CrossFile_Works()
    {
        var tools = new DocumentTools(_dir);
        var srcPath = Path.Combine(_dir, "src.xlsx");
        tools.ExcelWrite(srcPath, """[["A1","SourceData"]]""", create: true);

        var tgtPath = Path.Combine(_dir, "tgt.xlsx");
        tools.ExcelWrite(tgtPath, """[["A1","OldData"]]""", create: true);

        var result = tools.ExcelCopyRange(srcPath, "A1:A1", tgtPath, "A1");
        Assert.StartsWith("Copied", result);

        var readResult = tools.ExcelRead(tgtPath, "Sheet1");
        Assert.Contains("SourceData", readResult);
    }

    [Fact]
    public void CopyRange_SourceNotFound_ReturnsError()
    {
        var tools = new DocumentTools(_dir);
        var result = tools.ExcelCopyRange("nonexistent.xlsx", "A1:B2", Path.Combine(_dir, "out.xlsx"), "A1");
        Assert.Contains("copy error", result, StringComparison.OrdinalIgnoreCase);
    }
}

// ═══════════════════════════════════════════════════════════════
//  WordCopyStyle — 样式复制
// ═══════════════════════════════════════════════

public class WordCopyStyleTests : IDisposable
{
    private readonly string _dir;
    public WordCopyStyleTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ltai-wcpy-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [Fact]
    public void CopyStyle_SourceToTarget_Works()
    {
        var tools = new DocumentTools(_dir);
        var srcPath = Path.Combine(_dir, "src.docx");
        tools.WordWrite(srcPath, "Hello World", create: true);

        var tgtPath = Path.Combine(_dir, "tgt.docx");
        tools.WordWrite(tgtPath, "Target Content", create: true);

        var result = tools.WordCopyStyle(srcPath, tgtPath);
        Assert.StartsWith("Copied styles", result);

        var readResult = tools.WordRead(tgtPath);
        Assert.Contains("Target Content", readResult);
    }

    [Fact]
    public void CopyStyle_SourceNotFound_ReturnsError()
    {
        var tools = new DocumentTools(_dir);
        var tgt = Path.Combine(_dir, "t.docx");
        tools.WordWrite(tgt, "test", create: true);
        var result = tools.WordCopyStyle("nonexistent.docx", tgt);
        Assert.Contains("copy style error", result, StringComparison.OrdinalIgnoreCase);
    }
}

// ═══════════════════════════════════════════════════════════════
//  PptCopyStyle — PPT 主题/母版复制
// ═══════════════════════════════════════════════

public class PptCopyStyleTests : IDisposable
{
    private readonly string _dir;
    public PptCopyStyleTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ltai-pcpy-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [Fact]
    public void CopyStyle_SourceToTarget_Works()
    {
        var tools = new DocumentTools(_dir);
        var srcPath = Path.Combine(_dir, "src.pptx");
        tools.PptWrite(srcPath, "Slide One", create: true);

        var tgtPath = Path.Combine(_dir, "tgt.pptx");
        tools.PptWrite(tgtPath, "Target Slide", create: true);

        var result = tools.PptCopyStyle(srcPath, tgtPath);
        Assert.StartsWith("Copied slide master", result);

        var readResult = tools.PptRead(tgtPath);
        Assert.Contains("Target Slide", readResult);
    }

    [Fact]
    public void CopyStyle_SourceNotFound_ReturnsError()
    {
        var tools = new DocumentTools(_dir);
        var tgt = Path.Combine(_dir, "t.pptx");
        tools.PptWrite(tgt, "test", create: true);
        var result = tools.PptCopyStyle("nonexistent.pptx", tgt);
        Assert.Contains("copy style error", result, StringComparison.OrdinalIgnoreCase);
    }
}

// ═══════════════════════════════════════════════════════════════
//  LLM-as-Judge — 回归测试 (DISABLED: requires FakeChatClient from testing NuGet)
// ═══════════════════════════════════════════════════════════════
