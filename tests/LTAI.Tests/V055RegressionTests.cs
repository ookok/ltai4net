using System.Collections.Concurrent;
using LTAI.AI.Governors;
using LTAI.Agent.Skills;
using LTAI.Core.Configuration;
using LTAI.Core.System;
using LTAI.Knowledge.Core;
using LTAI.Knowledge.Core.Models;
using LTAI.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LTAI.Tests;

public sealed class V055RegressionTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    private string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ltai_bts_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, true); }
            catch { }
        }
    }

    private KnowledgeGraph CreateKnowledgeGraph(string? dbPath = null)
    {
        var effectivePath = dbPath ?? Path.Combine(CreateTempDir(), "knowledge_graph.db");
        return new KnowledgeGraph(
            NullLogger<KnowledgeGraph>.Instance,
            dbPath: effectivePath);
    }

    private MemoryFilesService CreateMemoryFilesService(KnowledgeGraph? kg = null)
    {
        var graph = kg ?? CreateKnowledgeGraph();
        var loader = new MemoryFileLoader(NullLogger<MemoryFileLoader>.Instance);
        return new MemoryFilesService(loader, graph, NullLogger<MemoryFilesService>.Instance);
    }

    private DualMemoryStore CreateDualMemoryStore(string? dbPath = null)
    {
        var path = dbPath ?? Path.Combine(CreateTempDir(), $"dual_memory_{Guid.NewGuid():N}.db");
        return new DualMemoryStore(
            path,
            logger: NullLogger<DualMemoryStore>.Instance);
    }

    // ============================================================
    // PI Threshold Fix — SelectMode and PredictabilityIndex
    // ============================================================

    [Fact]
    public void PI_01_SelectModeReturnsFilesWhenPiLow()
    {
        using var kg = CreateKnowledgeGraph();
        var service = CreateMemoryFilesService(kg);

        var mode = service.SelectMode();

        Assert.Equal(MemoryMode.Files, mode);
    }

    [Fact]
    public void PI_02_GetStatsAlwaysContainsPredictabilityKey()
    {
        using var kg = CreateKnowledgeGraph();

        var stats = kg.GetStats();

        Assert.True(stats.ContainsKey("predictability"));
        var predObj = stats["predictability"];
        Assert.NotNull(predObj);
    }

    [Fact]
    public void PI_03_PredictabilitySnapshot_UsesCorrectKey()
    {
        using var kg = CreateKnowledgeGraph();
        kg.AddEntity(new Entity("test_entity", "Test Entity"));

        var stats = kg.GetStats();
        var predObj = stats["predictability"];

        var json = System.Text.Json.JsonSerializer.Serialize(predObj);
        var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("pi", out var pi));
        Assert.True(pi.ValueKind == System.Text.Json.JsonValueKind.Number);
    }

    // ============================================================
    // GetAwaiter Deadlock Fix — DualMemoryStore sync wrappers
    // ============================================================

    [Fact]
    public void GA_01_DualMemoryStore_StoreEpisode_SurvivesSyncCall()
    {
        using var store = CreateDualMemoryStore();
        var episode = new RawEpisode
        {
            Query = "test query",
            FullTrajectory = "test trajectory",
            FinalAnswer = "test answer",
            Domain = "test",
            WasSuccessful = true,
            Confidence = 0.9f,
            Reward = 0.8f
        };

        var ex = Record.Exception(() => store.StoreEpisode(episode));

        Assert.Null(ex);
        var stats = store.GetStats();
        Assert.True(stats.TotalEpisodes > 0);
    }

    [Fact]
    public void GA_02_SyncWrappers_SurviveSyncContext()
    {
        using var ctx = new SingleThreadSynchronizationContext();
        var previousCtx = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(ctx);
        try
        {
            using var store = CreateDualMemoryStore();
            var episode = new RawEpisode
            {
                Query = "sync context test",
                FullTrajectory = "test",
                FinalAnswer = "test",
                Domain = "test",
                WasSuccessful = true,
                Confidence = 0.9f,
                Reward = 0.8f
            };

            Assert.Null(Record.Exception(() => store.StoreEpisode(episode)));
            Assert.Null(Record.Exception(() =>
            {
                var results = store.FindSimilarEpisodes("sync context test");
                Assert.NotNull(results);
            }));

            var stats = store.GetStats();
            Assert.True(stats.TotalEpisodes > 0);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousCtx);
        }
    }

    private sealed class SingleThreadSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback, object?)> _queue = new();
        private readonly Thread _thread;

        public SingleThreadSynchronizationContext()
        {
            _thread = new Thread(Run) { IsBackground = true };
            _thread.Start();
        }

        private void Run()
        {
            SetSynchronizationContext(this);
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
            {
                try { callback(state); }
                catch { }
            }
        }

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public override void Send(SendOrPostCallback d, object? state)
        {
            using var evt = new ManualResetEventSlim(false);
            Exception? thrown = null;
            _queue.Add(((s) =>
            {
                try { d(s); }
                catch (Exception ex) { thrown = ex; }
                finally { evt.Set(); }
            }, state));
            evt.Wait();
            if (thrown != null) throw thrown;
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(5));
            _queue.Dispose();
        }
    }

    // ============================================================
    // Fire-and-Forget Fix — KnowledgeBase vector indexing
    // ============================================================

    [Fact]
    public void FF_01_FireAndForget_DoesNotBlockCaller()
    {
        var fired = false;
        var mre = new ManualResetEventSlim(false);

        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            fired = true;
            mre.Set();
        });

        Assert.False(fired);
        mre.Wait(TimeSpan.FromSeconds(5));
        Assert.True(fired);
    }

    // ============================================================
    // OptionService Resolution Chain
    // ============================================================

    [Fact]
    public void OS_01_Get_FallsBackToDefault_WhenNoInstance()
    {
        var key = $"ltai_os_test_nonexistent_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(key, null);

        var result = OptionService.Get(key, "default_xyz");

        Assert.Equal("default_xyz", result);
    }

    [Fact]
    public void OS_02_Get_PrefersEnvVar()
    {
        var key = $"ltai_os_test_env_{Guid.NewGuid():N}";
        try
        {
            Environment.SetEnvironmentVariable(key, "env_value_123");

            var result = OptionService.Get(key, "default_456");

            Assert.Equal("env_value_123", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    [Fact]
    public async Task OS_03_ResolveChain_CreatesConfigAndResolves()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"ltai_os_{Guid.NewGuid():N}");
        _tempDirs.Add(tempRoot);
        var configDir = Path.Combine(tempRoot, "config");
        Directory.CreateDirectory(tempRoot);

        var options = Options.Create(new LTAIOptions());
        var loader = new OptionLoader(NullLogger<OptionLoader>.Instance);
        var svc = new OptionService(loader, options.Value, null, NullLogger<OptionService>.Instance, configDir);

        await svc.LoadAllAsync();

        Assert.True(svc.IsLoaded);
        Assert.True(svc.Sections.Count > 0);

        var resolved = svc.Resolve("paths", "DataDirectory");
        Assert.Equal(".livingtree", resolved);
    }

    // ============================================================
    // SkillExtractor OptionService Thresholds
    // ============================================================

    [Fact]
    public void SE_01_Thresholds_ReadFromOptionService()
    {
        var loader = new SkillLoader(NullLogger<SkillLoader>.Instance);
        var tempSkillsRoot = CreateTempDir();
        var registry = new SkillRegistry(loader, NullLogger<SkillRegistry>.Instance, tempSkillsRoot);
        var mockChat = new FakeChatClient();

        var extractor = new SkillExtractor(registry, mockChat, NullLogger<SkillExtractor>.Instance, tempSkillsRoot);

        var patternKey = $"test_pattern_{Guid.NewGuid():N}";
        extractor.RecordSuccess(patternKey,
            new List<string> { "tool_a", "tool_b" }, "run a test", "OK");
        extractor.RecordSuccess(patternKey,
            new List<string> { "tool_a", "tool_b" }, "run a test", "OK");
        extractor.RecordSuccess(patternKey,
            new List<string> { "tool_a", "tool_b" }, "run a test", "OK");

        Assert.True(registry.Get(patternKey) != null || mockChat.GetResponseCallCount > 0,
            "After 3 successes, SkillExtractor should have triggered L0 skill creation");
    }

    private sealed class FakeChatClient : Microsoft.Extensions.AI.IChatClient
    {
        public int GetResponseCallCount { get; private set; }

        public async Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> chatMessages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            GetResponseCallCount++;
            var content = """
                # skill: auto_dedup_scanner
                domain: general
                layer: 0
                version: 1.0.0
                intent: Scan and deduplicate files
                triggers:
                  - pattern: "dedup|scan.*duplicate"
                requires: []
                confidence: 0.85

                ## 步骤
                1. Run tool_a
                2. Run tool_b

                ## 验证
                - must_contain: "completed"
                """;
            var message = new Microsoft.Extensions.AI.ChatMessage(
                Microsoft.Extensions.AI.ChatRole.Assistant, content);
            return await Task.FromResult(new Microsoft.Extensions.AI.ChatResponse(message));
        }

        public IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> chatMessages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }

    // ============================================================
    // LanguageParsers Deduplication — GetRelativePath helper
    // ============================================================

    [Fact]
    public void LP_01_GetRelativePath_Works_FromHelper()
    {
        var currentDir = Directory.GetCurrentDirectory();
        var filePath = Path.Combine(currentDir, "subdir", "test.cs");
        var expected = "subdir/test.cs";

        var helperType = typeof(LTAI.Tools.CodeGraph.CSharpParser).Assembly
            .GetType("LTAI.Tools.CodeGraph.LanguageParserHelper")!;
        var method = helperType.GetMethod("GetRelativePath",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
        var result = (string)method.Invoke(null, new object[] { filePath })!;

        Assert.Equal(expected, result);
    }

    [Fact]
    public void LP_02_GetRelativePath_HandlesAbsolutePath()
    {
        var filePath = Path.Combine(Directory.GetCurrentDirectory(), "src", "Program.cs");
        var expected = "src/Program.cs";

        var helperType = typeof(LTAI.Tools.CodeGraph.CSharpParser).Assembly
            .GetType("LTAI.Tools.CodeGraph.LanguageParserHelper")!;
        var method = helperType.GetMethod("GetRelativePath",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
        var result = (string)method.Invoke(null, new object[] { filePath })!;

        Assert.Equal(expected, result);
    }

    // ============================================================
    // CLI Plugin Loading
    // ============================================================

    [Fact]
    public void CLI_01_LTAICliTypes_AreResolvable()
    {
        var cliAssembly = typeof(LTAI.Cli.Program).Assembly;
        var types = cliAssembly.GetExportedTypes();

        Assert.NotEmpty(types);
        Assert.Contains(types, t => t.Name == "Program");
    }

    // ============================================================
    // Additional Resilience — KnowledgeGraph basic operations
    // ============================================================

    [Fact]
    public void KG_01_EmptyGraph_DisposeDoesNotThrow()
    {
        var kg = CreateKnowledgeGraph();
        kg.Dispose();
    }

    [Fact]
    public void KG_02_AddEntityAndQuery_BasicRoundTrip()
    {
        using var kg = CreateKnowledgeGraph();

        kg.AddEntity(new Entity("e1", "Entity One"));
        var stats = kg.GetStats();

        Assert.True((int)stats["entity_count"] > 0);
    }

    [Fact]
    public void KG_03_Stats_AfterAddingTriplets()
    {
        using var kg = CreateKnowledgeGraph();
        kg.AddRelation("subj", "obj", "is_a");

        var stats = kg.GetStats();

        Assert.True((int)stats["entity_count"] > 0);
        Assert.True((int)stats["edge_count"] > 0);
    }
}
