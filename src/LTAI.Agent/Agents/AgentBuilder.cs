using System.Collections.Concurrent;
using System.Text.Json;
using LTAI.Agent.Context;
using LTAI.Agent.Memory;
using LTAI.Agent.Services;
using LTAI.Agent.Tools;
using LTAI.Agent.Tools.Review;
using LTAI.Agent.Vector;
using LTAI.Agent.Workflows;
using LTAI.AI;
using LTAI.AI.Compaction;
using LTAI.Core.Configuration;
using LTAI.Core.Safety;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

#pragma warning disable MAAI001

namespace LTAI.Agent;

/// <summary>
/// ─────────────────────────────────────────────────────
///  TOOL REGISTRATION MATRIX (agent × tool category)
/// ─────────────────────────────────────────────────────
///                     Chat  Code  Math  Data  System  LLM  Writer  Frontend
///  Filesystem R/W       ✅    ✅    —     ✅     —      —     ✅      ✅
///  Shell/Exec           ✅    —     ✅    ✅     ✅     —     ✅      ✅
///  Search/Symbols       ✅    ✅    —     —     —      —     ✅      ✅
///  EIA                  ✅    —     —     ✅     ✅     —
///  Web                  ✅    —     —     ✅     —      —     ✅      ✅
///  Multimedia           ✅    ✅    —     ✅     ✅     —     ✅      ✅
///  Office               ✅    ✅    —     ✅     —      —
///  Memory               ✅    —     —     —     ✅     —     ✅      —
///  Git                  ✅    ✅    —     —     ✅     —     ✅      ✅
///  Plan/Flowchart       ✅    ✅    —     ✅     —      —     ✅      ✅
///  GIS/Weather/Trans    ✅    —     —     ✅     ✅     —     ✅      ✅
///  System/Network       ✅    —     —     —     ✅     —     ✅      —
///  Subagent             ✅    —     —     —     —      —     ✅      ✅
///  Task/Jobs            ✅    ✅    —     —     ✅     —     ✅      ✅
///  Container            ✅    —     ✅    ✅     ✅     —     —       ✅
/// ─────────────────────────────────────────────────────
///  Permission flags: canRead, canWrite, canList, canExec
///  Add new tools by inserting a new section below.
/// ─────────────────────────────────────────────────────
/// </summary>
internal static partial class AgentBuilder
{
    // Shared LSP manager across all agents (process-wide)
    private static readonly LanguageServer.LspLanguageManager s_lsp = new();
    internal static LanguageServer.LspLanguageManager GetLspManager() => s_lsp;

    // Shared caches across all agents (process-wide)
    private static readonly Caching.MmapCache s_mmapCache = new(new Caching.MmapCacheOptions
    {
        WatchDirectories = [Directory.GetCurrentDirectory()]
    });
    private static readonly Caching.MmapFileProvider s_mmapProvider = new(s_mmapCache);
    private static readonly Caching.WriteBuffer s_writeBuf = new(mmap: s_mmapCache);

    private static readonly ConcurrentDictionary<string, Task<IReadOnlyList<AITool>>> s_mcpToolsCache = new(StringComparer.OrdinalIgnoreCase);

    public static AIAgent BuildAgent(IServiceProvider sp, string name, string description,
        bool canRead, bool canWrite, bool canList, bool canExec,
        string? modelId = null, float? temperature = null, float? topP = null)
    {
        return BuildAgentImpl(sp, name, description, canRead, canWrite, canList, canExec, modelId, temperature, topP);
    }

    public static AIAgent BuildAgentImpl(IServiceProvider sp, string name, string description,
        bool canRead, bool canWrite, bool canList, bool canExec,
        string? modelId = null, float? temperature = null, float? topP = null, string? agentPrompt = null)
    {
        var ws = Directory.GetCurrentDirectory();
        var opts = sp.GetRequiredService<IOptions<LTAIOptions>>().Value;
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var llm = sp.GetRequiredService<IChatClient>();
        var log = loggerFactory.CreateLogger("Agent." + name);

        // P0.1: Wrap with progress guard to detect repeated tool calls
        var guardedLlm = new LTAI.Agent.Clients.ThinkingTagValidator(
            new LTAI.Agent.Clients.ProgressGuardChatClient(llm));

        var tools = new List<AITool>();
        var httpFactory = sp.GetRequiredService<IHttpClientFactory>();

        RegisterFileAndTextTools(tools, name, canRead, canWrite, canList, canExec, ws,
            canRead ? s_mmapProvider : null, canWrite ? s_writeBuf : null);
        RegisterSearchAndCodeAnalysisTools(tools, name, canRead, ws);
        RegisterWebTools(tools, name, httpFactory);
        RegisterMultimediaTools(tools, canRead, canExec, ws);
        RegisterDocumentTools(tools, canRead, canWrite, ws, sp);
        RegisterPlanAndDiagramTools(tools, name, httpFactory);
        RegisterChoiceAndSubagentTools(tools, name, sp, llm, ws);
        RegisterGitTools(tools, name, ws);
        RegisterReviewTools(tools, name, ws);
        RegisterSkillBankTools(tools, name);
        RegisterLspTools(tools, name);
        RegisterTaskTools(tools, name);
        RegisterIntegrationTools(tools, name, httpFactory);
        RegisterSystemAndJobTools(tools, name, canExec, canRead, canWrite, ws, sp);
        RegisterWorkflowTools(tools, name, sp);
        RegisterClusterAndDeepenTools(tools, name, sp);
        RegisterNewDomainTools(tools, name, canExec, canRead, canWrite, ws, sp);

        // EIA tools — in optional LTAI.Agent.Eia project (modularized)

        // Universal tools (all agents)
        {
            var qt = sp.GetRequiredService<LTAI.Agent.Tools.QuestionTool>();
            tools.Add(AIFunctionFactory.Create(qt.AskQuestions));
        }
        {
            var kat = sp.GetRequiredService<LTAI.Agent.Tools.KnowledgeAssetTool>();
            tools.Add(AIFunctionFactory.Create(kat.WikiCommit));
            tools.Add(AIFunctionFactory.Create(kat.WikiSearch));
            tools.Add(AIFunctionFactory.Create(kat.WikiList));
            tools.Add(AIFunctionFactory.Create(kat.WikiExtract));
        }

        // Safety guardrail (optional — skip for local dev to reduce latency)
        SafetyCoordinator? safety = null;
        if (!opts.AI.SkipSafetyChecks)
        {
            // P6 Steer: use lightweight model for safety when available (cheaper, faster).
            // Falls back to DeepSeek V4 Flash when steer is disabled or unavailable.
            var steerLlm = sp.GetKeyedService<IChatClient>("steer");
            IChatClient? safetyClient = null;
            if (steerLlm != null)
            {
                safetyClient = steerLlm;
                safety = new SafetyCoordinator(safetyClient, loggerFactory.CreateLogger<SafetyCoordinator>());
            }
            else
            {
                // 优雅降级：safety 模型未配置时不抛异常，跳过 safety
                // 优先级: opts.AI.Model → L1.Model → KnownKeys 默认
                var safetyModel = !string.IsNullOrEmpty(opts.AI.Model)
                    ? opts.AI.Model
                    : opts.AI.L1?.Model;

                if (string.IsNullOrEmpty(safetyModel))
                {
                    var dp = MultiProviderChatClient.DefaultProviders
                        .FirstOrDefault(p => string.Equals(p.name, opts.AI.DefaultProvider, StringComparison.OrdinalIgnoreCase));
                    if (dp.name != null) safetyModel = dp.model;
                }

                if (string.IsNullOrEmpty(safetyModel))
                {
                    log?.LogWarning("Safety agent: no model, skipping for agent '{Name}'", name);
                }
                else
                {
                    var safetyKey = opts.AI.ApiKeyEnv != null ? SecretManager.Get(opts.AI.ApiKeyEnv) ?? "" : "";
                    if (string.IsNullOrEmpty(safetyKey))
                    {
                        log?.LogWarning("Safety agent: no API key ({Env}), skipping for agent '{Name}'", opts.AI.ApiKeyEnv ?? "?", name);
                    }
                    else
                    {
                        safetyClient = OpenAIChatClientFactory.Create("https://api.deepseek.com/v1", safetyModel, safetyKey);
                    }
                }
            }
            if (safetyClient != null)
            {
                safety = new SafetyCoordinator(safetyClient, loggerFactory.CreateLogger<SafetyCoordinator>());
            }
            else
            {
                log?.LogWarning("Safety agent: client not available, skipping safety for agent '{Name}'", name);
            }
        }

        // LTAI does NOT use MAF's ShellEnvironmentProvider:
        // - It starts a persistent PowerShell process via LocalShellExecutor, which hangs
        //   on Windows .NET 10 preview during InitializeAsync (60+ seconds).
        // - LTAI has its own EnvironmentProvider (line below) + SafeShellTool + WasmtimeSandbox,
        //   so MAF's auto shell-context probing is redundant.
        // The variable is kept as null so AIContextProviders can be updated in one place.

        LTAI.Core.Configuration.UsageTracker.SetContextWindowSize(opts.AI.ContextWindowSize);
        LTAI.Agent.Tools.RipgrepDetector.RipgrepDownloadUrl = opts.Mirrors.RipGrepUrl;
        LTAI.Agent.Tools.SkillScriptRunner.SystemPathFallback = opts.Security.SystemPathFallback;
        LTAI.Agent.Tools.SafeShellTool.SystemPathFallback = opts.Security.SystemPathFallback;
        LTAI.AI.LocalEmbedder.ModelBaseUrl = opts.Mirrors.ModelBaseUrl;
        // P6 Steer: use lightweight model as verifier when available (saves ~LLM call per compaction).
        // The summarizer is still the main LLM (needs full context window); the verifier
        // only does a hallucination check (short output), which the steer model handles well.
        var steerLlmVerify = sp.GetKeyedService<IChatClient>("steer");
        var compaction = new CompactionProvider(
            new PipelineCompactionStrategy(
                new ContextWindowCompactionStrategy(opts.AI.ContextWindowSize, opts.AI.MaxTokens),
                new VerifiedSummarizationStrategy(
                    summarizer: llm,
                    verifier: steerLlmVerify ?? llm,
                    trigger: CompactionTriggers.TokensExceed(opts.AI.ContextWindowSize),
                    minimumPreservedGroups: 2)
            ), loggerFactory: loggerFactory);

        // KB & Code graphs for context augmentation (SQLite FTS5 + CTE)
        var kbGraph = sp.GetRequiredService<KbGraph>();
        var codeGraph = sp.GetRequiredService<CgGraph>();
        var codeChunkIndex = sp.GetRequiredService<LTAI.Agent.Indexing.CodeChunkIndex>();

        // Wasmtime sandbox: WASM-based code execution with WASI capability restrictions.
        // Recommended over Hyperlight (v0.4, pre-1.0) for general-purpose sandboxing.
        // See sandbox-roadmap MEMORY.md for the full evaluation.
        var wasmtimeSandbox = new WasmtimeSandbox(ws, loggerFactory.CreateLogger<WasmtimeSandbox>());

            // Skills provider: loads SKILL.md from skills/ (框架自动去重合并)
        // P3 APM: also loads from .agents/skills/ (APM-managed skills)
        var apmSkillsDir = Path.Combine(ws, ".agents", "skills");
        var skillsDir = new[] {
            Path.Combine(AppContext.BaseDirectory, "skills"),
            Path.Combine(Directory.GetCurrentDirectory(), "skills"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "skills"),
        }.FirstOrDefault(Directory.Exists) ?? Path.Combine(Directory.GetCurrentDirectory(), "skills");
        Directory.CreateDirectory(skillsDir);
        var skillDirs = new List<string> { skillsDir };
        if (Directory.Exists(apmSkillsDir))
            skillDirs.Add(apmSkillsDir);

        var skillsBuilder = new Microsoft.Agents.AI.AgentSkillsProviderBuilder()
            .UseFileSkills([.. skillDirs]);

        if (opts.SkillsUrls is { Length: > 0 })
            skillsBuilder = skillsBuilder.UseSource(
                new AgentUrlSkillsSource(opts.SkillsUrls, httpFactory.CreateClient()));

        var skillsProvider = skillsBuilder
            .UseFileScriptRunner(LTAI.Agent.Tools.SkillScriptRunner.RunAsync)
            .UseOptions(o =>
            {
                o.ScriptApproval = true;
                 o.SkillsInstructionPrompt =
                    """
                    你拥有领域专精技能（skills），每个技能采用三层渐进式披露（Three-Layer Progressive Disclosure）：

                    <available_skills>
                    {skills}
                    </available_skills>

                    ### 三层加载策略
                    - **L1（上表中可见）**: name + description → 判断技能是否相关，零成本
                    - **L2**: 加载后 `## 概要` 节 → 了解核心步骤和关键参数（load_skill 后自动加载）
                    - **L3**: 完整技能正文 → 包含示例、边界情况、输出格式（继续阅读即可）

                    当任务匹配某个技能的领域时：
                    1. 先用 description 判断相关度（L1）
                    2. 用 `load_skill` 加载技能（示例：load_skill("code-review")），阅读 `## 概要` 节（L2）
                    3. 如需深入了解，继续阅读后续章节（L3）
                    4. 如果技能声明了 allowedTools，请优先使用这些工具
                    {resource_instructions}
                    {script_instructions}
                    只加载所需技能，不要全部加载。
                    """;
            })
            .Build();

        // ── Plan mode 特殊处理 ──
        var isPlanMode = name == "LTAI-Plan";
        if (isPlanMode)
        {
            tools.Clear();
            tools.Add(AIFunctionFactory.Create(LTAI.Agent.Tools.PlanTools.PlanExit));
            if (canRead)
            {
                var planFs = new FileSystemTools(ws, s_mmapProvider, s_writeBuf);
                tools.Add(AIFunctionFactory.Create((string path) => planFs.ReadFileContent(path), "ReadFileContent", "Read a file"));
                tools.Add(AIFunctionFactory.Create(planFs.Glob));
                tools.Add(AIFunctionFactory.Create(planFs.ListFiles));
                tools.Add(AIFunctionFactory.Create(planFs.DirectoryTree));
            }
            var planSearch = new SearchTools(ws);
            tools.Add(AIFunctionFactory.Create(planSearch.SearchContent));
            tools.Add(AIFunctionFactory.Create(planSearch.SearchFiles));
        }

        // Cross-session long-term memory: 7-layer memory palace (PalaceStore + AIContextProviders).
        // Hierarchical Wing→Room→Drawer architecture. Each layer has a fixed token budget
        // (L0+L1 ≈ 900t always loaded).
        var embedder = sp.GetRequiredService<LTAI.AI.EmbeddingClient>();
        var palaceDb = Path.Combine(opts.DataDirectory, "palace.db");
        WingClassifier.LlmClassifier = (text) => null;

        var palaceStore = new LTAI.Agent.Memory.PalaceStore(embedder, palaceDb,
            loggerFactory.CreateLogger<LTAI.Agent.Memory.PalaceStore>());

        // L0: Identity (~100t, always loaded). Reads from config or identity.txt.
        var identityPath = Path.Combine(AppContext.BaseDirectory, "identity.txt");
        var identityText = File.Exists(identityPath) ? File.ReadAllText(identityPath).Trim() : "";
        if (string.IsNullOrWhiteSpace(identityText))
            identityText = opts.AI.DefaultProvider ?? "";

        RegisterMemoryTools(tools, canWrite, palaceStore, ws);

        // MCP (Model Context Protocol) client tools: lazy-loaded on first invocation.
        // Defers spawning MCP child processes from DI resolution to actual tool use.
        if (!isPlanMode)
        {
            var mcpFactory = sp.GetRequiredService<LTAI.Agent.Mcp.McpClientFactory>();
            var mcpTask = s_mcpToolsCache.GetOrAdd(name, _ => mcpFactory.GetToolsAsync(opts.Mcp));
            if (mcpTask.IsCompletedSuccessfully)
            {
                foreach (var mcpTool in mcpTask.Result)
                {
                    if (!canRead) continue;
                    var mn = mcpTool.Name.ToLowerInvariant();
                    if (mn.Contains("write") || mn.Contains("create") || mn.Contains("delete") || mn.Contains("upload"))
                    { if (!canWrite) continue; }
                    if (mn.Contains("shell") || mn.Contains("command") || mn.Contains("exec") || mn.Contains("process"))
                    { if (!canExec) continue; }
                    tools.Add(mcpTool);
                }
            }
        }

        // Semantic code search tool (cocoindex-inspired AST chunk index).
        // Available for all canRead agents (not in Plan Mode — no AST index in read-only mode).
        if (canRead && !isPlanMode)
            tools.Add(AIFunctionFactory.Create(codeChunkIndex.SemanticCodeSearch));

        // P3: APM / MCP Registry 包管理工具 — 所有 agent 可用（需安装 apm CLI）
        {
            var pkg = new LTAI.Agent.Tools.PackageManagerTools();
            tools.Add(AIFunctionFactory.Create(pkg.PkgSearch));
            tools.Add(AIFunctionFactory.Create(pkg.PkgInstall));
            tools.Add(AIFunctionFactory.Create(pkg.PkgList));
        }

        RegisterDebugTools(tools, name, sp);

        // 去重：同名工具保留第一个，记录警告
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = tools.Count - 1; i >= 0; i--)
        {
            if (!seenNames.Add(tools[i].Name))
            {
                log?.LogWarning("工具名重复已被移除: {Name}", tools[i].Name);
                tools.RemoveAt(i);
            }
        }

        AIAgent agent = guardedLlm.AsHarnessAgent(
            options: new HarnessAgentOptions
            {
                MaxContextWindowTokens = opts.AI.ContextWindowSize,
                MaxOutputTokens = opts.AI.MaxTokens,
                Name = name,
                // P10.2: Chinese harness instructions replacing the default English
                // block. Default is Chinese; switches to English when OS language is en-US.
                // Uses LTAI.Core.I18n.Locale for culture-aware string selection.
                HarnessInstructions = isPlanMode
                    ? null  // plan mode keeps the default
                    : AgentPromptBuilder.AppendAgentPrompt(AgentPromptBuilder.BuildSystemPrompt(), agentPrompt),
                Description = isPlanMode
                    ? AgentPromptBuilder.BuildPlanModePrompt()
                    : AgentPromptBuilder.BuildAgentDescription(name, description),
                ChatOptions = new ChatOptions
                {
                    Temperature = temperature ?? (float)opts.AI.Temperature,
                    TopP = topP ?? 0.95f,
                    MaxOutputTokens = opts.AI.MaxTokens,
                    Tools = tools,
                    ModelId = modelId,
                },
                // F2: cap at 200 messages to prevent unbounded memory growth
                ChatHistoryProvider = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions
                {
                    ChatReducer = new MaxMessageCountReducer(200),
                    ReducerTriggerEvent = InMemoryChatHistoryProviderOptions.ChatReducerTriggerEvent.AfterMessageAdded,
                }),
                // 7-layer memory palace: L0 identity → L1 essential → L3 on-demand → L4 deep → L6 diary.
                // Placed after tool-filtering providers (Tool RAG, Skill ranking) and before the final
                // instruction providers so memories augment the conversation context.
                // Tool RAG: 动态工具召回（放第一个）→ L1 Skill Evolution Ranking
                 // P12.2: inject ToolEmbeddingCache so 80+ tool description embeddings are
                // batched + persisted. Cold start 0 ONNX calls after first run.
                AIContextProviders = AgentContextProviderBuilder.Build(sp, loggerFactory, name, identityText,
                    compaction, kbGraph, codeGraph, codeChunkIndex, wasmtimeSandbox,
                    embedder, palaceStore, identityText, modelId, skillsProvider, safety),

                 // ── Disable MAF defaults LTAI doesn't need ────────────────────
                // LTAI uses its own 7-layer memory palace (PalaceStore + AIContextProviders).
                DisableFileMemory = true,
                // MAF FileAccessProvider provides sandboxed file storage (.sandbox/ dir).
                // LTAI also registers FileSystemTools for direct workspace file operations.
                FileAccessStore = new FileSystemAgentFileStore(
                    Path.Combine(ws, ".sandbox", "file-access")),
                // MAF AgentModeProvider: plan (interactive) / execute (autonomous) / chat (free-form).
                // LTAI's own edit-mode (review/auto) is separate — controlled via /mode command.
                AgentModeProviderOptions = new AgentModeProviderOptions
                {
                    Modes =
                    [
                        new("plan", "交互式规划模式 — 分析需求、分解任务、制定计划，用户确认后执行"),
                        new("execute", "自主执行模式 — 执行已批准的计划，不向用户提问"),
                        new("chat", "自由对话模式 — 无约束的日常问答，不需要计划"),
                    ],
                    DefaultMode = "chat",
                },
                // LTAI doesn't surface web search to its agents.
                DisableWebSearch = true,
                // LTAI has its own AgentSkillsProvider (the one passed above), pre-configured
                // with script approval + custom instructions. Don't double-register MAF's.
                DisableAgentSkillsProvider = true,

                // MAF ToolApprovalAgent handles tool-level approval centrally
                // (replacing per-tool confirm parameters).
                // Use the per-agent source name so /health and DevUI can identify spans.
                OpenTelemetrySourceName = $"LTAI.{name}",

                // P10.3: bound function-invocation iterations. Default is 40; bump to 50
                // to give multi-agent BackgroundAgents delegation room to converge (the
                // "StartTask → WaitForFirstCompletion → GetResults" loop counts as
                // several iterations per logical task).
                MaximumIterationsPerRequest = 50,

                // P10.0: BackgroundAgents delegation. Every LTAI agent can asynchronously
                // delegate work to its sibling agents (LTAI-Chat → LTAI-Math for numerical
                // work, LTAI-Code for code execution, etc.) via the 6 BackgroundAgents_*
                // tools auto-injected by MAF. Sister agents are wrapped in
                // LazyAIAgentProxy to break the circular dependency at HarnessAgent
                // construction time (Name/Description come from the static AgentRegistry;
                // RunAsync/RunStreamingAsync resolve the actual agent on first call, by
                // which time the agent graph is fully built).
                BackgroundAgents = AgentRegistry.LoadAll()
                    .Where(d => !string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)
                                && !string.Equals(d.Name, "router", StringComparison.OrdinalIgnoreCase))
                    .Select(d => (AIAgent)new LazyAIAgentProxy(sp, d.Name))
                    .ToList(),
                BackgroundAgentsProviderOptions = new BackgroundAgentsProviderOptions
                {
                    Instructions = """
                    ## BackgroundAgents — 异步委派
                    你可以将任务**异步委派**给以下 sibling agents。每个 agent 在自己 session 中独立并发执行。

                    ### 典型用法
                    1. `BackgroundAgents_StartTask(agentName, goal)` → 启动1个或多个后台任务（不阻塞）
                    2. `BackgroundAgents_WaitForFirstCompletion()` → 等待任意一个完成后取结果
                    3. `BackgroundAgents_GetTaskResults(id)` → 取出已完成的结果
                    4. 回复用户前**必须**等待所有 outstanding tasks 完成
                    5. 取完结果后调用 `BackgroundAgents_ClearCompletedTask(id)` 释放内存

                    ### 适用场景
                    - **并发搜索**：同时让 LTAI-Code 分析代码 + LTAI-Data 查数据 + LTAI-Writer 写文档
                    - **分步委派**：LTAI-Math 算数值 → 结果传给 LTAI-Code 实现 → 再传给 LTAI-Writer 写说明
                    - **异步后台**：长耗时操作（编译、测试、数据迁移）交给专用 agent，不阻塞主对话

                    ### 工具列表
                    - `BackgroundAgents_StartTask` — 启动后台任务（返回 taskId，不阻塞）
                    - `BackgroundAgents_WaitForFirstCompletion` — 等待任意一个任务完成
                    - `BackgroundAgents_GetTaskResults` — 取出已完成任务的文本结果
                    - `BackgroundAgents_GetAllTasks` — 列出所有任务（id/状态/描述/agent 名）
                    - `BackgroundAgents_ContinueTask` — 向已完成任务的 session 追加输入
                    - `BackgroundAgents_ClearCompletedTask` — 释放已完成任务的 session

                    {background_agents}
                    """,
                },
            });

        // LTAI's outer-most logging wrapper — captures the final agent response and the
        // pre-decorator inner-agent state. HarnessAgent's own OpenTelemetryAgent / ToolApprovalAgent
        // sit just inside this, so the log entry is recorded after both have transformed the run.
        agent = new LoggingAgent(agent, log!);
        return agent;
    }
}

/// <summary>
/// P0: Minimal no-op AIAgent used when the real agent fails to build.
/// Returns a static error message so the caller can surface the failure gracefully.
/// </summary>
internal sealed class FallbackAgent : AIAgent
{
    public FallbackAgent(string name, string description)
    {
        Name = name;
        Description = description;
    }

    public override string? Name { get; }
    public override string? Description { get; }

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken ct)
        => new(new MinimalAgentSession());

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session, JsonSerializerOptions? jsonOptions, CancellationToken ct)
        => new(JsonSerializer.SerializeToElement(new { fallback = true }));

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement state, JsonSerializerOptions? jsonOptions, CancellationToken ct)
        => new(new MinimalAgentSession());

    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, CancellationToken ct)
        => Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant,
            $"[Agent '{Name}' unavailable — build failed. Check logs for details.]")));

    protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, CancellationToken ct)
        => AsyncEnumerable.Repeat(new AgentResponseUpdate(ChatRole.Assistant,
            $"[Agent '{Name}' unavailable — build failed. Check logs for details.]"), 1);
}

file sealed class MinimalAgentSession : AgentSession
{
    public MinimalAgentSession() : base(new AgentSessionStateBag()) { }
}

// F2: caps InMemoryChatHistoryProvider message count to prevent unbounded growth
internal sealed class MaxMessageCountReducer : IChatReducer
{
    private readonly int _maxCount;
    public MaxMessageCountReducer(int maxCount) => _maxCount = Math.Max(10, maxCount);

    public Task<IEnumerable<ChatMessage>> ReduceAsync(
        IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        if (list.Count <= _maxCount)
            return Task.FromResult<IEnumerable<ChatMessage>>(list);

        // Keep the system prompt (first message) and the most recent messages
        var system = list.FirstOrDefault(m => m.Role == ChatRole.System);
        var recent = list.TakeLast(_maxCount - (system != null ? 1 : 0)).ToList();
        if (system != null)
            recent.Insert(0, system);

        return Task.FromResult<IEnumerable<ChatMessage>>(recent);
    }
}
