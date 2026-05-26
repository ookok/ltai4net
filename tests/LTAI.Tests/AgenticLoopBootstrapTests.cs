using System.Runtime.CompilerServices;
using LTAI.Agent.MAF;
using LTAI.Agent.Skills;
using LTAI.AI.Governors;
using LTAI.AI.Interfaces;
using LTAI.Core.Execution;
using LTAI.DNA;
using LTAI.Knowledge.Core;
using LTAI.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LTAI.Tests;

public sealed class AgenticLoopBootstrapTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var envVar in _envVarsToRestore)
        {
            try { Environment.SetEnvironmentVariable(envVar.Key, envVar.Value); } catch { }
        }
        _envVarsToRestore.Clear();

        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ltai_btb_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private readonly Dictionary<string, string?> _envVarsToRestore = new();

    private void SetEnvVar(string name, string value)
    {
        var old = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process);
        _envVarsToRestore[name] = old;
        Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.Process);
    }

    [Fact]
    public void BTS_BOOT_01_SystemPromptAssemblerSevenLayerPrompt()
    {
        var tempDir = CreateTempDir();

        File.WriteAllText(Path.Combine(tempDir, "AGENTS.md"),
            "# LTAI Bootstrap Agent\nrole: test harness\n");

        var l0Dir = Path.Combine(tempDir, "l0_atomic");
        Directory.CreateDirectory(l0Dir);

        File.WriteAllText(Path.Combine(l0Dir, "btb_s1.md"), """
            # skill: btb_skill_one
            domain: test/boot
            layer: 0
            version: 1.0.0
            intent: First bootstrap test skill

            triggers:
              - pattern: "bootstrap|boot|启动"
                weight: 1.0

            ## 步骤
            1. shell: echo boot_skill_one activated

            ## 验证
            - must_contain: "boot"
            """);

        File.WriteAllText(Path.Combine(l0Dir, "btb_s2.md"), """
            # skill: btb_skill_two
            domain: test/boot
            layer: 0
            version: 1.0.0
            intent: Second bootstrap test skill

            triggers:
              - pattern: "verify|验证|check"
                weight: 1.0

            ## 步骤
            1. shell: echo boot_skill_two activated

            ## 验证
            - must_contain: "verify"
            """);

        var loader = new SkillLoader(NullLogger<SkillLoader>.Instance);
        var registry = new SkillRegistry(loader, NullLogger<SkillRegistry>.Instance, tempDir);
        registry.LoadAllAsync().GetAwaiter().GetResult();

        Assert.True(registry.All.Count >= 2);

        var assembler = new SystemPromptAssembler(registry);

        var ctx = new PromptLayerContext
        {
            WorkspaceRoot = tempDir,
            CurrentDir = tempDir,
            Platform = "win32",
            Date = "2026-05-26",
            Shell = "pwsh",
            GitBranch = "main",
            GitClean = true,
            BuildOk = true,
            Domain = "test/boot",
            ModeHint = "Bootstrap test mode",
            TaskInstructions = "Verify the full agentic loop bootstrap chain",
            BuildDiagnostics = "Build: OK, no errors",
            MemoryContext = "Memory: bootstrap context from prior runs"
        };

        var assembled = assembler.Assemble(ctx);

        Assert.Contains("[AGENTS.md", assembled);
        Assert.Contains("LTAI Bootstrap Agent", assembled);
        Assert.Contains("[Mode]", assembled);
        Assert.Contains("Bootstrap test mode", assembled);
        Assert.Contains("[Environment]", assembled);
        Assert.Contains(tempDir, assembled);
        Assert.Contains("win32", assembled);
        Assert.Contains("pwsh", assembled);
        Assert.Contains("main", assembled);
        Assert.Contains("[Skills]", assembled);
        Assert.Contains("btb_skill_one", assembled);
        Assert.Contains("btb_skill_two", assembled);
        Assert.Contains("L0 Atomic", assembled);
        Assert.Contains("[Task]", assembled);
        Assert.Contains("bootstrap chain", assembled);
        Assert.Contains("[Diagnostics]", assembled);
        Assert.Contains("Build: OK", assembled);
        Assert.Contains("[Memory]", assembled);
        Assert.Contains("bootstrap context", assembled);

        var noSkillsCtx = new PromptLayerContext
        {
            WorkspaceRoot = tempDir,
            SuppressSkills = true,
            TaskInstructions = "just test"
        };
        var suppressed = assembler.Assemble(noSkillsCtx);
        Assert.DoesNotContain("[Skills]", suppressed);

        assembler.InvalidateAgentsMdCache();
        var noAgentsMdCtx = new PromptLayerContext { WorkspaceRoot = CreateTempDir() };
        var noAgentsMd = assembler.Assemble(noAgentsMdCtx);
        Assert.DoesNotContain("[AGENTS.md", noAgentsMd);
    }

    [Fact]
    public void BTS_BOOT_02_PartAssemblerStateMachine()
    {
        var assembler = new PartAssembler();
        var appendedParts = new List<Part>();
        var updatedParts = new List<Part>();

        assembler.OnPartAppended += p => appendedParts.Add(p);
        assembler.OnPartUpdated += p => updatedParts.Add(p);

        assembler.FeedText("hello");
        Assert.Single(appendedParts);
        Assert.IsType<TextPart>(appendedParts[0]);
        Assert.Equal("hello", ((TextPart)appendedParts[0]).Text);

        assembler.FeedText(" world");
        Assert.Equal(2, appendedParts.Count + updatedParts.Count);
        var textPart = (TextPart)appendedParts[0];
        var updatedText = (TextPart)updatedParts.Last();
        Assert.Contains("hello world", updatedText.Text);

        appendedParts.Clear();
        updatedParts.Clear();

        var toolPart = assembler.StartToolInvocation("dotnet build", "--no-restore");
        Assert.Single(appendedParts);
        Assert.IsType<ToolInvocationPart>(appendedParts[0]);
        Assert.Equal("dotnet build", ((ToolInvocationPart)appendedParts[0]).ToolName);
        Assert.Equal(ToolState.Pending, ((ToolInvocationPart)appendedParts[0]).State);

        assembler.UpdateToolState(toolPart.Id, ToolState.Executing);
        Assert.Single(updatedParts);
        var updatedTool = (ToolInvocationPart)updatedParts.Last();
        Assert.Equal(ToolState.Executing, updatedTool.State);

        updatedParts.Clear();
        assembler.UpdateToolState(toolPart.Id, ToolState.Completed, "Build succeeded", null);
        Assert.Single(updatedParts);
        var completedTool = (ToolInvocationPart)updatedParts.Last();
        Assert.Equal(ToolState.Completed, completedTool.State);
        Assert.Equal("Build succeeded", completedTool.Output);

        appendedParts.Clear();
        assembler.AddFilePart("test.cs", "public class Test {}", null);
        Assert.Single(appendedParts);
        Assert.IsType<FilePart>(appendedParts[0]);
        var filePart = (FilePart)appendedParts[0];
        Assert.Equal("test.cs", filePart.Path);
        Assert.Equal("public class Test {}", filePart.Content);

        var snapshot = assembler.Snapshot();
        Assert.True(snapshot.Length >= 3);
    }

    [Fact]
    public async Task BTS_BOOT_03_PartStreamStorePersistAndReplay()
    {
        var tempDir = CreateTempDir();
        SetEnvVar("LTAI_WORKSPACE", tempDir);

        var partStore = new PartStreamStore(tempDir);
        var sessionId = "test_session_03";

        var textPart = new TextPart("p_t1", "Hello from text part") { Seq = 1 };
        var toolPart = new ToolInvocationPart("p_t2", "dotnet build", null, ToolState.Executing) { Seq = 2 };
        var filePart = new FilePart("p_t3", "Program.cs", "namespace Test {}", null, null) { Seq = 3 };

        await partStore.AppendAsync(sessionId, textPart, CancellationToken.None);
        await partStore.AppendAsync(sessionId, toolPart, CancellationToken.None);
        await partStore.AppendAsync(sessionId, filePart, CancellationToken.None);

        var replayed = await partStore.ReplayAsync(sessionId, CancellationToken.None);
        Assert.Equal(3, replayed.Count);

        Assert.IsType<TextPart>(replayed[0]);
        Assert.Equal("Hello from text part", ((TextPart)replayed[0]).Text);

        Assert.IsType<ToolInvocationPart>(replayed[1]);
        Assert.Equal("dotnet build", ((ToolInvocationPart)replayed[1]).ToolName);

        Assert.IsType<FilePart>(replayed[2]);
        Assert.Equal("Program.cs", ((FilePart)replayed[2]).Path);

        var emptyReplay = await partStore.ReplayAsync("nonexistent_session", CancellationToken.None);
        Assert.Empty(emptyReplay);

        var sessionIds = partStore.GetSessionIds();
        Assert.Single(sessionIds, id => id == sessionId);

        partStore.ForkSession(sessionId, "forked_session_03");
        var forked = await partStore.ReplayAsync("forked_session_03", CancellationToken.None);
        Assert.Equal(3, forked.Count);
    }

    [Fact]
    public async Task BTS_BOOT_04_AgenticLoopFullCycleWithFakeChatClient()
    {
        var tempDir = CreateTempDir();
        SetEnvVar("LTAI_WORKSPACE", tempDir);

        var fakeLts = new FakeLivingTreeSystem(
            "ACTION: done\nDETAIL: task complete");

        var hooks = new AgentHookPipeline();
        var assembler = new SystemPromptAssembler();

        var loop = new AgenticLoop(fakeLts, hooks, promptAssembler: assembler,
            logger: NullLogger<AgenticLoop>.Instance);

        var result = await loop.RunAsync("test task", CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(1, result.Iterations);
        Assert.NotEmpty(result.Steps);
        Assert.Single(loop.History);

        var step = loop.History[0];
        Assert.Equal(1, step.Iteration);
        Assert.Equal(LoopPhase.Done, step.Phase);
        Assert.Contains("task complete", step.Thinking);
        Assert.Equal("done", step.Action);
        Assert.Equal("task complete", step.Detail);
        Assert.NotNull(step.Observation);

        Assert.True(result.TotalMs >= 0);
        Assert.NotEmpty(result.FinalOutput);
        Assert.Contains("Task marked as complete", result.FinalOutput);

        var parts = loop.PartAssembler.Snapshot();
        Assert.NotEmpty(parts);
        Assert.Contains(parts, p => p is TextPart);
    }

    [Fact]
    public async Task BTS_BOOT_05_AgenticLoopReadPhaseReadsRealFiles()
    {
        var tempDir = CreateTempDir();
        SetEnvVar("LTAI_WORKSPACE", tempDir);

        var testFilePath = Path.Combine(tempDir, "HelloWorld.cs");
        await File.WriteAllTextAsync(testFilePath, "public class HelloWorld { public string Greet() => \"hello\"; }");

        var fakeLts = new FakeLivingTreeSystem(
            "ACTION: read\nDETAIL: \"HelloWorld.cs\"",
            "ACTION: done\nDETAIL: done after read");

        var hooks = new AgentHookPipeline();

        var loop = new AgenticLoop(fakeLts, hooks,
            logger: NullLogger<AgenticLoop>.Instance);

        var result = await loop.RunAsync("read the HelloWorld file", CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(2, result.Iterations);
        Assert.Equal(2, loop.History.Count);

        var readStep = loop.History[0];
        Assert.Equal("read", readStep.Action);
        Assert.Equal(LoopPhase.Read, readStep.Phase);

        var allParts = loop.PartAssembler.Snapshot();
        var fileParts = allParts.OfType<FilePart>().ToList();
        Assert.NotEmpty(fileParts);
        Assert.Contains(fileParts, fp => fp.Path.EndsWith("HelloWorld.cs", StringComparison.OrdinalIgnoreCase)
            && fp.Content != null && fp.Content.Contains("HelloWorld"));

        var textParts = allParts.OfType<TextPart>().ToList();
        Assert.NotEmpty(textParts);

        var toolParts = allParts.OfType<ToolInvocationPart>().ToList();
        Assert.NotEmpty(toolParts);
        Assert.Contains(toolParts, tp => tp.ToolName == "read");
    }

    [Fact]
    public async Task BTS_BOOT_06_AgenticLoopWithMemoryFilesAndSkills()
    {
        var tempDir = CreateTempDir();
        SetEnvVar("LTAI_WORKSPACE", tempDir);

        File.WriteAllText(Path.Combine(tempDir, "AGENTS.md"),
            "# Bootstrap Memory Agent\nrole: memory integration test\n");

        var l0Dir = Path.Combine(tempDir, "l0_atomic");
        Directory.CreateDirectory(l0Dir);
        await File.WriteAllTextAsync(Path.Combine(l0Dir, "btb_mem_s.md"), """
            # skill: btb_memory_skill
            domain: test/memory
            layer: 0
            version: 1.0.0
            intent: Memory integration test skill

            triggers:
              - pattern: "memory|记忆|context"
                weight: 1.0

            ## 步骤
            1. shell: echo memory_skill_activated

            ## 验证
            - must_contain: "memory"
            """);

        var skillLoader = new SkillLoader(NullLogger<SkillLoader>.Instance);
        var registry = new SkillRegistry(skillLoader, NullLogger<SkillRegistry>.Instance, tempDir);
        await registry.LoadAllAsync();

        var memoryDir = Path.Combine(tempDir, "memory");
        Directory.CreateDirectory(memoryDir);
        await File.WriteAllTextAsync(Path.Combine(memoryDir, "bootstrap_eia_memory.md"), """
            # memory: 环评项目启动经验
            id: eia_bootstrap_001
            domain: test/memory
            topic: EIA项目启动检查清单
            confidence: 0.92

            ## summary
            在EIA项目启动时，需要首先检查数据完整性、模型版本和参数配置。

            ## facts
            - EIA项目启动前应验证环境数据完整性 (0.95)
            - 模型版本号与配置参数应匹配 (0.90)
            - 预测前需要校准系数 (0.88)

            ## context
            适用于环境评价项目启动检查。确保数据源有效，模型参数正确。

            ## tags
            - [eia]
            - [bootstrap]
            - [启动检查]

            ## triggers
            - pattern: "EIA|环评|环境评价|bootstrap|启动"
              weight: 1.0
            """);

        var dbPath = Path.Combine(tempDir, "test_kg.db");
        using var kg = new KnowledgeGraph(NullLogger<KnowledgeGraph>.Instance, dbPath: dbPath);

        var memLoader = new MemoryFileLoader(NullLogger<MemoryFileLoader>.Instance);
        var memService = new MemoryFilesService(memLoader, kg, NullLogger<MemoryFilesService>.Instance, memoryDir);
        await memService.LoadAllAsync();

        Assert.Equal(1, memService.FileCount);

        var relevantMemories = memService.RetrieveRelevant("EIA项目启动检查", domain: "test/memory");
        Assert.NotEmpty(relevantMemories);
        Assert.Contains(relevantMemories, m => m.Name.Contains("环评"));

        var fakeLts = new FakeLivingTreeSystem(
            "ACTION: done\nDETAIL: EIA bootstrap complete");

        var hooks = new AgentHookPipeline();
        var assembler = new SystemPromptAssembler(registry);

        var loop = new AgenticLoop(fakeLts, hooks,
            promptAssembler: assembler,
            memoryFiles: memService,
            logger: NullLogger<AgenticLoop>.Instance);

        var result = await loop.RunAsync("执行EIA项目启动检查", CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Single(loop.History);

        var promptSentToLlm = fakeLts.LastQuery;
        Assert.Contains("[Memory]", promptSentToLlm);
        Assert.Contains("Relevant knowledge from memory files", promptSentToLlm);

        var parts = loop.PartAssembler.Snapshot();
        Assert.Contains(parts, p => p is TextPart);
    }
}

internal sealed class FakeLivingTreeSystem : ILivingTreeSystem
{
    private readonly Queue<string> _responses;

    public string LastQuery { get; private set; } = "";

    public FakeLivingTreeSystem(params string[] responses)
    {
        _responses = new Queue<string>(
            responses.Length > 0 ? responses : new[] { "FAKE: default response" });
    }

    public SystemMode Mode => throw new NotSupportedException();
    public bool DNAEnabled => throw new NotSupportedException();
    public IChatClient LLMClient => throw new NotSupportedException();
    public SystemGuardian Guardian => throw new NotSupportedException();
    public DNAStatus? DNAStatus => throw new NotSupportedException();
    public InputGovernor InputGovernor => throw new NotSupportedException();
    public ContextGovernor ContextGovernor => throw new NotSupportedException();
    public RoutingGovernor RoutingGovernor => throw new NotSupportedException();
    public TaskPipeline TaskPipeline => throw new NotSupportedException();

    public Task InitializeAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public async Task<string> ChatAsync(string query, CancellationToken cancellationToken = default)
    {
        LastQuery = query;
        await Task.CompletedTask;
        return _responses.Count > 0 ? _responses.Dequeue() : "ACTION: done\nDETAIL: queue exhausted";
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        string query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        var response = _responses.Count > 0 ? _responses.Dequeue() : "ACTION: done\nDETAIL: default";
        yield return response;
    }

    public async IAsyncEnumerable<string> StreamWithModelAsync(
        string query, string model,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield return _responses.Count > 0 ? _responses.Dequeue() : "ACTION: done\nDETAIL: default";
    }

    public Task<GovernorOutput> ProcessTypedAsync(
        GovernorInput input, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
