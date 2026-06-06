using Xunit;
using LTAI.Core.Configuration;
using LTAI.Core.I18n;
using LTAI.Agent;
using LTAI.Agent.Tools;
using LTAI.Core.Safety;
using LTAI.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Tests;

// ═══════════════════════════════════════════════
//  Unit Tests — SecretManager
// ═══════════════════════════════════════════════

public class SecretManagerUnitTests
{
    [Fact]
    public void SetAndGet_Roundtrip()
    {
        var key = "LTAI_UT_" + Guid.NewGuid().ToString("N")[..8];
        SecretManager.Set(key, "sk-test-value", persistent: false);
        Assert.Equal("sk-test-value", SecretManager.Get(key));
        SecretManager.Invalidate(key);
    }

    [Fact]
    public void MissingKey_ReturnsNull()
    {
        Assert.Null(SecretManager.Get("LTAI_NONEXISTENT_" + Guid.NewGuid().ToString("N")[..8]));
    }

    [Fact]
    public void Has_DetectsPresence()
    {
        var key = "LTAI_UT_HAS_" + Guid.NewGuid().ToString("N")[..8];
        Assert.False(SecretManager.Has(key));
        SecretManager.Set(key, "present", persistent: false);
        Assert.True(SecretManager.Has(key));
        SecretManager.Invalidate(key);
    }

    [Fact]
    public void Invalidate_ForcesReRead()
    {
        var key = "LTAI_UT_INV_" + Guid.NewGuid().ToString("N")[..8];
        SecretManager.Set(key, "v1", persistent: false);
        Assert.Equal("v1", SecretManager.Get(key));
        SecretManager.Invalidate(key);
        SecretManager.Set(key, "v2", persistent: false);
        Assert.Equal("v2", SecretManager.Get(key));
        SecretManager.Invalidate(key);
    }

    [Fact]
    public void Set_Null_ClearsValue()
    {
        var key = "LTAI_UT_NULL_" + Guid.NewGuid().ToString("N")[..8];
        SecretManager.Set(key, "val", persistent: false);
        Assert.True(SecretManager.Has(key));
        SecretManager.Set(key, null, persistent: false);
        Assert.False(SecretManager.Has(key));
        SecretManager.Invalidate(key);
    }
}

// ═══════════════════════════════════════════════
//  Unit Tests — Locale (i18n)
// ═══════════════════════════════════════════════

public class LocaleUnitTests
{
    [Fact]
    public void Default_IsChineseOrEnglish()
    {
        Assert.True(Locale.CurrentLang == "zh-CN" || Locale.CurrentLang == "en-US");
    }

    [Fact]
    public void Get_KnownKey_ReturnsString()
    {
        var val = Locale.Get("AppName");
        Assert.False(string.IsNullOrEmpty(val));
    }

    [Fact]
    public void Get_UnknownKey_ReturnsKeyItself()
    {
        Assert.Equal("__nonexistent_key__", Locale.Get("__nonexistent_key__"));
    }

    [Fact]
    public void Get_IsCultureAware()
    {
        Locale.SetLang("en-US");
        Assert.Contains("LTAI Assistant", Locale.Get("AppName"));
        Locale.SetLang("zh-CN");
        Assert.Contains("智能助手", Locale.Get("AppName"));
    }

    [Fact]
    public void Format_WithArgs_Works()
    {
        Locale.SetLang("en-US");
        // JobsCount has format {0} jobs ({1} running)
        var val = Locale.Format("JobsCount", "5", "2");
        Assert.Contains("5", val);
        Assert.Contains("2", val);
        Locale.SetLang("zh-CN");
    }

    [Fact]
    public void SetLang_Invalid_RevertsToDefault()
    {
        Locale.SetLang("zh-CN");
        var zh = Locale.Get("AppName");
        Locale.SetLang("fr-FR"); // unsupported — stays at previous
        Assert.Equal(zh, Locale.Get("AppName"));
    }

    [Fact]
    public void IsChinese_CorrectWithSetLang()
    {
        Locale.SetLang("zh-CN");
        Assert.True(Locale.IsChinese);
        Locale.SetLang("en-US");
        Assert.False(Locale.IsChinese);
    }
}

// ═══════════════════════════════════════════════
//  Unit Tests — CircuitBreakerStore
// ═══════════════════════════════════════════════

public class CircuitBreakerStoreUnitTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"ltai-cb-ut-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task SaveAndLoad_Roundtrip()
    {
        var path = TempDb();
        using var store = new CircuitBreakerStore(path);
        await store.SaveAsync("p1", 3, DateTime.UtcNow.AddSeconds(30));
        var (failures, cooldown) = await store.LoadAsync("p1");
        Assert.Equal(3, failures);
        Assert.NotNull(cooldown);
        Assert.True(cooldown.Value > DateTime.UtcNow);
    }

    [Fact]
    public async Task Clear_ResetsToZero()
    {
        var path = TempDb();
        using var store = new CircuitBreakerStore(path);
        await store.SaveAsync("p2", 5, null);
        await store.ClearAsync("p2");
        var (failures, _) = await store.LoadAsync("p2");
        Assert.Equal(0, failures);
    }

    [Fact]
    public async Task LoadAll_ReturnsAll()
    {
        var path = TempDb();
        using var store = new CircuitBreakerStore(path);
        await store.SaveAsync("a", 1, null);
        await store.SaveAsync("b", 2, DateTime.UtcNow.AddMinutes(1));
        var all = await store.LoadAllAsync();
        Assert.Equal(2, all.Count);
        Assert.True(all.ContainsKey("a"));
        Assert.True(all.ContainsKey("b"));
    }

    [Fact]
    public async Task LoadNonexistent_ReturnsZero()
    {
        var path = TempDb();
        using var store = new CircuitBreakerStore(path);
        var (failures, _) = await store.LoadAsync("nonexistent");
        Assert.Equal(0, failures);
    }
}

// ═══════════════════════════════════════════════
//  Unit Tests — ToolDescriptionProvider
// ═══════════════════════════════════════════════

public class ToolDescriptionProviderUnitTests
{
    [Fact]
    public void RegisterAndGet_Roundtrip()
    {
        ToolDescriptionProvider.Register("TestTool", "en", "Test tool description");
        var en = ToolDescriptionProvider.Get("TestTool", "en");
        Assert.Equal("Test tool description", en);
    }

    [Fact]
    public void Get_Unregistered_ReturnsNull()
    {
        Assert.Null(ToolDescriptionProvider.Get("NonExistent", "en"));
    }

    [Fact]
    public void Resolve_FallsBackToChinese()
    {
        Locale.SetLang("zh-CN");
        ToolDescriptionProvider.Register("MyTool", "zh-CN", "中文描述");
        var resolved = ToolDescriptionProvider.Resolve("MyTool");
        Assert.Equal("中文描述", resolved);
        Locale.SetLang("zh-CN");
    }
}

// ═══════════════════════════════════════════════
//  Unit Tests — AgentRegistry
// ═══════════════════════════════════════════════

public class AgentRegistryUnitTests
{
    [Fact]
    public void ParseFile_ValidFrontmatter_ReturnsDef()
    {
        var md = """
            ---
            name: TestAgent
            description: A test agent
            temperature: 0.5
            topP: 0.9
            permissions: [read, write]
            tools: [filesystem, git]
            modelId: deepseek-v4-flash
            ---
            This is the prompt body.
            """;
        var def = AgentRegistry.Parse(md);
        Assert.NotNull(def);
        Assert.Equal("TestAgent", def.Name);
        Assert.Equal("A test agent", def.Description);
        Assert.Equal(0.5, def.Temperature);
        Assert.Equal(0.9, def.TopP);
        Assert.Equal("deepseek-v4-flash", def.ModelId);
        Assert.Contains("read", def.Permissions);
        Assert.Contains("write", def.Permissions);
        Assert.Contains("filesystem", def.Tools);
        Assert.Contains("git", def.Tools);
        Assert.Equal("This is the prompt body.", def.Prompt);
    }

    [Fact]
    public void Parse_MissingFrontmatter_ReturnsNull()
    {
        Assert.Null(AgentRegistry.Parse("Just some text without frontmatter."));
    }

    [Fact]
    public void Parse_EmptyString_ReturnsNull()
    {
        Assert.Null(AgentRegistry.Parse(""));
    }

    [Fact]
    public void Parse_CSVStylePermissions_Parses()
    {
        var md = """
            ---
            name: CSVAgent
            permissions: [read, write, list]
            tools: [shell, git]
            ---
            Body
            """;
        var def = AgentRegistry.Parse(md);
        Assert.NotNull(def);
        Assert.Contains("read", def.Permissions);
        Assert.Contains("shell", def.Tools);
    }

    [Fact]
    public void CapabilityText_IncludesDescriptionAndTools()
    {
        var md = """
            ---
            name: CapAgent
            description: Test capabilities
            tools: [a, b]
            ---
            Prompt text here
            """;
        var def = AgentRegistry.Parse(md);
        Assert.NotNull(def);
        Assert.Contains("Test capabilities", def.CapabilityText);
        Assert.Contains("a", def.CapabilityText);
        Assert.Contains("Prompt", def.CapabilityText);
    }
}

// ═══════════════════════════════════════════════
//  Unit Tests — SafeShellTool
// ═══════════════════════════════════════════════

public class SafeShellToolUnitTests
{
    [Fact]
    public async Task DangerousCommand_ReturnsError()
    {
        var tool = new SafeShellTool(Directory.GetCurrentDirectory());
        var result = await tool.RunCommand("rm -rf /", confirm: true);
        Assert.Contains("危险", result);
    }

    [Fact]
    public async Task SimpleCommand_WithoutConfirm_ReturnsWarning()
    {
        var tool = new SafeShellTool(Directory.GetCurrentDirectory());
        var result = await tool.RunCommand("echo hello", confirm: false);
        Assert.Contains("确认", result);
    }

    [Fact]
    public async Task SimpleCommand_WithConfirm_Runs()
    {
        var tool = new SafeShellTool(Directory.GetCurrentDirectory());
        var result = await tool.RunCommand("echo hello world", confirm: true);
        Assert.Contains("hello world", result);
    }

    [Fact]
    public async Task SudoCommand_Blocked()
    {
        var tool = new SafeShellTool(Directory.GetCurrentDirectory());
        Assert.Contains("阻止", await tool.RunCommand("sudo rm -rf /", confirm: true));
    }

    [Fact]
    public async Task DotnetVersion_Safe_Allowed()
    {
        var tool = new SafeShellTool(Directory.GetCurrentDirectory());
        var result = await tool.RunCommand("dotnet --version", confirm: true);
        Assert.False(string.IsNullOrWhiteSpace(result));
    }
}

// ═══════════════════════════════════════════════
//  Unit Tests — SafetyRules
// ═══════════════════════════════════════════════

public class SafetyRulesUnitTests
{
    [Theory]
    [InlineData("Hello, how are you?", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    [InlineData("my api_key a1b2c3d4e5f6g7h8i9j0k1l2m3n4o5", false)]
    [InlineData("DROP TABLE users", false)]
    [InlineData("<script>alert('xss')</script>", false)]
    [InlineData("4111 1111 1111 1111", false)]
    [InlineData("+86 138 0013 8000", false)]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----", false)]
    public void IsSafeByRules_VariousInputs_ReturnsExpected(string? input, bool expected)
    {
        Assert.Equal(expected, SafetyRules.IsSafeByRules(input!));
    }
}

// ═══════════════════════════════════════════════
//  Unit Tests — LTAIOptionsValidator
// ═══════════════════════════════════════════════

public class OptionsValidatorUnitTests
{
    private static LTAIOptions ValidOptions => new()
    {
        AI = new AIConfig
        {
            DefaultProvider = "deepseek",
            MaxTokens = 4096,
            Temperature = 0.7,
            GlobalTokenBudget = 1_000_000,
            PerUserTokenBudget = 200_000,
        },
        Web = new WebConfig { Port = 5100 },
        MaxHistoryMessages = 200,
        DataDirectory = ".livingtree",
    };

    [Fact]
    public void Valid_Passes() => Assert.True(new LTAIOptionsValidator().Validate(null, ValidOptions).Succeeded);

    [Fact]
    public void EmptyProvider_Fails()
    {
        Assert.False(new LTAIOptionsValidator().Validate(null, new LTAIOptions
        {
            AI = new AIConfig { DefaultProvider = "", MaxTokens = 4096, Temperature = 0.7, GlobalTokenBudget = 1_000_000, PerUserTokenBudget = 200_000 },
            Web = new WebConfig { Port = 5100 }, MaxHistoryMessages = 200, DataDirectory = ".livingtree",
        }).Succeeded);
    }

    [Fact]
    public void ZeroMaxTokens_Fails()
    {
        Assert.False(new LTAIOptionsValidator().Validate(null, new LTAIOptions
        {
            AI = new AIConfig { DefaultProvider = "deepseek", MaxTokens = 0, Temperature = 0.7, GlobalTokenBudget = 1_000_000, PerUserTokenBudget = 200_000 },
            Web = new WebConfig { Port = 5100 }, MaxHistoryMessages = 200, DataDirectory = ".livingtree",
        }).Succeeded);
    }

    [Fact]
    public void InvalidTemperature_Fails()
    {
        Assert.False(new LTAIOptionsValidator().Validate(null, new LTAIOptions
        {
            AI = new AIConfig { DefaultProvider = "deepseek", MaxTokens = 4096, Temperature = 3.0, GlobalTokenBudget = 1_000_000, PerUserTokenBudget = 200_000 },
            Web = new WebConfig { Port = 5100 }, MaxHistoryMessages = 200, DataDirectory = ".livingtree",
        }).Succeeded);
    }

    [Fact]
    public void InvalidPort_Fails()
    {
        Assert.False(new LTAIOptionsValidator().Validate(null, new LTAIOptions
        {
            AI = new AIConfig { DefaultProvider = "deepseek", MaxTokens = 4096, Temperature = 0.7, GlobalTokenBudget = 1_000_000, PerUserTokenBudget = 200_000 },
            Web = new WebConfig { Port = 99999 }, MaxHistoryMessages = 200, DataDirectory = ".livingtree",
        }).Succeeded);
    }
}

// ═══════════════════════════════════════════════
//  Unit Tests — PathUtils
// ═══════════════════════════════════════════════

public class PathUtilsUnitTests
{
    [Fact]
    public void SafeResolvePath_NullInput_ReturnsNull()
    {
        Assert.Null(PathUtils.SafeResolvePath(Environment.CurrentDirectory, null!));
        Assert.Null(PathUtils.SafeResolvePath(Environment.CurrentDirectory, ""));
        Assert.Null(PathUtils.SafeResolvePath(Environment.CurrentDirectory, "   "));
    }

    [Fact]
    public void SafeResolvePath_PathTraversal_ReturnsNull()
    {
        Assert.Null(PathUtils.SafeResolvePath(Environment.CurrentDirectory,
            $"..{Path.DirectorySeparatorChar}..{Path.DirectorySeparatorChar}tmp"));
    }

    [Fact]
    public void SafeResolvePath_ValidRelative_ReturnsPath()
    {
        var result = PathUtils.SafeResolvePath(Environment.CurrentDirectory, "test.txt");
        Assert.NotNull(result);
        Assert.EndsWith("test.txt", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PathPermissionStore_GrantAndCheck_Works()
    {
        PathUtils.PathPermissionStore.Clear();
        var testPath = Path.Combine(Path.GetTempPath(), "ltai_granted_test.txt");
        Assert.False(PathUtils.PathPermissionStore.IsGranted(testPath));
        PathUtils.PathPermissionStore.Grant(testPath);
        Assert.True(PathUtils.PathPermissionStore.IsGranted(testPath));
        PathUtils.PathPermissionStore.Revoke(testPath);
        Assert.False(PathUtils.PathPermissionStore.IsGranted(testPath));
    }
}

// ═══════════════════════════════════════════════
//  Integration Tests — KgStore (Core CRUD + FTS + BFS)
// ═══════════════════════════════════════════════

public class KgStoreIntegrationTests : IDisposable
{
    private readonly string _dir;

    public KgStoreIntegrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ltai-kg-int-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task UpsertNode_CreatesAndReturnsId()
    {
        using var store = new LTAI.Agent.Vector.KgStore(Path.Combine(_dir, "crud.db"));
        var id = await store.UpsertNode("ext:1", "class", "TestClass",
            ns: "LTAI.Tests", signature: "", source: "test.cs");
        Assert.True(id > 0);

        var node = await store.GetNode(id);
        Assert.NotNull(node);
        Assert.Equal("class", node.Kind);
        Assert.Equal("TestClass", node.Name);
    }

    [Fact]
    public async Task UpsertNode_UpdateExisting_KeepsSameId()
    {
        using var store = new LTAI.Agent.Vector.KgStore(Path.Combine(_dir, "update.db"));
        var id1 = await store.UpsertNode("x:1", "class", "A");
        var id2 = await store.UpsertNode("x:1", "method", "B");
        Assert.Equal(id1, id2);
        Assert.Equal("method", (await store.GetNode(id1))!.Kind);
    }

    [Fact]
    public async Task AddEdge_AndTraverseBfs_ReturnsNeighbors()
    {
        using var store = new LTAI.Agent.Vector.KgStore(Path.Combine(_dir, "graph.db"));
        var a = await store.UpsertNode("n:a", "class", "ClassA");
        var b = await store.UpsertNode("n:b", "method", "MethodB");
        await store.AddEdge(a, b, "calls");
        var neighbors = await store.TraverseBfs([a], maxDepth: 1);
        Assert.Contains(neighbors, n => n.Id == b);
    }

    [Fact]
    public async Task SearchFts_BasicQuery_ReturnsResults()
    {
        using var store = new LTAI.Agent.Vector.KgStore(Path.Combine(_dir, "fts.db"));
        var id = await store.UpsertNode("doc:1", "document", "TestDoc");
        await store.AddDoc(id, "This is a test document about authentication");
        await store.OptimizeFtsAsync();

        var results = await store.SearchFts("authentication");
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.nodeId == id);
    }

    [Fact]
    public async Task DeleteNode_CascadeRemovesEdges()
    {
        using var store = new LTAI.Agent.Vector.KgStore(Path.Combine(_dir, "cascade.db"));
        var a = await store.UpsertNode("d:a", "class", "A");
        var b = await store.UpsertNode("d:b", "class", "B");
        await store.AddEdge(a, b, "calls");
        await store.DeleteNode(a);
        var edges = await store.GetEdges(nodeId: a);
        Assert.Empty(edges);
    }

    [Fact]
    public async Task GetNodeByExtId_NotFound_ReturnsNull()
    {
        using var store = new LTAI.Agent.Vector.KgStore(Path.Combine(_dir, "notfound.db"));
        Assert.Null(await store.GetNodeByExtId("nonexistent"));
    }

    [Fact]
    public async Task ConcurrentWrites_NoDeadlock()
    {
        using var store = new LTAI.Agent.Vector.KgStore(Path.Combine(_dir, "concur.db"));
        var tasks = Enumerable.Range(0, 5).Select(i =>
            store.UpsertNode($"n:{i}", "class", $"Node{i}"));
        var ids = await Task.WhenAll(tasks);
        Assert.Equal(5, ids.Distinct().Count());
    }

    [Fact]
    public async Task Meta_SetAndGet_Works()
    {
        using var store = new LTAI.Agent.Vector.KgStore(Path.Combine(_dir, "meta.db"));
        await store.SetMeta("ver", "1.0");
        Assert.Equal("1.0", await store.GetMeta("ver"));
    }
}

// ═══════════════════════════════════════════════
//  Integration Tests — MultiProviderChatClient (Degradation)
// ═══════════════════════════════════════════════

public class MultiProviderChatClientIntegrationTests
{
    [Fact]
    public async Task GetResponseAsync_NoProviders_ReturnsFailure()
    {
        var router = new LTAI.AI.MultiProviderChatClient(new LTAIOptions());
        var resp = await router.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        var text = resp.Messages?.LastOrDefault()?.Text ?? "";
        Assert.Contains("failed", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Register_Provider_CanBeResolved()
    {
        var router = new LTAI.AI.MultiProviderChatClient(new LTAIOptions());
        router.Register("test", new EchoChatClient("hello back"));
        Assert.Contains("test", router.RegisteredProviders);
    }

    [Fact]
    public async Task GetResponseAsync_WithProvider_Succeeds()
    {
        var router = new LTAI.AI.MultiProviderChatClient(new LTAIOptions());
        router.Register("l1", new EchoChatClient("hello back"));
        var resp = await router.GetResponseAsync([new ChatMessage(ChatRole.User, "say hi")]);
        var text = resp.Messages?.LastOrDefault()?.Text ?? "";
        Assert.Contains("hello back", text);
    }

    [Fact]
    public async Task Degradation_FallbackOnFailure()
    {
        var opts = new LTAIOptions
        {
            AI = new AIConfig
            {
                DefaultProvider = "primary",
                DegradationChain = new() { ["l1"] = "secondary" },
                GlobalTokenBudget = 1_000_000,
                PerUserTokenBudget = 200_000,
            }
        };
        var router = new LTAI.AI.MultiProviderChatClient(opts);
        router.Register("secondary", new EchoChatClient("fallback ok"));
        // l1 not registered → fallback to secondary via degradation chain
        var resp = await router.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        var text = resp.Messages?.LastOrDefault()?.Text ?? "";
        Assert.Contains("fallback ok", text);
    }
}

/// <summary>EchoChatClient returns a fixed response for any input.</summary>
public sealed class EchoChatClient : IChatClient
{
    private readonly string _response;
    public EchoChatClient(string response) => _response = response;
    public void Dispose() { }
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public object? GetService(Type serviceType, string? serviceKey) => null;
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _response)));
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => AsyncEnumerable.Repeat(new ChatResponseUpdate(ChatRole.Assistant, _response), 1);
}

// ═══════════════════════════════════════════════
//  Integration Tests — DecisionTreeRouter
// ═══════════════════════════════════════════════

public class DecisionTreeRouterIntegrationTests
{
    [Fact]
    public void Constructor_WithNullEmbedder_Works()
    {
        var router = new LTAI.Agent.Workflows.DecisionTreeRouter(
            null, NullLogger<LTAI.Agent.Workflows.DecisionTreeRouter>.Instance);
        Assert.NotNull(router);
    }

    [Fact]
    public void Constructor_WithOptions_AppliesDefaults()
    {
        var router = new LTAI.Agent.Workflows.DecisionTreeRouter(
            null, NullLogger<LTAI.Agent.Workflows.DecisionTreeRouter>.Instance,
            options: new LTAI.Agent.Workflows.DecisionTreeRouterOptions
            {
                TopK = 3,
                ConfidenceMarginThreshold = 0.15f,
                MinTopScoreThreshold = 0.30f,
            });
        Assert.NotNull(router);
    }
}

// ═══════════════════════════════════════════════
//  E2E Tests — CLI Dispatch
// ═══════════════════════════════════════════════

// E2E CLI tests are in LTAI.Cli project (access to internal Program class).
// This test validates the CLI agent print formatting.
public class AgentPrintE2ETests
{
    [Fact]
    public void AgentRegistry_LoadAll_ReturnsAgents()
    {
        var defs = AgentRegistry.LoadAll();
        Assert.NotEmpty(defs);
        Assert.Contains(defs, d => d.Name == "LTAI-Chat");
        Assert.Contains(defs, d => d.Name == "LTAI-Code");
    }
}

// ═══════════════════════════════════════════════
//  Integration Tests — WorkflowI18nResolver
// ═══════════════════════════════════════════════

[Collection("Locale")]
public class WorkflowI18nResolverTests
{
    [Fact]
    public void Resolve_KnownKey_Replaces()
    {
        Locale.SetLang("zh-CN");
        var result = LTAI.Core.I18n.WorkflowI18nResolver.Resolve("Hello: {{locale.GreetingHello}}");
        Assert.Contains("你好", result);
        Assert.DoesNotContain("{{locale.GreetingHello}}", result);
    }

    [Fact]
    public void Resolve_UnknownKey_KeepsOriginal()
    {
        Locale.SetLang("en-US");
        var result = LTAI.Core.I18n.WorkflowI18nResolver.Resolve("{{locale.NonexistentKey}}");
        Assert.Contains("NonexistentKey", result);
    }

    [Fact]
    public void Resolve_MultipleKeys_AllReplaced()
    {
        Locale.SetLang("zh-CN");
        var result = LTAI.Core.I18n.WorkflowI18nResolver.Resolve(
            "{{locale.GreetingHello}} -- {{locale.GreetingFarewell}}");
        Assert.Contains("你好", result);
        Assert.Contains("再见", result);
    }
}
